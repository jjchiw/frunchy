/// Feature 028 (T004, T005): the pure id rules, the Megáfono client calls that follow another instance's series
/// accounts (against the fake), and the data access behind the operator page (rows, outbox items, the sweep).
module FrunchyBackend.Tests.InstanceFollowRulesTests

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.InstanceFollows
open FrunchyBackend.Infrastructure.MegafonoClient
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Pure rules
// ---------------------------------------------------------------------------

[<Fact>]
let ``a series actor URL gives its host and series id, and nothing looser does`` () =
    Assert.Equal(Some("other.test", "GG5H5XQX4"), seriesIdOfActor "https://other.test/ap/actors/series-GG5H5XQX4")
    Assert.Equal(Some("sub.other.test", "ABC123"), seriesIdOfActor "https://sub.other.test/ap/actors/series-ABC123")

    for bad in
        [ "http://other.test/ap/actors/series-GG5H5XQX4"
          "https://other.test:8443/ap/actors/series-GG5H5XQX4"
          "https://other.test/ap/actors/series-GG5H5XQX4/"
          "https://other.test/ap/actors/series-GG5H5XQX4?x=1"
          "https://other.test/ap/actors/series-GG5H5XQX4#frag"
          "https://other.test/ap/actors/series-GG5H5XQX4/inbox"
          "https://user@other.test/ap/actors/series-GG5H5XQX4"
          "https://Other.test/ap/actors/series-GG5H5XQX4"
          "https://other.test/ap/actors/series-gg5h5xqx4"
          "https://other.test/ap/actors/series-AB12"
          "https://other.test/ap/actors/series-ABCDEFGHIJKLMNOPQRSTU"
          "https://other.test/ap/actors/frieren"
          "https://other.test/users/series-GG5H5XQX4"
          "https://other.test/ap/actors/series-GG5H5XQX4 "
          ""
          null ] do
        Assert.Equal(None, seriesIdOfActor bad)

[<Fact>]
let ``the remote series actor is the feature 015 actor URL`` () =
    Assert.Equal("https://other.test/ap/actors/series-GG5H5XQX4", remoteSeriesActor "other.test" "GG5H5XQX4")
    Assert.Equal(Some("other.test", "GG5H5XQX4"), seriesIdOfActor (remoteSeriesActor "other.test" "GG5H5XQX4"))

[<Fact>]
let ``series ids and group keys are checked by shape`` () =
    Assert.True(isSeriesId "GG5H5XQX4")
    Assert.False(isSeriesId "gg5h5xqx4")
    Assert.False(isSeriesId "A1")
    Assert.False(isSeriesId null)
    Assert.True(isGroupKey "GG5H5XQX4")
    Assert.True(isGroupKey "GR3KGEXD6#12")
    Assert.False(isGroupKey "GR3KGEXD6#")
    Assert.False(isGroupKey "GR3KGEXD6#1234567")
    Assert.False(isGroupKey "gr3kgexd6#1")

// ---------------------------------------------------------------------------
// The client calls, against the fake
// ---------------------------------------------------------------------------

let private clientOver (fake: FakeMegafono.FakeMegafonoHandler) =
    new HttpClient(fake, false, BaseAddress = Uri "http://megafono.test", Timeout = TimeSpan.FromSeconds 5.0)

[<Fact>]
let ``the remote-actor lookup asks by handle or by url and reads the actor`` () =
    task {
        let fake = FakeMegafono.FakeMegafonoHandler()
        use client = clientOver fake

        let! result, actor = lookupRemoteActor client (Actor "@gg5h5xqx4@other.test")
        Assert.True result.IsSuccess
        Assert.Equal(Some { Id = "https://other.test/ap/actors/series-GG5H5XQX4"; Handle = "@gg5h5xqx4@other.test"; Type = "Group" }, actor)

        let! _, byUrl = lookupRemoteActor client (Actor "https://other.test/ap/actors/series-GG5H5XQX4")
        Assert.Equal(Some "https://other.test/ap/actors/series-GG5H5XQX4", byUrl |> Option.map _.Id)

        let calls = fake.RequestsTo "GET /internal/remote-actors"
        Assert.Equal(2, calls.Length)
        Assert.Equal("?handle=%40gg5h5xqx4%40other.test", calls[0].Query)
        Assert.Equal("?url=https%3A%2F%2Fother.test%2Fap%2Factors%2Fseries-GG5H5XQX4", calls[1].Query)
    }

