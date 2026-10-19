/// SC-001: the CLI commands and the shared `MalActions` (what the review pages call) leave identical
/// rows, dubs included; the state checks refuse what they must without writing.
module FrunchyBackend.Tests.MalActionsParityTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.MalActions
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// The Weakest Tamer's original season and its English dub (the dub follows the original)
let private tamerSeason = "GRK5CNJ14"
let private tamerDub = "GYDQCGK5Q"
let private frierenSeries = "GG5H5XQX4"

let private enriched () =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        app.Mal.Clear()
        return app
    }

let private malSettings (app: TestApp) =
    FrunchyBackend.Infrastructure.MalClient.readSettings (ServiceProviderServiceExtensions.GetRequiredService<IConfiguration>(app.Services))

/// Run a command through the CLI map, as `dotnet run -- <command>` does
let private cli (app: TestApp) (command: string) (args: string list) : Task<int> =
    let env: Program.CliEnv =
        { Db = app.Db
          Http = httpFactory app
          Mal = malSettings app
          Clock = TimeProvider.System
          Federated = false; Federation = FrunchyBackend.Infrastructure.Federation.Disabled; RootOperator = None; Profiles = FrunchyBackend.Infrastructure.Profiles.ProfileSettings(FrunchyBackend.Infrastructure.Profiles.Private) }

    Program.cliCommands[command] env args

let private cliOk (app: TestApp) (command: string) (args: string list) : Task<unit> =
    task {
        let! code = cli app command args
        Assert.Equal(0, code)
    }

/// Every column of cr_season_matches that a decision sets, times reduced to "set or not"
let private state (app: TestApp) =
    task {
        let! rows =
            Db.dump
                app.Db
                """SELECT season_id || ' ' || status || ' ' || coalesce(mal_id::text, '-') || ' ' || coalesce(method, '-')
                          || ' ' || coalesce(shared_from, '-') || ' ' || coalesce(candidates::text, '-') || ' ' || coalesce(reason, '-')
                          || ' attempts=' || attempts || ' next=' || (next_attempt_at IS NOT NULL) || ' claimed=' || (claimed_at IS NOT NULL)
                          || ' requested=' || (requested_at IS NOT NULL) || ' decided=' || (decided_at IS NOT NULL)
                          || ' error=' || coalesce(last_error, '-')
                   FROM cr_season_matches ORDER BY season_id"""

        let! searches = Db.scalar<int64> app.Db "SELECT count(*) FROM mal_searches" []
        let! refresh = Db.scalar<int64> app.Db "SELECT count(*) FROM mal_anime WHERE refresh_requested_at IS NOT NULL" []
        return rows, searches, refresh
    }

