module FrunchyBackend.Tests.RefreshTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Workers
open FrunchyBackend.Workers.Enrichment
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.ResilienceTests
open FrunchyBackend.Tests.Infrastructure.FakeJikan
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// The sample imported and enriched at a controllable "now"
let private enrichedWithClock () =
    task {
        let clock = TestClock()
        let! db = newDatabase ()
        let! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        routeSampleCatalog app.Jikan
        let! _ = enrichAllWith app clock
        app.Jikan.Clear()
        return app, clock
    }

let private times (app: TestApp) (malId: int) =
    task {
        let! fetched = Db.scalar<DateTime> app.Db "SELECT fetched_at FROM mal_anime WHERE mal_id = @id" [ "id", box malId ]
        let! checkedAt = Db.scalar<DateTime> app.Db "SELECT checked_at FROM mal_anime WHERE mal_id = @id" [ "id", box malId ]
        return fetched, checkedAt
    }

let private fullRequests (app: TestApp) =
    app.Jikan.Requests |> List.filter (fun r -> r.StartsWith "full:")

[<Fact>]
let ``nothing is refreshed before 30 days; after that each held anime once, unchanged ones keep their data`` () =
    task {
        let! app, clock = enrichedWithClock ()
        use app = app
        let! fetchedBefore, _ = times app 52991

        clock.Now <- clock.Now.AddDays 29.0
        let! early = enrichAllWith app clock
        Assert.Empty early
        Assert.Empty app.Jikan.Requests

        clock.Now <- clock.Now.AddDays 2.0
        let! _ = enrichAllWith app clock
        Assert.Equal(7, fullRequests app |> List.length)
        Assert.Equal(7, fullRequests app |> List.distinct |> List.length)

        // Same body → only checked_at moves
        let! fetchedAfter, checkedAfter = times app 52991
        Assert.Equal(fetchedBefore, fetchedAfter)
        Assert.True(DateTimeOffset(checkedAfter, TimeSpan.Zero) >= clock.Now.AddSeconds -1.0)

        // And then nothing more is due
        app.Jikan.Clear()
        let! _ = enrichAllWith app clock
        Assert.Empty app.Jikan.Requests
    }

[<Fact>]
let ``an airing anime is refreshed after 7 days, and a changed body replaces the details`` () =
    task {
        let! app, clock = enrichedWithClock ()
        use app = app
        let! _ = Db.scalar<int> app.Db "UPDATE mal_anime SET airing = true WHERE mal_id = 51009 RETURNING 1" []
        routeFull app.Jikan { jjk2 with Score = Some 9.1; Genres = [ "Action"; "Drama" ]; Airing = true }

        clock.Now <- clock.Now.AddDays 8.0
        let! _ = enrichAllWith app clock
        Assert.Equal<string list>([ "full:51009" ], fullRequests app)

        let! score = Db.scalar<decimal> app.Db "SELECT score FROM mal_anime WHERE mal_id = 51009" []
        Assert.Equal(9.1m, score)
        let! genres = Db.dump app.Db "SELECT t.name FROM mal_anime_tags a JOIN mal_tags t ON t.kind = a.kind AND t.tag_id = a.tag_id WHERE a.mal_id = 51009 AND a.kind = 'genre' ORDER BY a.position"
        Assert.Equal<string list>([ "Action"; "Drama" ], genres)
    }

[<Fact>]
let ``with a stored ETag the refresh is conditional and 304 means unchanged`` () =
    task {
        let! app, clock = enrichedWithClock ()
        use app = app
        let! _ = Db.scalar<int> app.Db "UPDATE mal_anime SET etag = '\"abc\"' WHERE mal_id = 15 RETURNING 1" []
        app.Jikan.Route("full:15", [ Status 304 ])
        let! fetchedBefore, _ = times app 15

        clock.Now <- clock.Now.AddDays 31.0
        let! _ = enrichAllWith app clock
        Assert.Contains(("full:15", "\"abc\""), app.Jikan.Conditional)
        let! fetchedAfter, checkedAfter = times app 15
        Assert.Equal(fetchedBefore, fetchedAfter)
        Assert.True(DateTimeOffset(checkedAfter, TimeSpan.Zero) >= clock.Now.AddSeconds -1.0)
    }

[<Fact>]
let ``a 404 keeps the last details and flags them; a 504 backs off and keeps them too`` () =
    task {
        let! app, clock = enrichedWithClock ()
        use app = app
        app.Jikan.Route("full:15", [ Status 404 ])
        app.Jikan.Route("full:55998", [ Status 504 ])

        clock.Now <- clock.Now.AddDays 31.0
        let! _ = enrichAllWith app clock

        let! missing = Db.scalar<int64> app.Db "SELECT count(*) FROM mal_anime WHERE mal_id = 15 AND missing_since IS NOT NULL AND title = 'Eyeshield 21'" []
        Assert.Equal(1L, missing)
        let! attempts = Db.scalar<int> app.Db "SELECT refresh_attempts FROM mal_anime WHERE mal_id = 55998" []
        Assert.Equal(1, attempts)
        let! next = Db.scalar<DateTime> app.Db "SELECT refresh_next_at FROM mal_anime WHERE mal_id = 55998" []
        Assert.True(DateTimeOffset(next, TimeSpan.Zero) > clock.Now)

        // The dashboard still shows the held details (the season stays matched)
        let! row = matchOf app.Db "G675KXENR"
        Assert.Equal("G675KXENR matched 15 search -", row)
        let! s = FrunchyBackend.Operations.MalStatus.status app.Db
        Assert.Equal(1L, s.AnimeMissing)
    }

[<Fact>]
let ``new lookups go before refreshes`` () =
    task {
        let! app, clock = enrichedWithClock ()
        use app = app
        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET status = 'pending', mal_id = NULL, method = NULL, shared_from = NULL WHERE season_id = 'GYK5CNZ9G' RETURNING 1" []
        clock.Now <- clock.Now.AddDays 31.0
        let! _ = processNext app.Db (httpFactory app) clock settings
        Assert.Equal<string list>([ "search:Platinum End" ], app.Jikan.Requests)
    }
