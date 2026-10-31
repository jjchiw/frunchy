/// Feature 028 (T006): the outbox kinds `follow_series` and `unfollow_series`, the `extensions` every thread is
/// published with, and the hourly sweep. Driven through `drainOutboxAt` against the fake Megáfono.
module FrunchyBackend.Tests.InstanceFollowOutboxTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.FakeMegafono
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers

let private series = "GG5H5XQX4"
let private domain = "other.test"
let private target = $"https://{domain}/ap/actors/series-{series}"
let private now = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

/// A federated app and one explicit follow (`source = 'series'`, queued, with its `follow_series` item)
let private withFollow () : Task<TestApp> =
    task {
        let! app = newFederatedApp ()
        use connection = Database.getConnection app.Db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let! _ = InstanceFollows.addSeries connection tx series domain now
        do! tx.CommitAsync()
        return app
    }

let private row (app: TestApp) =
    Db.dump app.Db "SELECT state, coalesce(follow_id, '-'), coalesce(last_error, '-') FROM series_follows"

let private outbox (app: TestApp) =
    Db.dump app.Db "SELECT kind, state, attempts, coalesce(last_error, '-') FROM fediverse_outbox ORDER BY id"

/// The outbox's own `next_attempt_at` is the database's clock, so the worker runs a few seconds after the real one
let private laterAt () = DateTimeOffset.UtcNow.AddSeconds 5.0

[<Fact>]
let ``a follow goes from the series actor to the other instance's series actor and waits as pending`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        let! processed = drainOutboxAt app later
        Assert.Equal(1, processed)

        let call = (app.Megafono.RequestsTo $"POST /internal/actors/series-{series}/following").Head
        Assert.Equal(target, call.Json.GetProperty("target").GetString())
        Assert.StartsWith("Bearer ", call.Authorization.Value)

        let! r = row app
        Assert.Equal<string list>([ "pending|f-1|-" ], r)
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|done|0|-" ], items)

        // our series account has a handle (Megáfono looks the actor up by it)
        let! handles = Db.count app.Db "series_handles"
        Assert.Equal(1L, handles)
    }

[<Fact>]
let ``a follow Megafono answers as accepted is accepted`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        app.Megafono.Route("POST /internal/actors/*/following", [ Body(202, """{"followId":"f-9","status":"accepted"}""") ])
        let! _ = drainOutboxAt app later
        let! r = row app
        Assert.Equal<string list>([ "accepted|f-9|-" ], r)
    }

[<Fact>]
let ``an accepted webhook that overtook the answer is not undone by it`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        do! Db.execute app.Db "UPDATE series_follows SET state = 'accepted', follow_id = 'f-hook'"
        let! _ = drainOutboxAt app later
        let! r = row app
        Assert.Equal<string list>([ "accepted|f-1|-" ], r)
    }

[<Fact>]
let ``a 404 means the other instance doesn't know the series: not_found, and the item is done`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        app.Megafono.Route("POST /internal/actors/*/following", [ Status 404 ])
        let! _ = drainOutboxAt app later
        let! r = row app
        Assert.Equal<string list>([ "not_found|-|404 not_found" ], r)
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|done|0|404 not_found" ], items)
    }

[<Fact>]
let ``a blocked answer, a refused address and any other refusal fail the follow for good`` () =
    task {
        for reply, expected in
            [ Body(409, """{"error":"blocked","message":"blocked"}"""), "409 blocked"
              Status 400, "400 invalid_request"
              Status 403, "403 server_error" ] do
            use! app = withFollow ()
            let later = laterAt ()
            app.Megafono.Route("POST /internal/actors/*/following", [ reply ])
            let! _ = drainOutboxAt app later
            let! r = row app
            Assert.Equal<string list>([ $"failed|-|{expected}" ], r)
            let! items = outbox app
            Assert.Equal<string list>([ $"follow_series|failed|1|{expected}" ], items)

            // never retried
            let! again = drainOutboxAt app (later.AddDays 1.0)
            Assert.Equal(0, again)
    }

[<Fact>]
let ``a server error keeps the follow queued and retries it with the backoff`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        app.Megafono.Route("POST /internal/actors/*/following", [ Status 503; Status 202 ])
        let! first = drainOutboxAt app later
        Assert.Equal(1, first)

        let! r = row app
        Assert.Equal<string list>([ "queued|-|-" ], r)
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|pending|1|503 app_unavailable" ], items)

        // not due again at once
        let! tooSoon = drainOutboxAt app later
        Assert.Equal(0, tooSoon)

        // a minute on: Megafono answers 202 with no body, which is unreadable, so it retries again
        let! _ = drainOutboxAt app (later.AddMinutes 5.0)
        let! items2 = outbox app
        Assert.Equal<string list>([ "follow_series|pending|2|unreadable answer" ], items2)

        app.Megafono.Route("POST /internal/actors/*/following", [ Body(202, """{"followId":"f-3","status":"pending"}""") ])
        let! _ = drainOutboxAt app (later.AddHours 2.0)
        let! r2 = row app
        Assert.Equal<string list>([ "pending|f-3|-" ], r2)
    }

[<Fact>]
let ``a follow whose row is gone is done without a call`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        do! Db.execute app.Db "DELETE FROM series_follows"
        let! _ = drainOutboxAt app later
        Assert.Empty app.Megafono.Requests
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|done|0|-" ], items)
    }