let private expectOk (action: Task<Result<'a, ActionError>>) : Task<'a> =
    task {
        match! action with
        | Ok v -> return v
        | Error e -> return failwith $"expected Ok, got {e}"
    }

/// Two fresh apps in the same state; `viaCli` acts on one, `direct` on the other; the rows must match
let private sameRows (prepare: TestApp -> Task<unit>) (viaCli: TestApp -> Task<unit>) (direct: TestApp -> Task<unit>) (inspect: TestApp -> TestApp -> Task<unit>) =
    task {
        use! a = enriched ()
        use! b = enriched ()
        do! prepare a
        do! prepare b
        let! before = state a
        let! beforeB = state b
        Assert.Equal(before, beforeB)
        do! viaCli a
        do! direct b
        let! afterA = state a
        let! afterB = state b
        Assert.Equal(afterA, afterB)
        Assert.NotEqual(before, afterA) // the action did something
        do! inspect a b
    }

let private noPreparation (_: TestApp) = Task.FromResult()

let private setByHand (app: TestApp) =
    task {
        routeFull app.Mal (spec 77777 "Another Tamer" None "TV" "2024-01-01")
        do! cliOk app "mal-set" [ tamerSeason; "77777" ]
        app.Mal.Clear()
    }

[<Fact>]
let ``set: the CLI and the shared action leave identical rows, the dub following`` () =
    task {
        let route (app: TestApp) = routeFull app.Mal (spec 77777 "Another Tamer" None "TV" "2024-01-01")

        do!
            sameRows
                (fun app -> task { route app })
                (fun app -> cliOk app "mal-set" [ tamerSeason; "77777" ])
                (fun app ->
                    task {
                        let! title = expectOk (MalActions.setMatch app.Db (httpFactory app) TimeProvider.System (malSettings app) Any tamerSeason 77777)
                        Assert.Equal("Another Tamer", title)
                    })
                (fun a b ->
                    task {
                        let! dub = matchOf b.Db tamerDub
                        Assert.Equal($"{tamerDub} matched 77777 dub {tamerSeason}", dub)
                        Assert.Equal(a.Mal.Count "details:77777", b.Mal.Count "details:77777")
                    })
    }

[<Fact>]
let ``none: the CLI and the shared action leave identical rows, the dub following`` () =
    task {
        do!
            sameRows
                noPreparation
                (fun app -> cliOk app "mal-none" [ tamerSeason ])
                (fun app -> expectOk (MalActions.setNone app.Db TimeProvider.System Any tamerSeason))
                (fun _ b ->
                    task {
                        let! original = matchOf b.Db tamerSeason
                        let! dub = matchOf b.Db tamerDub
                        Assert.Equal($"{tamerSeason} no_match - manual -", original)
                        Assert.Equal($"{tamerDub} no_match - dub {tamerSeason}", dub)
                    })
    }

[<Fact>]
let ``retry: the CLI and the shared action leave identical rows and drop the same searches`` () =
    task {
        do!
            sameRows
                noPreparation
                (fun app -> cliOk app "mal-retry" [ frierenSeries ])
                (fun app ->
                    task {
                        let! counts = expectOk (MalActions.retry app.Db Any frierenSeries)
                        Assert.Equal((1, 0), counts)
                    })
                (fun a _ ->
                    task {
                        let! _, searches, _ = state a
                        let! total = Db.scalar<int64> a.Db "SELECT count(*) FROM mal_searches WHERE query LIKE 'Frieren%'" []
                        Assert.Equal(0L, total)
                        Assert.True(searches > 0L, "the other series' searches stay")
                    })
    }

[<Fact>]
let ``auto: the CLI and the shared action leave identical rows, the dub following`` () =
    task {
        do!
            sameRows
                setByHand
                (fun app -> cliOk app "mal-auto" [ tamerSeason ])
                (fun app -> expectOk (MalActions.backToAutomatic app.Db Manual tamerSeason))
                (fun a _ ->
                    task {
                        let! original = matchOf a.Db tamerSeason
                        let! dub = matchOf a.Db tamerDub
                        Assert.Equal($"{tamerSeason} pending - - -", original)
                        Assert.Equal($"{tamerDub} pending - - -", dub)
                        let! queued = Db.scalar<int64> a.Db $"SELECT count(*) FROM cr_season_matches WHERE season_id IN ('{tamerSeason}', '{tamerDub}') AND requested_at IS NOT NULL AND attempts = 0" []
                        Assert.Equal(2L, queued)

                        // the matcher decides again, and the dub still shares the original
                        let! _ = enrichAll a
                        let! again = matchOf a.Db tamerSeason
                        Assert.StartsWith($"{tamerSeason} matched", again)
                        Assert.DoesNotContain("manual", again)
                    })
    }

[<Fact>]
let ``mal-auto exits 2 for an unknown season, 3 for a season that is not manual, 1 for usage`` () =
    task {
        use! app = enriched ()
        let! unknown = cli app "mal-auto" [ "NOPE" ]
        Assert.Equal(2, unknown)
        let! before = state app
        let! notManual = cli app "mal-auto" [ tamerSeason ]
        Assert.Equal(3, notManual)
        let! after = state app
        Assert.Equal(before, after)
        let! usage = cli app "mal-auto" []
        Assert.Equal(1, usage)
    }

/// A season nobody decided (ambiguous, no match or failed) and one that is matched automatically
let private pickSeasons (app: TestApp) =
    task {
        let! open' = Db.scalar<string> app.Db "SELECT season_id FROM cr_season_matches WHERE status IN ('ambiguous', 'no_match', 'failed') AND method IS DISTINCT FROM 'manual' ORDER BY season_id LIMIT 1" []
        let! matched = Db.scalar<string> app.Db "SELECT season_id FROM cr_season_matches WHERE status = 'matched' AND method IS DISTINCT FROM 'manual' ORDER BY season_id LIMIT 1" []
        return open', matched
    }

let private expectNotOpen (action: Task<Result<'a, ActionError>>) =
    task {
        match! action with
        | Error NotOpen -> ()
        | other -> failwith $"expected NotOpen, got {other}"
    }

let private expectError (error: ActionError) (action: Task<Result<'a, ActionError>>) =
    task {
        match! action with
        | Error e -> Assert.Equal(error, e)
        | Ok _ -> failwith $"expected {error}, got Ok"
    }

[<Fact>]
let ``Open refuses a decided season with NotOpen and writes nothing`` () =
    task {
        use! app = enriched ()
        let! _, matched = pickSeasons app
        let! manual = Db.scalar<int> app.Db $"UPDATE cr_season_matches SET method = 'manual' WHERE season_id = '{tamerSeason}' RETURNING 1" []
        Assert.Equal(1, manual)
        let! before = state app
        let clock = TimeProvider.System

        do! expectNotOpen (MalActions.setNone app.Db clock Open matched)
        do! expectNotOpen (MalActions.setNone app.Db clock Open tamerSeason) // manual, whatever its status
        do! expectNotOpen (MalActions.retry app.Db Open matched)
        do! expectNotOpen (MalActions.setMatch app.Db (httpFactory app) clock (malSettings app) Open matched 15)
        Assert.Empty app.Mal.Requests // refused after the (held) anime, no request for 15
        let! after = state app
        Assert.Equal(before, after)
    }

[<Fact>]
let ``Manual refuses an automatic season with NotOpen and writes nothing`` () =
    task {
        use! app = enriched ()
        let! openSeason, matched = pickSeasons app
        let! before = state app
        do! expectNotOpen (MalActions.backToAutomatic app.Db Manual matched)
        do! expectNotOpen (MalActions.backToAutomatic app.Db Manual openSeason)
        do! expectNotOpen (MalActions.retry app.Db Manual matched)
        let! after = state app
        Assert.Equal(before, after)
    }

[<Fact>]
let ``Open applies to an undecided season, and the decision then closes it`` () =
    task {
        use! app = enriched ()
        let! openSeason, _ = pickSeasons app
        let clock = TimeProvider.System
        do! expectOk (MalActions.setNone app.Db clock Open openSeason)
        let! row = matchOf app.Db openSeason
        Assert.Contains(" manual", row)
        do! expectNotOpen (MalActions.setNone app.Db clock Open openSeason)
        // and Manual now applies to it
        do! expectOk (MalActions.backToAutomatic app.Db Manual openSeason)
        let! back = matchOf app.Db openSeason
        Assert.Equal($"{openSeason} pending - - -", back)
    }

[<Fact>]
let ``actions on an unknown season say UnknownSeason whatever they expect`` () =
    task {
        use! app = enriched ()
        let clock = TimeProvider.System

        for expect in [ Open; Manual; Any ] do
            do! expectError UnknownSeason (MalActions.setNone app.Db clock expect "NOPE")
            do! expectError UnknownSeason (MalActions.backToAutomatic app.Db expect "NOPE")
            do! expectError UnknownSeason (MalActions.retry app.Db expect "NOPE")
            do! expectError UnknownSeason (MalActions.setMatch app.Db (httpFactory app) clock (malSettings app) expect "NOPE" 15)
    }

[<Fact>]
let ``setMatch without a client id is Unavailable, makes no request and writes nothing`` () =
    task {
        use! app = enriched ()
        let noKey = { malSettings app with ClientId = None }
        let! openSeason, _ = pickSeasons app
        let! before = state app

        for expect in [ Any; Open ] do
            match! MalActions.setMatch app.Db (httpFactory app) TimeProvider.System noKey expect openSeason 15 with
            | Error(Unavailable _) -> ()
            | other -> failwith $"expected Unavailable, got {other}"

        Assert.Empty app.Mal.Requests
        let! after = state app
        Assert.Equal(before, after)
    }
