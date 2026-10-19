/// Feature 015 (FR-017, T015): the operator commands `fediverse-status` and `series-handle`, run through the CLI map as
/// `dotnet run -- <command>` does.
module FrunchyBackend.Tests.FediverseOperationsTests

open System
open System.Net
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers

let private sample (federated: bool) : Task<TestApp> =
    task {
        let! app = if federated then newFederatedApp () else newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        return app
    }

/// Run a command through the CLI map; the app's own federation settings are the command's
let private cli (app: TestApp) (command: string) (args: string list) : Task<int> =
    let env: Program.CliEnv =
        { Db = app.Db
          Http = httpFactory app
          Mal = MalClient.readSettings (app.Services.GetRequiredService<IConfiguration>())
          Clock = TimeProvider.System
          Federated = false
          Federation = app.Services.GetRequiredService<Federation.FederationSettings>()
          RootOperator = None }

    Program.cliCommands[command] env args

let private twoSeries (app: TestApp) =
    task {
        let! ids = Db.dump app.Db "SELECT id FROM cr_series ORDER BY id LIMIT 2"
        return ids[0], ids[1]
    }

let private handleOf (app: TestApp) (seriesId: string) =
    Db.dump app.Db $"SELECT handle FROM series_handles WHERE series_id = '{seriesId}'"

// ---------------------------------------------------------------------------
// series-handle
// ---------------------------------------------------------------------------

[<Fact>]
let ``series-handle sets a handle the actor lookup then answers to`` () =
    task {
        use! app = sample true
        let! first, _ = twoSeries app

        let! code = cli app "series-handle" [ first; "My-Series" ]
        Assert.Equal(0, code)
        let! stored = handleOf app first
        Assert.Equal<string list>([ "my-series" ], stored)

        // Megáfono's lookup finds the series by the new handle
        use request = new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, "/megafono/actors?handle=my-series")
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {FakeMegafono.Token}") |> ignore
        use! response = app.Client.SendAsync request
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! body = response.Content.ReadAsStringAsync()
        Assert.Contains($"\"series-{first}\"", body)

        // and it can be changed again until the series federates; the same handle is a no-op
        let! again = cli app "series-handle" [ first; "my-series" ]
        Assert.Equal(0, again)
        let! changed = cli app "series-handle" [ first; "another_name" ]
        Assert.Equal(0, changed)
        let! after = handleOf app first
        Assert.Equal<string list>([ "another_name" ], after)
    }

[<Fact>]
let ``series-handle works with federation off, and replaces a handle made from the slug`` () =
    task {
        use! app = sample false
        let! first, _ = twoSeries app
        let! made =
            task {
                use connection = Database.getConnection app.Db
                do! connection.OpenAsync()
                return! Fediverse.ensureSeriesHandle connection first
            }

        let! code = cli app "series-handle" [ first; "corrected" ]
        Assert.Equal(0, code)
        let! stored = handleOf app first
        Assert.Equal<string list>([ "corrected" ], stored)
        Assert.NotEqual<string>("corrected", made)
    }

[<Fact>]
let ``series-handle refuses what breaks the handle rule, bad ids, and the wrong number of arguments`` () =
    task {
        use! app = sample true
        let! first, _ = twoSeries app

        for bad in [ "-lead"; "trail-"; "has.dot"; "with space"; "Ünïcode"; ""; String('a', 31) ] do
            let! code = cli app "series-handle" [ first; bad ]
            Assert.True((code = 1), $"'{bad}' gave {code}")

        let! badId = cli app "series-handle" [ "not an id"; "fine" ]
        Assert.Equal(1, badId)
        let! lower = cli app "series-handle" [ "abcdef123"; "fine" ]
        Assert.Equal(1, lower)
        let! noArgs = cli app "series-handle" []
        Assert.Equal(1, noArgs)
        let! oneArg = cli app "series-handle" [ first ]
        Assert.Equal(1, oneArg)
        let! three = cli app "series-handle" [ first; "fine"; "extra" ]
        Assert.Equal(1, three)

        let! none = Db.count app.Db "series_handles"
        Assert.Equal(0L, none)
    }

[<Fact>]
let ``series-handle: an unknown series is exit 2`` () =
    task {
        use! app = sample true
        let! code = cli app "series-handle" [ "ZZZZZZZZZ"; "fine" ]
        Assert.Equal(2, code)
        let! none = Db.count app.Db "series_handles"
        Assert.Equal(0L, none)
    }