[<Fact>]
let ``the remote-actor lookup passes errors on, reads nothing from a bad body, and never calls for a domain`` () =
    task {
        let fake = FakeMegafono.FakeMegafonoHandler()
        use client = clientOver fake

        fake.Route("GET /internal/remote-actors", [ FakeMegafono.Status 404 ])
        let! notFound, none = lookupRemoteActor client (Actor "@x@other.test")
        Assert.Equal(404, notFound.Status)
        Assert.Equal(None, none)

        fake.Route("GET /internal/remote-actors", [ FakeMegafono.Body(200, "not json") ])
        let! ok, unreadable = lookupRemoteActor client (Actor "@x@other.test")
        Assert.True ok.IsSuccess
        Assert.Equal(None, unreadable)

        fake.Clear()
        let! domain, _ = lookupRemoteActor client (Domain "other.test")
        Assert.Equal(400, domain.Status)
        Assert.Empty fake.Requests
    }

[<Fact>]
let ``follow posts the target and reads the follow id and status, unfollow deletes by actor`` () =
    task {
        let fake = FakeMegafono.FakeMegafonoHandler()
        use client = clientOver fake
        let target = "https://other.test/ap/actors/series-GG5H5XQX4"

        let! result = follow client "series-GG5H5XQX4" target
        Assert.Equal(202, result.Status)
        Assert.Equal(Some { FollowId = "f-1"; Status = "pending" }, followOf result)

        let call = (fake.RequestsTo "POST /internal/actors/series-GG5H5XQX4/following").Head
        Assert.Equal(target, call.Json.GetProperty("target").GetString())

        let! undone = unfollow client "series-GG5H5XQX4" target
        Assert.Equal(202, undone.Status)
        let del = (fake.RequestsTo "DELETE /internal/actors/series-GG5H5XQX4/following").Head
        Assert.Equal("?actor=https%3A%2F%2Fother.test%2Fap%2Factors%2Fseries-GG5H5XQX4", del.Query)

        fake.Route("POST /internal/actors/*/following", [ FakeMegafono.Status 404 ])
        let! missing = follow client "series-GG5H5XQX4" target
        Assert.Equal(None, followOf missing)
    }

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private now = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private inTx (app: TestApp) (work: Npgsql.NpgsqlConnection -> Npgsql.NpgsqlTransaction -> Task<'a>) : Task<'a> =
    task {
        use connection = Database.getConnection app.Db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let! result = work connection tx
        do! tx.CommitAsync()
        return result
    }

/// Sorted in F#, so the expected lists don't depend on the database's collation
let private follows (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT series_id, domain, source, state FROM series_follows"
        return List.sortWith (fun (a: string) b -> String.CompareOrdinal(a, b)) rows
    }

let private outbox (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT kind, subject FROM fediverse_outbox WHERE kind IN ('follow_series', 'unfollow_series')"
        return List.sortWith (fun (a: string) b -> String.CompareOrdinal(a, b)) rows
    }

let private ordinal (items: string list) = List.sortWith (fun (a: string) b -> String.CompareOrdinal(a, b)) items

/// The federated app with the 2026 sample imported: its watched series are the ones an instance follow covers
let private sample () : Task<TestApp * string list> =
    task {
        let! app = newFederatedApp ()
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db

        let! series =
            Db.dump
                app.Db
                "SELECT DISTINCT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY 1"

        return app, series
    }

[<Fact>]
let ``an instance follow queues a follow for every series watched here, once`` () =
    task {
        let! app, series = sample ()
        use _ = app
        Assert.True(series.Length > 1)

        let! changed = inTx app (fun c tx -> addInstance c tx "other.test" now)
        Assert.True changed

        let! rows = follows app
        Assert.Equal<string list>(series |> List.map (fun s -> $"{s}|other.test|instance|queued") |> ordinal, rows)
        let! queued = outbox app
        Assert.Equal<string list>(series |> List.map (fun s -> $"follow_series|{s}@other.test") |> ordinal, queued)

        let! actor = Db.scalar<string> app.Db "SELECT remote_actor FROM series_follows WHERE series_id = @s" [ "s", box series.Head ]
        Assert.Equal($"https://other.test/ap/actors/series-{series.Head}", actor)

        // the same again changes nothing
        let! again = inTx app (fun c tx -> addInstance c tx "other.test" now)
        Assert.False again
        let! rowsAgain = follows app
        let! queuedAgain = outbox app
        Assert.Equal<string list>(rows, rowsAgain)
        Assert.Equal<string list>(queued, queuedAgain)
    }

[<Fact>]
let ``an unresolved episode alone doesn't make a series watched`` () =
    task {
        let! app = newFederatedApp ()
        use _ = app
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        do! Db.execute app.Db "UPDATE cr_episodes SET duration_ms = NULL"

        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        let! rows = follows app
        Assert.Empty rows
    }

[<Fact>]
let ``the sweep adds the series watched later and leaves the others alone`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        let! _ = inTx app (fun c tx -> addInstance c tx "third.test" now)
        let before = series.Length

        // a new series is watched
        do! Db.execute app.Db "INSERT INTO cr_episodes (id, series_id, duration_ms, source_at) VALUES ('NEWEP1', 'NEWSER1', 1400000, now())"

        do!
            Db.execute
                app.Db
                "INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id) SELECT id, 'NEWEP1', now(), 10, true, gen_random_uuid() FROM users LIMIT 1"

        let! added = inTx app (fun c tx -> sweep c tx None now)
        Assert.Equal(2, added)
        let! total = Db.count app.Db "series_follows"
        Assert.Equal(int64 (2 * (before + 1)), total)

        let! none = inTx app (fun c tx -> sweep c tx None now)
        Assert.Equal(0, none)

        // one domain only
        do! Db.execute app.Db "DELETE FROM series_follows WHERE series_id = 'NEWSER1'"
        let! one = inTx app (fun c tx -> sweep c tx (Some "third.test") now)
        Assert.Equal(1, one)
        let! third = Db.scalar<int64> app.Db "SELECT count(*) FROM series_follows WHERE series_id = 'NEWSER1' AND domain = 'third.test'" []
        Assert.Equal(1L, third)
        let! other = Db.scalar<int64> app.Db "SELECT count(*) FROM series_follows WHERE series_id = 'NEWSER1' AND domain = 'other.test'" []
        Assert.Equal(0L, other)
    }

[<Fact>]
let ``the sweep asks again after 7 days for instance follows the other side didn't know`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        let known = series.Head
        let stale = series[1]
        do! Db.execute app.Db $"UPDATE series_follows SET state = 'not_found', updated_at = '2026-10-01T00:00:00Z' WHERE series_id = '{stale}'"
        do! Db.execute app.Db $"UPDATE series_follows SET state = 'not_found', updated_at = '2026-10-28T00:00:00Z' WHERE series_id = '{known}'"
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"

        let! queued = inTx app (fun c tx -> sweep c tx None now)
        Assert.Equal(1, queued)

        let! states = Db.dump app.Db $"SELECT series_id, state FROM series_follows WHERE series_id IN ('{known}', '{stale}') ORDER BY 1"
        let stateOf (s: string) = if s = stale then "queued" else "not_found"
        Assert.Equal<string list>([ for s in [ known; stale ] |> List.sort -> $"{s}|{stateOf s}" ], states)
        let! items = outbox app
        Assert.Equal<string list>([ $"follow_series|{stale}@other.test" ], items)
    }

