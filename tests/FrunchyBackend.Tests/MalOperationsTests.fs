module FrunchyBackend.Tests.MalOperationsTests

open System
open System.Threading.Tasks
open Xunit
open FrunchyBackend
open FrunchyBackend.Operations
open FrunchyBackend.Workers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

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

/// Run a command through the CLI map, as `dotnet run -- <command>` does
let private cli (app: TestApp) (command: string) (args: string list) : Task<int> =
    let env: Program.CliEnv =
        { Db = app.Db
          Http = httpFactory app
          Mal = FrunchyBackend.Infrastructure.MalClient.readSettings (Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(app.Services))
          Clock = TimeProvider.System
          Federated = false; RootOperator = None }

    Program.cliCommands[command] env args

[<Fact>]
let ``mal-status counts seasons per status and method`` () =
    task {
        use! app = enriched ()
        let! s = MalStatus.status app.Db
        let count status method' = s.Seasons |> List.tryFind (fun (k, _) -> k = (status, method')) |> Option.map snd |> Option.defaultValue 0L
        Assert.True(count "matched" "dub" >= 2L)
        Assert.True(count "matched" "search" >= 3L)
        Assert.True(count "no_match" "-" > 0L)
        Assert.Equal(7L, s.AnimeHeld) // Frieren, JJK S1 + S2 + 0, the Weakest Tamer, ROLL OVER AND DIE, Eyeshield 21
        Assert.Equal(0L, s.RefreshesDue)
        let! code = cli app "mal-status" []
        Assert.Equal(0, code)
    }

[<Fact>]
let ``mal-list shows what isn't matched, filterable, with candidates`` () =
    task {
        use! app = enriched ()
        let! rows = MalList.list app.Db None 500
        Assert.NotEmpty rows
        Assert.DoesNotContain(rows, fun r -> r.Status = "matched")
        let! noMatch = MalList.list app.Db (Some "no_match") 500
        Assert.True(noMatch |> List.forall (fun r -> r.Status = "no_match"))
        let! usage = cli app "mal-list" [ "--status"; "nonsense" ]
        Assert.Equal(1, usage)
    }

[<Fact>]
let ``mal-show lists a series' seasons with their matches`` () =
    task {
        use! app = enriched ()

        match! MalShow.show app.Db "GRDV0019R" with
        | None -> failwith "JUJUTSU KAISEN not found"
        | Some(title, rows) ->
            Assert.Equal("JUJUTSU KAISEN", title)
            let s2 = rows |> List.find (fun r -> r.SeasonId = "G6MGC3778")
            Assert.Equal((Some 51009, Some "link"), (s2.MalId, s2.Method))
            Assert.Equal<string list>([ "Action"; "Supernatural" ], s2.Genres)

        let! unknown = cli app "mal-show" [ "NOPE" ]
        Assert.Equal(2, unknown)
    }

[<Fact>]
let ``mal-set: a held anime needs no request, a new one is fetched, an unknown id is refused`` () =
    task {
        use! app = enriched ()
        // Platinum End had no match; set it by hand to an anime already held → no request
        let! held = cli app "mal-set" [ "GYK5CNZ9G"; "15" ]
        Assert.Equal(0, held)
        Assert.Empty app.Mal.Requests
        let! row = matchOf app.Db "GYK5CNZ9G"
        Assert.Equal("GYK5CNZ9G matched 15 manual -", row)

        // Not held → one request
        routeFull app.Mal (spec 48413 "Platinum End" (Some "Platinum End") "TV" "2021-10-08")
        let! fetched = cli app "mal-set" [ "GYK5CNZ9G"; "48413" ]
        Assert.Equal(0, fetched)
        Assert.Equal(1, app.Mal.Count "details:48413")

        // 404 → exit 3, the season keeps its previous state
        app.Mal.Route("details:99999", [ Status 404 ])
        let! unknown = cli app "mal-set" [ "GYK5CNZ9G"; "99999" ]
        Assert.Equal(3, unknown)
        let! still = matchOf app.Db "GYK5CNZ9G"
        Assert.Equal("GYK5CNZ9G matched 48413 manual -", still)

        // Jikan down → exit 4; unknown season → exit 2
        app.Mal.Route("details:88888", [ Status 504 ])
        let! down = cli app "mal-set" [ "GYK5CNZ9G"; "88888" ]
        Assert.Equal(4, down)
        let! noSeason = cli app "mal-set" [ "NOPE"; "15" ]
        Assert.Equal(2, noSeason)
    }

[<Fact>]
let ``mal-set without a MyAnimeList client id changes nothing and says why`` () =
    task {
        use! app = enriched ()

        let env: Program.CliEnv =
            { Db = app.Db
              Http = httpFactory app
              Mal = { FrunchyBackend.Infrastructure.MalClient.readSettings (Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()) with ClientId = None }
              Clock = TimeProvider.System
              Federated = false; RootOperator = None }

        let! code = Program.cliCommands["mal-set"] env [ "GYK5CNZ9G"; "15" ]
        Assert.Equal(4, code)
        Assert.Empty app.Mal.Requests
        let! row = matchOf app.Db "GYK5CNZ9G"
        Assert.StartsWith("GYK5CNZ9G no_match", row)
    }