[<Fact>]
let ``series-handle: a handle another series or a user has is exit 4 and nothing changes`` () =
    task {
        use! app = sample true
        let! first, second = twoSeries app

        let! ok = cli app "series-handle" [ first; "taken-one" ]
        Assert.Equal(0, ok)

        let! otherSeries = cli app "series-handle" [ second; "taken-one" ]
        Assert.Equal(4, otherSeries)
        let! username = cli app "series-handle" [ second; "animenerd" ]
        Assert.Equal(4, username)

        let! none = handleOf app second
        Assert.Empty none
        let! firstStill = handleOf app first
        Assert.Equal<string list>([ "taken-one" ], firstStill)
    }

[<Fact>]
let ``series-handle: a series whose thread is published has federated, so it is exit 3`` () =
    task {
        use! app = sample true
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND sea.season_number IS NOT NULL AND e.title IS NOT NULL"
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Hello" false

        // queued but not published: the handle can still change
        let! before = cli app "series-handle" [ ep.Series; "early-handle" ]
        Assert.Equal(0, before)

        let! _ = drainOutbox app
        let! refused = cli app "series-handle" [ ep.Series; "late-handle" ]
        Assert.Equal(3, refused)
        let! stored = handleOf app ep.Series
        Assert.Equal<string list>([ "early-handle" ], stored)

        // saying the handle it already has is harmless
        let! same = cli app "series-handle" [ ep.Series; "early-handle" ]
        Assert.Equal(0, same)
    }

// ---------------------------------------------------------------------------
// fediverse-status
// ---------------------------------------------------------------------------

[<Fact>]
let ``fediverse-status reports the settings without secrets, the outbox counts and the oldest waiting item`` () =
    task {
        use! app = sample true
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND sea.season_number IS NOT NULL AND e.title IS NOT NULL"
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Hello" false

        // two queued; Megáfono down once, so the first has an attempt and an error
        app.Megafono.FailNext 1
        let! _ = drainOutbox app

        let! s = FediverseStatus.status app.Db
        let settings = app.Services.GetRequiredService<Federation.FederationSettings>()
        let lines = FediverseStatus.render settings s
        let text = String.Join("\n", lines)

        Assert.Contains("FEDERATION  enabled", lines)
        Assert.Contains("DOMAIN      frunchy.test", lines)
        Assert.Contains("MEGAFONO    http://megafono.test/", lines)
        let countLine (state: string) (kind: string) =
            lines |> List.exists (fun l -> l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) = [| state; kind; "1" |])

        Assert.True(countLine "pending" "ensure_thread", text)
        Assert.True(countLine "pending" "publish_comment", text)

        let oldest = s.Oldest.Value
        Assert.Equal("ensure_thread", oldest.Kind)
        Assert.Equal(1, oldest.Attempts)
        Assert.Equal(Some "503 app_unavailable", oldest.LastError)
        Assert.Contains("OLDEST     #", text)
        Assert.Contains("ensure_thread", text)
        Assert.Contains("ERROR      503 app_unavailable", lines)

        // no secret anywhere
        Assert.DoesNotContain(FakeMegafono.Token, text)
        Assert.DoesNotContain(FakeMegafono.WebhookSecret, text)

        // once drained, nothing waits and the counts say done
        do! Db.execute app.Db "UPDATE fediverse_outbox SET next_attempt_at = now()"
        let! _ = drainOutbox app
        let! after = FediverseStatus.status app.Db
        Assert.Equal(None, after.Oldest)
        Assert.Contains("OLDEST     nothing waiting", FediverseStatus.render settings after)

        let! code = cli app "fediverse-status" []
        Assert.Equal(0, code)
    }

[<Fact>]
let ``fediverse-status says so when federation is off, and with an empty outbox`` () =
    task {
        use! app = sample false
        let! s = FediverseStatus.status app.Db
        let lines = FediverseStatus.render Federation.Disabled s
        Assert.Equal<string list>([ "FEDERATION  disabled"; "OUTBOX"; "  (empty)"; "OLDEST     nothing waiting" ], lines)

        let! code = cli app "fediverse-status" []
        Assert.Equal(0, code)
    }

[<Fact>]
let ``fediverse-status never prints credentials that sit in the Megafono address`` () =
    let values: Federation.FederationValues =
        { Domain = "frunchy.test"
          MegafonoUrl = Uri "http://user:hunter2@megafono.test:8081/base?token=abc"
          Token = "t"
          WebhookSecret = "s" }

    let text = String.Join("\n", FediverseStatus.render (Federation.Enabled values) { Counts = []; Oldest = None })
    Assert.Contains("MEGAFONO    http://megafono.test:8081/base", text)
    Assert.DoesNotContain("hunter2", text)
    Assert.DoesNotContain("token=abc", text)