[<Fact>]
let ``an explicit series follow queues one follow, a repeat does nothing, and it turns an instance follow's row explicit`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let s = series.Head

        let! first = inTx app (fun c tx -> addSeries c tx s "other.test" now)
        Assert.True first
        let! again = inTx app (fun c tx -> addSeries c tx s "other.test" now)
        Assert.False again
        let! rows = follows app
        Assert.Equal<string list>([ $"{s}|other.test|series|queued" ], rows)
        let! queued = outbox app
        Assert.Equal<string list>([ $"follow_series|{s}@other.test" ], queued)

        // an instance follow adds the rest, and leaves this one explicit
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        let! mixed = Db.dump app.Db $"SELECT source FROM series_follows WHERE series_id = '{s}'"
        Assert.Equal<string list>([ "series" ], mixed)

        // an explicit follow of an instance-made row promotes it without another follow
        let other = series[1]
        let! before = outbox app
        let! promoted = inTx app (fun c tx -> addSeries c tx other "other.test" now)
        Assert.True promoted
        let! after = outbox app
        Assert.Equal<string list>(before, after)
        let! source = Db.dump app.Db $"SELECT source FROM series_follows WHERE series_id = '{other}'"
        Assert.Equal<string list>([ "series" ], source)
    }

[<Fact>]
let ``a follow that ended without being accepted is asked again when the operator adds it again`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let s = series.Head
        let! _ = inTx app (fun c tx -> addSeries c tx s "other.test" now)

        for state in [ "not_found"; "failed"; "rejected"; "removed" ] do
            do! Db.execute app.Db $"UPDATE series_follows SET state = '{state}', last_error = 'x' WHERE series_id = '{s}'"
            do! Db.execute app.Db "DELETE FROM fediverse_outbox"
            let! again = inTx app (fun c tx -> addSeries c tx s "other.test" now)
            Assert.True(again, state)
            let! rows = follows app
            Assert.Equal<string list>([ $"{s}|other.test|series|queued" ], rows)
            let! items = outbox app
            Assert.Equal<string list>([ $"follow_series|{s}@other.test" ], items)

        // pending and accepted ones are left alone
        for state in [ "pending"; "accepted" ] do
            do! Db.execute app.Db $"UPDATE series_follows SET state = '{state}' WHERE series_id = '{s}'"
            let! again = inTx app (fun c tx -> addSeries c tx s "other.test" now)
            Assert.False(again, state)
    }

[<Fact>]
let ``removing an instance follow removes only what it made and queues an unfollow for each`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let explicit = series.Head
        let! _ = inTx app (fun c tx -> addSeries c tx explicit "other.test" now)
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"

        let! removed = inTx app (fun c tx -> removeInstance c tx "other.test")
        Assert.True removed

        let! rows = follows app
        Assert.Equal<string list>([ $"{explicit}|other.test|series|queued" ], rows)
        let! instances = Db.count app.Db "instance_follows"
        Assert.Equal(0L, instances)

        let! items = outbox app
        Assert.Equal<string list>((series |> List.filter ((<>) explicit) |> List.map (fun s -> $"unfollow_series|{s}@other.test") |> ordinal), items)

        let! again = inTx app (fun c tx -> removeInstance c tx "other.test")
        Assert.False again
    }

