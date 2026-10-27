module FrunchyBackend.Tests.ResilienceTests

open System
open System.Diagnostics
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.MalClient
open FrunchyBackend.Workers
open FrunchyBackend.Workers.Enrichment
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private fullKey = "details:52991"

let private client (extra: (string * string) list) =
    let fake = new FakeMalHandler()
    fake, factory fake extra

// ---------------------------------------------------------------------------------------------
// The HTTP pipeline (research R4)
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``504, 504, then 200: retried until it works`` () =
    task {
        let fake, http = client []
        fake.Route(fullKey, [ Status 504; Status 504; File "full-52991.json" ])

        match! get MalApi http (MalApi.DetailsPath 52991) None with
        | Ok(body, _) -> Assert.Contains("Sousou no Frieren", body)
        | other -> failwith $"expected Ok, got %A{other}"

        Assert.Equal(3, fake.Count fullKey)
    }

[<Fact>]
let ``429 with Retry-After is honoured`` () =
    task {
        let fake, http = client []
        fake.Route(fullKey, [ RetryAfter 1; File "full-52991.json" ])
        let watch = Stopwatch.StartNew()
        let! result = get MalApi http (MalApi.DetailsPath 52991) None
        watch.Stop()

        match result with
        | Ok _ -> ()
        | other -> failwith $"expected Ok, got %A{other}"

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds 0.9, $"waited only {watch.Elapsed}")
    }

[<Fact>]
let ``four 503s give up as temporary after exactly 4 attempts`` () =
    task {
        let fake, http = client []
        fake.Route(fullKey, [ Status 503 ])

        match! get MalApi http (MalApi.DetailsPath 52991) None with
        | Temporary message -> Assert.Contains("503", message)
        | other -> failwith $"expected Temporary, got %A{other}"

        Assert.Equal(4, fake.Count fullKey)
    }

[<Fact>]
let ``404 is not retried; 400 is definite; a dropped connection is temporary`` () =
    task {
        let fake, http = client []
        fake.Route(fullKey, [ Status 404 ])
        let! notFound = get MalApi http (MalApi.DetailsPath 52991) None
        Assert.Equal(NotFound, notFound)
        Assert.Equal(1, fake.Count fullKey)

        fake.Route("details:1", [ Status 400 ])

        match! get MalApi http (MalApi.DetailsPath 1) None with
        | Definite _ -> ()
        | other -> failwith $"expected Definite, got %A{other}"

        fake.Route("details:2", [ Throw(HttpRequestException "connection reset") ])

        match! get MalApi http (MalApi.DetailsPath 2) None with
        | Temporary message -> Assert.Contains("connection", message)
        | other -> failwith $"expected Temporary, got %A{other}"
    }

[<Fact>]
let ``an unreadable 200 is a temporary failure for the matcher`` () =
    task {
        let! db = newDatabase ()
        let fake, http = client []
        fake.Route(fullKey, [ Body "<html>maintenance</html>" ])
        let source = Live(http, db, TimeProvider.System, MalApi)

        match! source.Lookup.Full 52991 with
        | MalMatching.Unavailable reason -> Assert.StartsWith("unreadable", reason)
        | other -> failwith $"expected Unavailable, got %A{other}"

        let! held = Db.count db "mal_anime"
        Assert.Equal(0L, held)
    }

[<Fact>]
let ``requests are paced, retries included (FR-010)`` () =
    task {
        let fake, http = client [ "Frunchy:Mal:RequestsPerSecond", "2" ]
        fake.Route(fullKey, [ File "full-52991.json" ])
        let watch = Stopwatch.StartNew()

        for _ in 1..6 do
            let! _ = get MalApi http (MalApi.DetailsPath 52991) None
            ()

        watch.Stop()
        // 6 requests at 2 per second, the first one immediate: at least 2.5 s
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds 2.4, $"took only {watch.Elapsed}")
    }

// ---------------------------------------------------------------------------------------------
// The durable queue (research R5)
// ---------------------------------------------------------------------------------------------

type TestClock() =
    inherit TimeProvider()
    member val Now = DateTimeOffset.UtcNow with get, set
    override this.GetUtcNow() = this.Now

let private appWithClock () =
    task {
        let clock = TestClock()
        let! db = newDatabase ()
        let! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        return app, clock
    }

let private step (app: TestApp) (clock: TimeProvider) =
    Enrichment.processNext app.Db (httpFactory app) clock settings

