module FrunchyBackend.Tests.RecapTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 029: the weekly recap and the streaks on the dashboard, on a fixed clock

let private now = DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)

let private at (daysAgo: float) = now.AddDays(-daysAgo)

let private utc (year: int) (month: int) (day: int) (hour: int) (minute: int) =
    DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero)

let private startApp (extraConfig: (string * string) list) =
    task {
        let clock = TestClock(Now = now)
        let! db = newDatabase ()
        let! app = createAppWith db extraConfig (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        return app, clock
    }

let private userIdOf (db: DbConfig) (username: string) =
    Db.scalar<Guid> db $"SELECT id FROM users WHERE username = '{username}'" []

/// An episode of a series; `duration` in minutes, None for an unresolved one
let private addEpisode (db: DbConfig) (id: string) (series: string) (duration: int option) =
    let ms =
        match duration with
        | Some m -> string (m * 60000)
        | None -> "NULL"

    Db.execute
        db
        $"INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('{id}', '{series}', 'Title {series}', {ms}) ON CONFLICT DO NOTHING"

/// The one watch row of (user, episode); `playheadS` is what a partly watched one reached
let private watch (db: DbConfig) (userId: Guid) (episode: string) (at: DateTimeOffset) (fully: bool) (playheadS: int) =
    let when' = at.UtcDateTime.ToString("O")

    Db.execute
        db
        $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
            VALUES ('{userId}', '{episode}', '{when'}', {playheadS}, {fully}, gen_random_uuid())"""

/// A fully watched episode of a 24-minute series episode, on its own
let private finished (db: DbConfig) (userId: Guid) (episode: string) (series: string) (when': DateTimeOffset) =
    task {
        do! addEpisode db episode series (Some 24)
        do! watch db userId episode when' true 1440
    }

let private dashboard (app: TestApp) (language: string) =
    task {
        let! response = getWithLanguage app.Client language "/u/animegolden"
        let! html = readHtml response
        return response.StatusCode, html
    }

let private section (hook: string) (html: string) =
    Regex.Match(html, $"<section[^>]*data-{hook}=\"\"[\\s\\S]*?</section>").Value

// ---------------------------------------------------------------------------
// The recap
// ---------------------------------------------------------------------------

[<Fact>]
let ``the recap counts the last 7 days by the dashboard's rules and splits new from continued`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"
        let db = app.Db

        // Series A: before the window it was watched (continued); a1 finished, a2 only partly (10 of 24 minutes)
        do! addEpisode db "a0" "SA" (Some 24)
        do! watch db user "a0" (at 20.0) true 1440
        do! addEpisode db "a1" "SA" (Some 24)
        do! watch db user "a1" (at 2.0) true 1440
        do! addEpisode db "a2" "SA" (Some 24)
        do! watch db user "a2" (at 1.0) false 600

        // Series B: first watched inside the window (new), 20 minutes
        do! addEpisode db "b1" "SB" (Some 20)
        do! watch db user "b1" (at 3.0) true 1200

        // Series C: finished now; an unresolved episode of it was seen long before (continued)
        do! addEpisode db "c0" "SC" None
        do! watch db user "c0" (at 30.0) false 0
        do! finished db user "c1" "SC" (at 1.0)

        // Series E: only a partly watched episode (5 minutes): minutes, but no episode and no series
        do! addEpisode db "e1" "SE" (Some 24)
        do! watch db user "e1" (at 1.0) false 300

        // Series D: outside the window
        do! finished db user "d1" "SD" (at 8.0)

        let! status, html = dashboard app "en"
        Assert.Equal(HttpStatusCode.OK, status)
        let recap = section "recap" html
        Assert.Equal("3", attributeOf "recap-episodes" recap) // a1, b1, c1
        Assert.Equal("83", attributeOf "recap-minutes" recap) // 24 + 10 + 20 + 24 + 5
        Assert.Equal("3", attributeOf "recap-series" recap) // A, B, C
        Assert.Equal("1", attributeOf "recap-new" recap) // B
        Assert.Equal("2", attributeOf "recap-continued" recap) // A, C
        Assert.False(hasHook "recap-empty" recap)
    }

[<Fact>]
let ``an event older than 7 days is outside and the window starts exactly 7 days back`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "in1" "S1" (at 7.0)
        do! finished app.Db user "out1" "S2" (at (7.0 + 1.0 / 86400.0))

        let! _, html = dashboard app "en"
        let recap = section "recap" html
        Assert.Equal("1", attributeOf "recap-episodes" recap)
        Assert.Equal("1", attributeOf "recap-series" recap)
    }

[<Fact>]
let ``an empty window still shows the card with a plain line, and the streaks card has no current streak`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "old1" "S1" (utc 2026 10 1 18 0)

        let! _, html = dashboard app "en"
        let recap = section "recap" html
        Assert.True(hasHook "recap-empty" recap)
        Assert.Equal("0", attributeOf "recap-episodes" recap)
        Assert.Equal("0", attributeOf "recap-minutes" recap)
        Assert.Equal("0", attributeOf "recap-series" recap)
        Assert.Equal("0", attributeOf "recap-new" recap)
        Assert.Equal("0", attributeOf "recap-continued" recap)

        let streaks = section "streaks" html
        Assert.Equal("0", attributeOf "streak-current" streaks)
        Assert.Equal("1", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-10-01", attributeOf "streak-longest-from" streaks)
        Assert.Equal("2026-10-01", attributeOf "streak-longest-to" streaks)
    }

// ---------------------------------------------------------------------------
// The streaks
// ---------------------------------------------------------------------------

[<Fact>]
let ``three consecutive UTC days up to yesterday are a streak of 3, and a day without watching ends it`` () =
    task {
        let! app, clock = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "d1" "S1" (utc 2026 11 2 9 0)
        do! finished app.Db user "d2" "S2" (utc 2026 11 3 20 0)
        do! finished app.Db user "d3" "S3" (utc 2026 11 4 1 0)

        let! _, html = dashboard app "en"
        let streaks = section "streaks" html
        Assert.Equal("3", attributeOf "streak-current" streaks)
        Assert.Equal("3", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-11-02", attributeOf "streak-longest-from" streaks)
        Assert.Equal("2026-11-04", attributeOf "streak-longest-to" streaks)
        Assert.False(hasHook "streak-none" streaks)
        // the dates are plain text, never an element the layout script would shift into the viewer's time zone
        Assert.DoesNotContain("data-fr-time", streaks)

        // a whole day without watching: the current streak is over, the longest and its dates stay
        clock.Now <- utc 2026 11 6 12 0
        let! _, later = dashboard app "en"
        let streaks = section "streaks" later
        Assert.Equal("0", attributeOf "streak-current" streaks)
        Assert.Equal("3", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-11-02", attributeOf "streak-longest-from" streaks)
        Assert.Equal("2026-11-04", attributeOf "streak-longest-to" streaks)
    }

[<Fact>]
let ``a gap splits runs, and a tie for the longest goes to the most recent`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "o1" "S1" (utc 2026 10 20 12 0)
        do! finished app.Db user "o2" "S2" (utc 2026 10 21 12 0)
        do! finished app.Db user "n1" "S3" (utc 2026 11 3 12 0)
        do! finished app.Db user "n2" "S4" (utc 2026 11 4 8 0)

        let! _, html = dashboard app "en"
        let streaks = section "streaks" html
        // two runs of 2 days: the current one (ending yesterday) is also the later one, so it wins the tie
        Assert.Equal("2", attributeOf "streak-current" streaks)
        Assert.Equal("2", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-11-03", attributeOf "streak-longest-from" streaks)
        Assert.Equal("2026-11-04", attributeOf "streak-longest-to" streaks)
    }

[<Fact>]
let ``23:30 and 00:30 UTC are two days`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "x1" "S1" (utc 2026 11 3 23 30)
        do! finished app.Db user "x2" "S2" (utc 2026 11 4 0 30)

        let! _, html = dashboard app "en"
        let streaks = section "streaks" html
        Assert.Equal("2", attributeOf "streak-current" streaks)
        Assert.Equal("2", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-11-03", attributeOf "streak-longest-from" streaks)
        Assert.Equal("2026-11-04", attributeOf "streak-longest-to" streaks)
    }

[<Fact>]
let ``a partly watched episode does not make a streak day`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! finished app.Db user "p1" "S1" (utc 2026 11 3 12 0)
        do! addEpisode app.Db "p2" "S2" (Some 24)
        do! watch app.Db user "p2" (utc 2026 11 4 12 0) false 700
        do! finished app.Db user "p3" "S3" (utc 2026 11 5 8 0)

        let! _, html = dashboard app "en"
        let streaks = section "streaks" html
        // the 4th has only a partial watch: two runs of one day, the later wins the tie, the current one is today's
        Assert.Equal("1", attributeOf "streak-current" streaks)
        Assert.Equal("1", attributeOf "streak-longest" streaks)
        Assert.Equal("2026-11-05", attributeOf "streak-longest-from" streaks)
    }

[<Fact>]
let ``a user with only partly watched episodes has no streak at all`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"

        do! addEpisode app.Db "q1" "S1" (Some 24)
        do! watch app.Db user "q1" (at 1.0) false 300

        let! _, html = dashboard app "en"
        let streaks = section "streaks" html
        Assert.True(hasHook "streak-none" streaks)
        Assert.Equal("0", attributeOf "streak-current" streaks)
        Assert.Equal("0", attributeOf "streak-longest" streaks)
        Assert.False(hasHook "streak-longest-from" streaks)
        Assert.False(hasHook "streak-longest-to" streaks)
    }

// ---------------------------------------------------------------------------
// Who sees it, and when
// ---------------------------------------------------------------------------

[<Fact>]
let ``a private profile's visitor gets the 404 with no recap or streaks, and the owner sees them`` () =
    task {
        let! app, _ = startApp [ Profiles.DefaultVisibilityKey, "private" ]
        use app = app
        let! owner = signedUp app "sub-owner" "animegolden"
        use _owner = owner
        let! user = userIdOf app.Db "animegolden"
        do! finished app.Db user "v1" "S1" (at 1.0)

        use stranger = app.NewBrowser()
        let! hidden = stranger.Get "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, hidden.Status)
        Assert.False(hasHook "recap" hidden.Html)
        Assert.False(hasHook "streaks" hidden.Html)

        let! mine = owner.Get "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, mine.Status)
        Assert.True(hasHook "recap" mine.Html)
        Assert.True(hasHook "streaks" mine.Html)
        Assert.Equal("1", attributeOf "recap-episodes" (section "recap" mine.Html))
    }

[<Fact>]
let ``the empty dashboard has neither card`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"

        let! status, html = dashboard app "en"
        Assert.Equal(HttpStatusCode.OK, status)
        Assert.True(hasHook "empty-state" html)
        Assert.False(hasHook "recap" html)
        Assert.False(hasHook "streaks" html)
    }

[<Fact>]
let ``the cards read in Spanish`` () =
    task {
        let! app, _ = startApp []
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! user = userIdOf app.Db "animegolden"
        do! finished app.Db user "s1" "S1" (utc 2026 11 4 12 0)

        let! _, html = dashboard app "es"
        Assert.Contains("Últimos 7 días", section "recap" html)
        Assert.Contains("Los días se cuentan en UTC.", section "recap" html)
        Assert.Contains("Racha actual", section "streaks" html)
        Assert.Contains("Racha más larga", section "streaks" html)

        let! _, english = dashboard app "en"
        Assert.Contains("Last 7 days", section "recap" english)
        Assert.Contains("Days are counted in UTC.", section "recap" english)
        Assert.Contains("Current streak", section "streaks" english)
    }