[<Fact>]
let ``removing a series follow removes it whatever made it`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let s = series.Head
        let! _ = inTx app (fun c tx -> addSeries c tx s "other.test" now)
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"

        let! removed = inTx app (fun c tx -> removeSeries c tx s "other.test")
        Assert.True removed
        let! items = outbox app
        Assert.Equal<string list>([ $"unfollow_series|{s}@other.test" ], items)
        let! left = Db.count app.Db "series_follows"
        Assert.Equal(0L, left)

        let! again = inTx app (fun c tx -> removeSeries c tx s "other.test")
        Assert.False again
    }

[<Fact>]
let ``a series unfollowed while its instance follow stays is kept as a marker the sweep leaves alone`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let s = series.Head
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"

        let! removed = inTx app (fun c tx -> removeSeries c tx s "other.test")
        Assert.True removed
        let! items = outbox app
        Assert.Equal<string list>([ $"unfollow_series|{s}@other.test" ], items)
        let! marker = Db.dump app.Db $"SELECT source, state FROM series_follows WHERE series_id = '{s}'"
        Assert.Equal<string list>([ "series|unfollowed" ], marker)

        // the sweep doesn't follow it again, and a second removal finds nothing to stop
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"
        let! queued = inTx app (fun c tx -> sweep c tx None now)
        Assert.Equal(0, queued)
        let! again = inTx app (fun c tx -> removeSeries c tx s "other.test")
        Assert.False again
        let! none = outbox app
        Assert.Empty none

        // adding it by hand follows it again
        let! added = inTx app (fun c tx -> addSeries c tx s "other.test" now)
        Assert.True added
        let! back = Db.dump app.Db $"SELECT source, state FROM series_follows WHERE series_id = '{s}'"
        Assert.Equal<string list>([ "series|queued" ], back)

        // removing the instance follow clears a marker without another call
        let! _ = inTx app (fun c tx -> removeSeries c tx s "other.test")
        do! Db.execute app.Db "DELETE FROM fediverse_outbox"
        let! _ = inTx app (fun c tx -> removeInstance c tx "other.test")
        let! left = Db.count app.Db "series_follows"
        Assert.Equal(0L, left)
        let! unfollows = outbox app
        Assert.Equal(series.Length - 1, unfollows.Length)
    }