[<Fact>]
let ``a manual match carries its dubs, and nothing automatic overwrites it (SC-008)`` () =
    task {
        use! app = enriched ()
        // The Weakest Tamer: set the original by hand; its English dub follows
        routeFull app.Mal (spec 77777 "Another Tamer" None "TV" "2024-01-01")
        let! code = cli app "mal-set" [ "GRK5CNJ14"; "77777" ]
        Assert.Equal(0, code)
        let! dub = matchOf app.Db "GYDQCGK5Q"
        Assert.Equal("GYDQCGK5Q matched 77777 dub GRK5CNJ14", dub)

        // The worker, a title change, a series retry and a rebuild all leave it as set
        let! _ = Db.scalar<int> app.Db "UPDATE cr_seasons SET title = 'Season 01 renamed' WHERE id = 'GRK5CNJ14' RETURNING 1" []
        let! _ = enrichAll app
        let! retried = cli app "mal-retry" [ "GQWH0M1XK" ]
        Assert.Equal(0, retried)
        let! _ = enrichAll app
        let! _ = MalRebuild.rebuild app.Db
        let! original = matchOf app.Db "GRK5CNJ14"
        Assert.Equal("GRK5CNJ14 matched 77777 manual -", original)
    }

[<Fact>]
let ``mal-none marks a season not on MyAnimeList, manually`` () =
    task {
        use! app = enriched ()
        let! code = cli app "mal-none" [ "GYE5CQMQ5" ]
        Assert.Equal(0, code)
        let! row = matchOf app.Db "GYE5CQMQ5"
        Assert.Equal("GYE5CQMQ5 no_match - manual -", row)
        let! reason = Db.scalar<string> app.Db "SELECT reason FROM cr_season_matches WHERE season_id = 'GYE5CQMQ5'" []
        Assert.Equal("manual-none", reason)
        let! unknown = cli app "mal-none" [ "NOPE" ]
        Assert.Equal(2, unknown)
    }

[<Fact>]
let ``mal-retry puts automatic seasons at the front and asks Jikan again`` () =
    task {
        use! app = enriched ()

        match! MalRetry.retry app.Db "GG5H5XQX4" with
        | Some(seasons, anime) -> Assert.Equal((1, 0), (seasons, anime))
        | None -> failwith "Frieren's series not found"

        let! queued = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE season_id = 'GYE5CQMQ5' AND status = 'pending' AND requested_at IS NOT NULL AND attempts = 0" []
        Assert.Equal(1L, queued)
        let! first = Enrichment.claimSeason app.Db DateTimeOffset.UtcNow true
        Assert.Equal(Some "GYE5CQMQ5", first)
        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET claimed_at = NULL RETURNING 1" []

        let! _ = enrichAll app
        Assert.Equal(1, app.Mal.Count "search:Frieren: Beyond Journey's End") // the stored search was dropped
        let! back = matchOf app.Db "GYE5CQMQ5"
        Assert.Equal("GYE5CQMQ5 matched 52991 search -", back)

        let! unknown = cli app "mal-retry" [ "NOPE" ]
        Assert.Equal(2, unknown)
    }

[<Fact>]
let ``mal-rebuild sends no requests, keeps every decision, and is idempotent (SC-006)`` () =
    task {
        use! app = enriched ()
        let! before = matches app.Db
        let! tagsBefore = Db.dump app.Db "SELECT mal_id || ' ' || kind || ' ' || tag_id || ' ' || position FROM mal_anime_tags ORDER BY 1"
        // Damage a derived column: the rebuild restores it from the stored response
        let! _ = Db.scalar<int> app.Db "UPDATE mal_anime SET title = 'wrong', score = NULL WHERE mal_id = 52991 RETURNING 1" []

        let! first = MalRebuild.rebuild app.Db
        Assert.Equal(0, first.Changed)
        Assert.Equal(7, first.AnimeRederived)
        let! second = MalRebuild.rebuild app.Db
        Assert.Equal(0, second.Changed)
        Assert.Equal(first.Unchanged, second.Unchanged)
        Assert.Empty app.Mal.Requests

        let! after = matches app.Db
        Assert.Equal<string list>(before, after)
        let! tagsAfter = Db.dump app.Db "SELECT mal_id || ' ' || kind || ' ' || tag_id || ' ' || position FROM mal_anime_tags ORDER BY 1"
        Assert.Equal<string list>(tagsBefore, tagsAfter)
        let! title = Db.scalar<string> app.Db "SELECT title FROM mal_anime WHERE mal_id = 52991" []
        Assert.Equal("Sousou no Frieren", title)
    }