[<Fact>]
let ``an unfollow deletes the follow by the other instance's actor URL and treats 404 as done`` () =
    task {
        use! app = withFollow ()
        let later = laterAt ()
        let! _ = drainOutboxAt app later

        use connection = Database.getConnection app.Db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let! removed = InstanceFollows.removeSeries connection tx series domain
        do! tx.CommitAsync()
        Assert.True removed

        let! _ = drainOutboxAt app later
        let call = (app.Megafono.RequestsTo $"DELETE /internal/actors/series-{series}/following").Head
        Assert.Equal("?actor=" + Uri.EscapeDataString target, call.Query)
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|done|0|-"; "unfollow_series|done|0|-" ], items)
    }

[<Fact>]
let ``an unfollow Megafono doesn't know is done`` () =
    task {
        use! app = newFederatedApp ()
        let later = laterAt ()
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject) VALUES ('unfollow_series', '{series}@{domain}')"
        app.Megafono.Route("DELETE /internal/actors/*/following", [ Status 404 ])
        let! _ = drainOutboxAt app later
        let! items = outbox app
        Assert.Equal<string list>([ "unfollow_series|done|0|404 not_found" ], items)
    }

[<Fact>]
let ``a follow item with a bad subject is refused`` () =
    task {
        use! app = newFederatedApp ()
        let later = laterAt ()
        do! Db.execute app.Db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('follow_series', 'nonsense')"
        let! _ = drainOutboxAt app later
        Assert.Empty app.Megafono.Requests
        let! items = outbox app
        Assert.Equal<string list>([ "follow_series|failed|1|bad subject" ], items)
    }

// ---------------------------------------------------------------------------
// Threads carry the ids another instance maps them by
// ---------------------------------------------------------------------------

[<Fact>]
let ``a thread is published with the ids of its episode as extensions, a comment with none`` () =
    task {
        let! app = newFederatedApp ()
        use _ = app
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND sea.season_number IS NOT NULL AND e.title IS NOT NULL"
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Alice's take" false
        let! _ = drainOutbox app

        let! groupKey = Db.scalar<string> app.Db "SELECT group_key FROM episode_threads" []
        let calls = app.Megafono.RequestsTo "POST /internal/actors/*/objects"
        let thread, reply = calls[0], calls[1]

        let extensions = thread.Json.GetProperty "extensions"
        Assert.Equal("https://github.com/jjchiw/frunchy/ns#", extensions.GetProperty("namespace").GetString())
        Assert.Equal("frunchy", extensions.GetProperty("prefix").GetString())
        let episode = extensions.GetProperty("data").GetProperty "episode"
        Assert.Equal(ep.Series, episode.GetProperty("seriesId").GetString())
        Assert.Equal(ep.Episode, episode.GetProperty("episodeId").GetString())
        Assert.Equal(groupKey, episode.GetProperty("groupKey").GetString())

        // a reply says nothing under our namespace: `extensions` is a JSON null
        Assert.Equal(JsonValueKind.Null, reply.Json.GetProperty("extensions").ValueKind)
    }

// ---------------------------------------------------------------------------
// The sweep schedule
// ---------------------------------------------------------------------------

[<Fact>]
let ``the sweep runs at once, then not again within the hour, then again`` () =
    task {
        let! app = newFederatedApp ()
        use _ = app
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        do! Db.execute app.Db $"INSERT INTO instance_follows (domain) VALUES ('{domain}')"

        let! last = FrunchyBackend.Workers.FediverseOutbox.sweepWhenDue app.Db None now
        Assert.Equal(Some now, last)
        let! first = Db.count app.Db "series_follows"
        Assert.True(first > 0L)

        // a series watched afterwards waits for the next run
        do! Db.execute app.Db "INSERT INTO cr_episodes (id, series_id, duration_ms, source_at) VALUES ('NEWEP1', 'NEWSER1', 1400000, now())"

        do!
            Db.execute
                app.Db
                "INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id) SELECT id, 'NEWEP1', now(), 10, true, gen_random_uuid() FROM users LIMIT 1"

        let! soon = FrunchyBackend.Workers.FediverseOutbox.sweepWhenDue app.Db last (now.AddMinutes 59.0)
        Assert.Equal(last, soon)
        let! unchanged = Db.count app.Db "series_follows"
        Assert.Equal(first, unchanged)

        let! next = FrunchyBackend.Workers.FediverseOutbox.sweepWhenDue app.Db last (now.AddMinutes 61.0)
        Assert.Equal(Some(now.AddMinutes 61.0), next)
        let! after = Db.count app.Db "series_follows"
        Assert.Equal(first + 1L, after)
    }