[<Fact>]
let ``the listing joins the title and the handle and counts an instance's series`` () =
    task {
        let! app, series = sample ()
        use _ = app
        let s = series.Head
        let! _ = inTx app (fun c tx -> addInstance c tx "other.test" now)

        do!
            Db.execute
                app.Db
                $"INSERT INTO remote_actors (id, handle, name) VALUES ('https://other.test/ap/actors/series-{s}', '@frieren@other.test', 'Frieren')"

        use connection = Database.getConnection app.Db
        do! connection.OpenAsync()
        let! instances, rows = list connection

        Assert.Equal(1, instances.Length)
        Assert.Equal("other.test", instances.Head.Domain)
        Assert.Equal(series.Length, instances.Head.SeriesCount)
        Assert.Equal(series.Length, rows.Length)

        let row = rows |> List.find (fun r -> r.SeriesId = s)
        Assert.Equal(Some "@frieren@other.test", row.Handle)
        Assert.Equal("instance", row.Source)
        Assert.Equal("queued", row.State)
        Assert.True row.Title.IsSome
    }

// ---------------------------------------------------------------------------
// Which local episode
// ---------------------------------------------------------------------------

[<Fact>]
let ``a remote episode id we have maps to it, only within the same series`` () =
    task {
        let! app, _ = sample ()
        use _ = app
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL"

        let! found = inTx app (fun c tx -> localEpisodeOf c tx ep.Series ep.Episode None)
        Assert.Equal(Some ep.Episode, found)

        let! otherSeries = inTx app (fun c tx -> localEpisodeOf c tx "OTHERSER1" ep.Episode None)
        Assert.Equal(None, otherSeries)

        let! unknown = inTx app (fun c tx -> localEpisodeOf c tx ep.Series "NOSUCHEP1" None)
        Assert.Equal(None, unknown)
    }

[<Fact>]
let ``a group key finds the episode of our own season, else of a season matched as sharing it`` () =
    task {
        // the enriched 2026 sample: GRK5CNJ14 is an original whose series also holds its dub
        let! app = newFederatedApp ()
        use _ = app
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app

        let! rows =
            Db.dump
                app.Db
                """SELECT dub.series_id, dub.id, orig.id, orig.episode_number
                   FROM cr_episodes dub
                   JOIN cr_season_matches m ON m.season_id = dub.season_id AND m.status = 'matched' AND m.method = 'dub'
                   JOIN cr_episodes orig ON orig.season_id = m.shared_from AND orig.episode_number = dub.episode_number
                   WHERE m.shared_from = 'GRK5CNJ14' AND dub.series_id = orig.series_id
                   ORDER BY dub.id, orig.id LIMIT 1"""

        let parts = rows.Head.Split '|'
        let series, dub, orig, number = parts[0], parts[1], parts[2], parts[3]

        // our own season first
        let! own = inTx app (fun c tx -> localEpisodeOf c tx series "REMOTEEP1" (Some $"GRK5CNJ14#{number}"))
        Assert.Equal(Some orig, own)

        // without the original's number, the dub season shares it
        do! Db.execute app.Db $"UPDATE cr_episodes SET episode_number = 999 WHERE id = '{orig}'"
        let! shared = inTx app (fun c tx -> localEpisodeOf c tx series "REMOTEEP1" (Some $"GRK5CNJ14#{number}"))
        Assert.Equal(Some dub, shared)

        // no number, no key, another series, a key that isn't one
        let! noNumber = inTx app (fun c tx -> localEpisodeOf c tx series "REMOTEEP1" (Some "GRK5CNJ14#98765"))
        Assert.Equal(None, noNumber)
        let! alone = inTx app (fun c tx -> localEpisodeOf c tx series "REMOTEEP1" (Some "GRK5CNJ14"))
        Assert.Equal(None, alone)
        let! noKey = inTx app (fun c tx -> localEpisodeOf c tx series "REMOTEEP1" None)
        Assert.Equal(None, noKey)
        let! otherSeries = inTx app (fun c tx -> localEpisodeOf c tx "OTHERSER1" "REMOTEEP1" (Some $"GRK5CNJ14#{number}"))
        Assert.Equal(None, otherSeries)
    }