let private row (app: TestApp) (seasonId: string) =
    task {
        let! status = Db.scalar<string> app.Db "SELECT status FROM cr_season_matches WHERE season_id = @id" [ "id", box seasonId ]
        let! attempts = Db.scalar<int> app.Db "SELECT attempts FROM cr_season_matches WHERE season_id = @id" [ "id", box seasonId ]
        let! next = Db.scalar<DateTime> app.Db "SELECT next_attempt_at FROM cr_season_matches WHERE season_id = @id" [ "id", box seasonId ]
        return status, attempts, DateTimeOffset(next, TimeSpan.Zero)
    }

[<Fact>]
let ``a failing lookup backs off, doubling, and becomes failed from the third attempt (FR-014)`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        // Only Frieren is due; every other season waits far in the future
        do! Enrichment.sweep app.Db
        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET next_attempt_at = now() + interval '30 days' WHERE season_id <> 'GYE5CQMQ5' RETURNING 1" []
        app.Mal.Route("search:Frieren: Beyond Journey's End", [ Status 504 ])

        let! r1 = step app clock
        Assert.Equal(TemporaryFailure, r1)
        let! status, attempts, next = row app "GYE5CQMQ5"
        Assert.Equal(("pending", 1), (status, attempts))
        Assert.InRange((next - clock.Now).TotalMinutes, 0.7, 1.3)

        clock.Now <- next.AddSeconds 1.0
        let! _ = step app clock
        let! status, attempts, next = row app "GYE5CQMQ5"
        Assert.Equal(("pending", 2), (status, attempts))
        Assert.InRange((next - clock.Now).TotalMinutes, 1.5, 2.5)

        // Not due yet: nothing is sent
        app.Mal.Clear()
        let! idle = step app clock
        Assert.Equal(Idle, idle)
        Assert.Empty app.Mal.Requests

        clock.Now <- next.AddSeconds 1.0
        let! _ = step app clock
        let! status, attempts, _ = row app "GYE5CQMQ5"
        Assert.Equal(("failed", 3), (status, attempts))

        // The Jikan error is kept for the operator
        let! error = Db.scalar<string> app.Db "SELECT last_error FROM cr_season_matches WHERE season_id = 'GYE5CQMQ5'" []
        Assert.Contains("504", error)
    }

[<Fact>]
let ``the backoff is capped at 6 hours`` () =
    Assert.Equal(TimeSpan.FromMinutes 1.0, MalCatalog.backoff 1 1.0)
    Assert.Equal(TimeSpan.FromMinutes 4.0, MalCatalog.backoff 3 1.0)
    Assert.Equal(TimeSpan.FromHours 6.0, MalCatalog.backoff 20 1.0)

[<Fact>]
let ``one season's failure doesn't stop the others (FR-017)`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        routeSampleCatalog app.Mal
        app.Mal.Route("search:Frieren: Beyond Journey's End", [ Status 504 ])
        let! _ = enrichAllWith app clock
        let! frieren = matchOf app.Db "GYE5CQMQ5"
        Assert.StartsWith("GYE5CQMQ5 pending", frieren)
        let! jjk = matchOf app.Db "G6MGC3778"
        Assert.Equal("G6MGC3778 matched 51009 link -", jjk)
    }

[<Fact>]
let ``a candidate whose details are gone is dropped, not retried`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        routeSearch app.Mal "Eyeshield 21" [ eyeshield ]
        app.Mal.Route("details:15", [ Status 404 ])
        let! _ = enrichAllWith app clock
        let! row = matchOf app.Db "G675KXENR"
        Assert.StartsWith("G675KXENR no_match", row)
    }

// ---------------------------------------------------------------------------------------------
// The pause (FR-015)
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``5 failures in a row pause, then one probe; a failed probe pauses again; a success resets`` () =
    let pause = Some settings.PauseFor
    let mutable count = 0

    let next result =
        let c, p = afterResult settings count result
        count <- c
        p

    for _ in 1..4 do
        Assert.Equal(None, next TemporaryFailure)

    Assert.Equal(pause, next TemporaryFailure) // 5th in a row
    Assert.Equal(pause, next TemporaryFailure) // the probe failed: pause again
    Assert.Equal(None, next Done) // the probe worked
    Assert.Equal(0, count)
    Assert.Equal(None, next Idle)
    Assert.Equal(None, next TemporaryFailure)
    Assert.Equal(1, count)
