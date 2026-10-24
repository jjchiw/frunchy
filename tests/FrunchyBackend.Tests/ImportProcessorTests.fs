module FrunchyBackend.Tests.ImportProcessorTests

open System
open System.Diagnostics
open System.Text.Json
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private at (iso: string) = DateTime.Parse(iso).ToUniversalTime()

let private pages2026 () =
    use doc = JsonDocument.Parse(load2026Sample ())

    [ for page in doc.RootElement.GetProperty("pages").EnumerateArray() ->
          page.GetProperty("url").GetString(), page.GetProperty("body").GetRawText() ]

let private counts (db) =
    Db.dump db "SELECT entries_received, processed, unresolved, unsupported, failed, status FROM cr_imports ORDER BY received_at"

[<Fact>]
let ``counts add up for both fixtures`` () =
    task {
        use! app = newApp ()
        let! keyA = createUser app.Db "usera"
        let! keyB = createUser app.Db "userb"
        let! _ = submitAccepted app keyA (load2022Envelope ())
        // the 2026 sample shares the real account id, so it goes to a fresh app
        use! app2 = newApp ()
        let! keyC = createUser app2.Db "userc"
        let! _ = submitAccepted app2 keyC (load2026Sample ())
        let! _ = processAll app.Db
        let! _ = processAll app2.Db
        let! rows1 = counts app.Db
        let! rows2 = counts app2.Db
        Assert.Equal<string list>([ "101|100|1|0|0|completed" ], rows1)
        Assert.Equal<string list>([ "400|309|91|0|0|completed" ], rows2)
        ignore keyB
    }

[<Fact>]
let ``submitting the same history 3 times equals submitting it once`` () =
    task {
        use! once = newApp ()
        let! k1 = createUser once.Db "animegolden"
        let! _ = submitAccepted once k1 (load2022Envelope ())
        let! _ = processAll once.Db

        use! thrice = newApp ()
        let! k3 = createUser thrice.Db "animegolden"

        for _ in 1..3 do
            let! _ = submitAccepted thrice k3 (load2022Envelope ())
            ()

        let! _ = processAll thrice.Db
        let! expected = watchEvents once.Db
        let! actual = watchEvents thrice.Db
        Assert.Equal(101, expected.Length)
        Assert.Equal<string list>(expected, actual)
    }

[<Fact>]
let ``arrival order doesn't change the result`` () =
    task {
        let pages = pages2026 ()

        let run (ordered: (string * string) list) =
            task {
                let! app = newApp ()
                let! key = createUser app.Db "animegolden"

                for page in ordered do
                    let! _ = submitAccepted app key (envelopeOf [ page ])
                    ()

                let! _ = processAll app.Db
                let! events = watchEvents app.Db
                let! episodes = episodesTable app.Db
                do! (app :> IAsyncDisposable).DisposeAsync()
                return events, episodes
            }

        let! forward = run pages
        let! reverse = run (List.rev pages)
        Assert.Equal(400, (fst forward).Length)
        Assert.Equal<string list>(fst forward, fst reverse)
        Assert.Equal<string list>(snd forward, snd reverse)
    }

[<Fact>]
let ``an unresolved episode resolves when its details arrive later`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let url = historyUrl RealAccountId
        // newest watch first arrives without details
        let! _ = submitAccepted app key (envelopeOf [ url, pageOf [ unresolvedEntry "EP1" "SER1" (at "2026-03-01T10:00:00Z") ] ])
        let! _ = processAll app.Db
        let! before = Db.scalar<obj> app.Db "SELECT duration_ms FROM cr_episodes WHERE id = 'EP1'" []
        Assert.Equal(box DBNull.Value, before)

        // an older page brings the details
        let! _ = submitAccepted app key (envelopeOf [ url, pageOf [ syntheticEntry "EP1" "SER1" (at "2026-01-01T10:00:00Z") false 300 ] ])
        let! _ = processAll app.Db
        let! duration = Db.scalar<int> app.Db "SELECT duration_ms FROM cr_episodes WHERE id = 'EP1'" []
        let! watched = Db.scalar<DateTime> app.Db "SELECT watched_at FROM watch_events WHERE cr_episode_id = 'EP1'" []
        Assert.Equal(1420000, duration)
        Assert.Equal(at "2026-03-01T10:00:00Z", watched.ToUniversalTime())
    }

[<Fact>]
let ``newer watch dates win and older ones don't overwrite`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let url = historyUrl RealAccountId
        let send (date: string) fully playhead =
            task {
                let! _ = submitAccepted app key (envelopeOf [ url, pageOf [ syntheticEntry "EP1" "SER1" (at date) fully playhead ] ])
                let! _ = processAll app.Db
                ()
            }

        do! send "2023-01-09T10:00:00Z" false 100
        do! send "2023-03-01T10:00:00Z" true 1400 // newer: wins, partial becomes fully watched
        do! send "2023-02-01T10:00:00Z" false 50 // older: ignored
        let! rows = Db.dump app.Db "SELECT watched_at, fully_watched, playhead_s FROM watch_events"
        Assert.Equal(1, rows.Length)
        let! watched = Db.scalar<DateTime> app.Db "SELECT watched_at FROM watch_events" []
        let! fully = Db.scalar<bool> app.Db "SELECT fully_watched FROM watch_events" []
        Assert.Equal(at "2023-03-01T10:00:00Z", watched.ToUniversalTime())
        Assert.True fully
    }

[<Fact>]
let ``invalid entries fail individually and the import still completes`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let page = $"""{{"data":[42,{syntheticEntry "EP1" "SER1" (at "2026-01-01T00:00:00Z") true 1400}]}}"""
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, page ])
        let! _ = processAll app.Db
        let! rows = counts app.Db
        Assert.Equal<string list>([ "2|1|0|0|1|completed" ], rows)
    }

[<Fact>]
let ``a stale processing claim is reclaimed`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! importId = submitAccepted app key (load2022Envelope ())
        do!
            Db.execute
                app.Db
                $"UPDATE cr_imports SET status = 'processing', processing_started_at = now() - interval '11 minutes' WHERE id = '{importId}'"
        let! _ = processAll app.Db
        let! status = Db.scalar<string> app.Db "SELECT status FROM cr_imports" []
        Assert.Equal("completed", status)
    }

[<Fact>]
let ``a fresh processing claim is left alone`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! importId = submitAccepted app key (load2022Envelope ())
        do! Db.execute app.Db $"UPDATE cr_imports SET status = 'processing', processing_started_at = now() WHERE id = '{importId}'"
        let! processed = processAll app.Db
        Assert.Equal(0, processed)
    }

[<Fact>]
let ``several pending imports of one user equal one import with all pages`` () =
    task {
        let pages = pages2026 ()
        use! split = newApp ()
        let! k1 = createUser split.Db "animegolden"

        for page in pages do
            let! _ = submitAccepted split k1 (envelopeOf [ page ])
            ()

        let! _ = processAll split.Db

        use! whole = newApp ()
        let! k2 = createUser whole.Db "animegolden"
        let! _ = submitAccepted whole k2 (envelopeOf pages)
        let! _ = processAll whole.Db

        let! a = watchEvents split.Db
        let! b = watchEvents whole.Db
        Assert.Equal<string list>(a, b)
    }

[<Fact>]
let ``a 64-page first sync is processed well within the target`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (envelopeOf (syntheticPages RealAccountId 64 100))
        let watch = Stopwatch.StartNew()
        let! _ = processAll app.Db
        watch.Stop()
        let! events = Db.count app.Db "watch_events"
        Assert.Equal(6400L, events)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 60.0, $"took {watch.Elapsed}")
    }
