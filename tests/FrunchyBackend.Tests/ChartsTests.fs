module FrunchyBackend.Tests.ChartsTests

open System
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure.Charts
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 012: `Charts.chart` and the people counts on real Postgres. This is the module's smoke test;
// the page tests (T006, T008, T010) build on the same helpers.

let private now = DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)

let private userIds = [ for i in 1..4 -> Guid.Parse $"00000000-0000-0000-0000-00000000000{i}" ]

/// Four users, series A (episodes a1, a2) and B (b1, b2), and C (c1, no duration: unresolved). `watch` rows are
/// (user number, episode, days before `now`).
let private seed (db: DbConfig) (watches: (int * string * float) list) =
    task {
        for i, id in List.indexed userIds do
            do!
                Db.execute
                    db
                    $"INSERT INTO users (id, username, display_name) VALUES ('{id}', 'user{i + 1}', 'User {i + 1}')"

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES
                     ('SA', 'Alpha', 'alpha', now()), ('SB', 'Beta', 'beta', now());
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms, thumbnail_url) VALUES
                     ('a1', 'SA', 'Alpha', 1440000, 'https://img/a1.jpg'), ('a2', 'SA', 'Alpha', 1440000, NULL),
                     ('b1', 'SB', 'Beta', 1440000, NULL), ('b2', 'SB', 'Beta', 1440000, NULL), ('c1', 'SC', 'Gamma', NULL, NULL)"""

        for user, episode, daysAgo in watches do
            let at = now.AddDays(-daysAgo).UtcDateTime.ToString("O")

            do!
                Db.execute
                    db
                    $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
                       VALUES ('{userIds[user - 1]}', '{episode}', '{at}', 1400, true, gen_random_uuid())"""
    }

let private rowOf (chart: Chart) (id: string) = chart.Rows |> List.find (fun r -> r.SeriesId = id)

[<Fact>]
let ``the week chart ranks by people then episodes, hides series below the minimum and flags the viewer's`` () =
    task {
        let! app = newApp ()
        use app = app

        do!
            seed
                app.Db
                [ // A: 3 people, 4 episodes this week
                  1, "a1", 1.0; 1, "a2", 1.0; 2, "a1", 2.0; 3, "a2", 3.0
                  // B: 2 people this week, 3 people last week
                  1, "b1", 2.0; 2, "b1", 3.0; 1, "b2", 8.0; 2, "b2", 9.0; 3, "b2", 10.0
                  // an unresolved episode never counts
                  1, "c1", 1.0; 2, "c1", 1.0; 3, "c1", 1.0 ]

        let! three = chart app.Db Week 3 now None
        Assert.Equal<string list>([ "SA" ], three.Rows |> List.map _.SeriesId)
        Assert.Equal(3, three.MinPeople)
        Assert.Equal(3, (rowOf three "SA").People)
        Assert.Equal(4, (rowOf three "SA").Episodes)
        Assert.Equal(Some "alpha", (rowOf three "SA").Slug)
        Assert.Equal(Some(Thumbnail "https://img/a1.jpg"), (rowOf three "SA").Poster)
        Assert.False (rowOf three "SA").Mine

        let! two = chart app.Db Week 2 now (Some userIds[3])
        Assert.Equal<string list>([ "SA"; "SB" ], two.Rows |> List.map _.SeriesId)
        // user 4 watched nothing
        Assert.All(two.Rows, fun r -> Assert.False r.Mine)

        // B ranked 1st last week (3 people) and is 2nd now; A was below... A had 0 people last week: new
        Assert.Equal(Some(Down 1), (rowOf two "SB").Move)
        Assert.Equal(Some New, (rowOf two "SA").Move)

        // trend: oldest first, B's 3 people sit in the block ending 7 days ago (index 6), 2 now (index 7)
        Assert.Equal<int list>([ 0; 0; 0; 0; 0; 0; 3; 2 ], (rowOf two "SB").Trend)
        Assert.Equal<int list>([ 0; 0; 0; 0; 0; 0; 0; 3 ], (rowOf two "SA").Trend)

        // below 2 is clamped to 2: a single person never makes a row
        let! one = chart app.Db Week 1 now None
        Assert.Equal(2, one.MinPeople)
        Assert.Equal(2, one.Rows.Length)

        let! viewer = chart app.Db Week 2 now (Some userIds[0])
        Assert.All(viewer.Rows, fun r -> Assert.True r.Mine)
    }

[<Fact>]
let ``the season and all-time charts use their own windows`` () =
    task {
        let! app = newApp ()
        use app = app

        do!
            seed
                app.Db
                [ // Fall 2026 starts on 1 October: 15 days before `now`
                  1, "a1", 5.0; 2, "a1", 6.0; 3, "a1", 14.0
                  // B: all three watches were in summer
                  1, "b1", 30.0; 2, "b1", 31.0; 3, "b1", 40.0 ]

        let! season = chart app.Db Season 3 now None
        Assert.Equal(Some("fall", 2026), season.Season)
        Assert.Equal<string list>([ "SA" ], season.Rows |> List.map _.SeriesId)
        Assert.Empty(season.Rows |> List.choose _.Move)

        let! all = chart app.Db AllTime 3 now None
        Assert.Equal<string list>([ "SA"; "SB" ], all.Rows |> List.map _.SeriesId)
        Assert.Equal(None, all.Season)

        let! empty = chart app.Db Week 3 now None
        Assert.Empty empty.Rows
    }

[<Fact>]
let ``people counts need the minimum and count each user once`` () =
    task {
        let! app = newApp ()
        use app = app

        do!
            seed
                app.Db
                [ 1, "a1", 1.0; 1, "a2", 1.0; 2, "a1", 20.0; 3, "a2", 90.0; 1, "b1", 1.0; 2, "b1", 1.0
                  // an unresolved episode doesn't count
                  4, "c1", 1.0 ]

        // A: users 1, 2 and 3, user 1 once although they watched two episodes
        let! a3 = seriesPeople app.Db "SA" 3
        let! a4 = seriesPeople app.Db "SA" 4
        let! b2 = seriesPeople app.Db "SB" 2
        let! b3 = seriesPeople app.Db "SB" 3
        let! b1 = seriesPeople app.Db "SB" 1 // 1 clamps to 2, and B has exactly 2
        let! c2 = seriesPeople app.Db "SC" 2
        Assert.Equal(Some 3, a3)
        Assert.Equal(None, a4)
        Assert.Equal(Some 2, b2)
        Assert.Equal(None, b3)
        Assert.Equal(Some 2, b1)
        Assert.Equal(None, c2)

        // an episode group: users who watched any of them
        let! both = episodePeople app.Db [ "a1"; "a2" ] 3
        let! one = episodePeople app.Db [ "a2" ] 3
        let! none = episodePeople app.Db [] 2
        Assert.Equal(Some 3, both)
        Assert.Equal(None, one)
        Assert.Equal(None, none)
    }

// ===========================================================================
// T006: the chart pages (contracts/charts.md). The expected figures are computed in F# from the raw
// watch rows, not by calling Charts.fs or its SQL.
// ===========================================================================

open System.Net
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests

type private Watch =
    { User: Guid
      Series: string
      Episode: string
      At: DateTimeOffset }

type private Expected = { Id: string; Rank: int; People: int; Episodes: int }

type private Parsed =
    { Id: string
      Rank: int
      People: int
      Episodes: int
      Block: string }

let private fallStart = DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)

/// (series id, title, slug). "GSER0007" has no slug; "GSER0008" also has an unresolved episode
let private catalog =
    [ "GSER0001", "Anime One", Some "anime-one"
      "GSER0002", "Anime Two", Some "anime-two"
      "GSER0003", "Anime Three", Some "anime-three"
      "GSER0004", "Anime Four", Some "anime-four"
      "GSER0005", "Anime Five", Some "anime-five"
      "GSER0006", "Tom & Jerry's <Big> Show", Some "tom-and-jerry"
      "GSER0007", "Anime Seven", None
      "GSER0008", "Anime Eight", Some "anime-eight" ]

let private titles = catalog |> List.map (fun (id, title, _) -> id, title) |> Map.ofList
let private episodesOf (series: string) = [ for n in 1..3 -> $"{series}E{n}" ]

let private userGuid (n: int) = Guid.Parse("00000000-0000-0000-0000-0000000000" + n.ToString "00")

let private sqlText (text: string) = text.Replace("'", "''")

let private insertCatalog (db: DbConfig) =
    task {
        for id, title, slug in catalog do
            let slug = slug |> Option.map (fun s -> $"'{s}'") |> Option.defaultValue "NULL"
            do! Db.execute db $"INSERT INTO cr_series (id, title, slug, source_at) VALUES ('{id}', '{sqlText title}', {slug}, now())"

            for e in episodesOf id do
                do!
                    Db.execute
                        db
                        $"INSERT INTO cr_episodes (id, series_id, series_title, duration_ms, thumbnail_url) VALUES ('{e}', '{id}', '{sqlText title}', 1440000, 'https://img/{e}.jpg')"

        // an unresolved episode (no length) of the last series: it never counts
        do! Db.execute db "INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('GSER0008E9', 'GSER0008', 'Anime Eight', NULL)"
    }

let private addWatch (db: DbConfig) (user: Guid) (episode: string) (at: DateTimeOffset) =
    Db.execute
        db
        $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
            VALUES ('{user}', '{episode}', '{at.UtcDateTime:O}', 1400, true, gen_random_uuid())
            ON CONFLICT (user_id, cr_episode_id) DO UPDATE SET watched_at = EXCLUDED.watched_at"""

let private insertUsers (db: DbConfig) (count: int) =
    task {
        for i in 1..count do
            do! Db.execute db $"INSERT INTO users (id, username, display_name) VALUES ('{userGuid i}', 'user{i}', 'User {i}')"
    }

/// Five SQL users with random overlapping history over the last 140 days (a third of it in the last week,
/// a quarter in the week before, a few days inside the current season), plus boundary rows
let private seedRandom (db: DbConfig) =
    task {
        let rng = Random 2026
        let chosen = Collections.Generic.Dictionary<(int * string), DateTimeOffset>()

        for user in 1..5 do
            for id, _, _ in catalog do
                if rng.NextDouble() < 0.75 then
                    for e in episodesOf id do
                        if rng.NextDouble() < 0.7 then
                            let days =
                                match rng.Next 20 with
                                | n when n < 7 -> rng.NextDouble() * 7.0
                                | n when n < 12 -> 7.0 + rng.NextDouble() * 7.0
                                | n when n < 14 -> 14.0 + rng.NextDouble() * 1.0
                                | _ -> rng.NextDouble() * 140.0

                            chosen[(user, e)] <- now.AddDays(-days)

        // the unresolved episode, watched by everybody
        for user in 1..5 do
            chosen[(user, "GSER0008E9")] <- now.AddDays -1.0

        // boundaries: the week window is [now - 7 days, now), the season window starts at 1 October 00:00 UTC
        chosen[(1, "GSER0002E2")] <- now.AddDays -7.0
        chosen[(2, "GSER0002E2")] <- now.AddDays(-7.0).AddSeconds -1.0
        chosen[(3, "GSER0002E2")] <- now
        chosen[(4, "GSER0002E3")] <- fallStart
        chosen[(5, "GSER0002E3")] <- fallStart.AddSeconds -1.0

        for KeyValue((user, episode), at) in chosen do
            do! addWatch db (userGuid user) episode at
    }

let private loadWatches (db: DbConfig) : Task<Watch list> =
    task {
        let! lines =
            Db.dump
                db
                """SELECT w.user_id, e.series_id, e.id, (extract(epoch from w.watched_at) * 1000)::bigint
                   FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                   WHERE e.duration_ms IS NOT NULL"""

        return
            [ for line in lines do
                  let p = line.Split '|'

                  { User = Guid.Parse p[0]
                    Series = p[1]
                    Episode = p[2]
                    At = DateTimeOffset.FromUnixTimeMilliseconds(int64 p[3]) } ]
    }

let private inWindow (window: string) (watches: Watch list) =
    match window with
    | "week" -> watches |> List.filter (fun w -> w.At >= now.AddDays -7.0 && w.At < now)
    | "season" -> watches |> List.filter (fun w -> w.At >= fallStart && w.At < DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))
    | _ -> watches

/// The chart's rows: people, then episodes, then title, then id; at least `minimum` people; at most 20
let private expectedChart (minimum: int) (watches: Watch list) : Expected list =
    watches
    |> List.groupBy _.Series
    |> List.map (fun (series, ws) -> series, (ws |> List.map _.User |> List.distinct |> List.length), ws.Length)
    |> List.filter (fun (_, people, _) -> people >= minimum)
    |> List.sortBy (fun (series, people, episodes) -> -people, -episodes, titles[series], series)
    |> List.truncate 20
    |> List.mapi (fun i (series, people, episodes) -> { Id = series; Rank = i + 1; People = people; Episodes = episodes })

let private path (window: string) =
    match window with
    | "week" -> "/charts"
    | "season" -> "/charts/season"
    | _ -> "/charts/all"

let private appAt (db: DbConfig) (config: (string * string) list) (at: DateTimeOffset) =
    let clock = TestClock(Now = at)
    createAppWith db config (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

let private pageOf (client: Net.Http.HttpClient) (language: string) (url: string) =
    task {
        let! response = getWithLanguage client language url
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        return! readHtml response
    }

let private parseRows (html: string) : Parsed list =
    [ for m in Regex.Matches(html, "<li\\b[^>]*\\bdata-chart-row=\"([^\"]*)\"[^>]*>(.*?)</li>", RegexOptions.Singleline) ->
          let tag = Regex.Match(m.Value, "^<li\\b[^>]*>").Value
          let a (name: string) = Regex.Match(tag, $"data-{name}=\"([^\"]*)\"").Groups[1].Value

          { Id = m.Groups[1].Value
            Rank = int (a "rank")
            People = int (a "people")
            Episodes = int (a "episodes")
            Block = m.Value } ]

let private asExpected (rows: Parsed list) : Expected list =
    rows |> List.map (fun r -> { Id = r.Id; Rank = r.Rank; People = r.People; Episodes = r.Episodes })

let private attrOf (hook: string) (html: string) =
    Regex.Match(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}=\"([^\"]*)\"[^>]*>").Groups[1].Value

let private fixtureDb () =
    task {
        let! db = newDatabase ()
        do! insertCatalog db
        do! insertUsers db 5
        do! seedRandom db
        return db
    }

[<Fact>]
let ``every row of the three charts equals the figures computed from the watch rows, for minimums 2 to 5`` () =
    task {
        let! db = fixtureDb ()
        let! watches = loadWatches db

        // sanity: the fixture gives every window something to compare at the lowest minimum
        for window in [ "week"; "season"; "all" ] do
            Assert.NotEmpty(expectedChart 2 (inWindow window watches))

        for minimum in 2..5 do
            use! app = appAt db [ "Frunchy:Charts:MinPeople", string minimum ] now

            for window in [ "week"; "season"; "all" ] do
                let! html = pageOf app.Client "en" (path window)
                let expected = expectedChart minimum (inWindow window watches)
                let rows = parseRows html

                Assert.Equal<Expected list>(expected, asExpected rows)
                Assert.Equal(window, attrOf "charts" html)
                Assert.Equal(string minimum, attrOf "min-people" html)

                // the minimum: no row below it
                Assert.All(rows, fun r -> Assert.True(r.People >= minimum, $"{r.Id} has {r.People} people at minimum {minimum}"))

                // titles are shown (HTML-escaped on the wire, decoded here)
                for r in rows do
                    Assert.Contains(titles[r.Id], r.Block)

                // the empty state shows exactly when there are no rows
                Assert.Equal(rows.IsEmpty, html.Contains "data-chart-empty")

                // the unresolved episode never counts: "GSER0008" has 3 known episodes and 5 users
                for r in rows |> List.filter (fun r -> r.Id = "GSER0008") do
                    Assert.True(r.Episodes <= 15)
    }

[<Fact>]
let ``the week window is half open and the season starts at 1 October 00:00 UTC`` () =
    task {
        let! db = fixtureDb ()
        let! watches = loadWatches db
        use! app = appAt db [ "Frunchy:Charts:MinPeople", "2" ] now

        // GSER0002E2 is watched exactly at now - 7 days (in), one second earlier (out) and at now (out of the week);
        // GSER0002E3 exactly at the start of the season (in) and one second earlier (out)
        let! week = pageOf app.Client "en" "/charts"
        let! season = pageOf app.Client "en" "/charts/season"
        let two (html: string) = parseRows html |> List.tryFind (fun r -> r.Id = "GSER0002")

        let expectedEpisodes (window: string) =
            inWindow window watches |> List.filter (fun w -> w.Series = "GSER0002") |> List.length

        Assert.Equal(Some(expectedEpisodes "week"), two week |> Option.map _.Episodes)
        Assert.Equal(Some(expectedEpisodes "season"), two season |> Option.map _.Episodes)

        // the boundary rows really are on the sides the contract says
        Assert.Contains(watches, fun w -> w.Episode = "GSER0002E3" && w.At = fallStart)
        Assert.Contains(watches, fun w -> w.Episode = "GSER0002E3" && w.At = fallStart.AddSeconds -1.0)
        Assert.Contains(inWindow "season" watches, fun w -> w.Episode = "GSER0002E3" && w.At = fallStart)
        Assert.DoesNotContain(inWindow "season" watches, fun w -> w.Episode = "GSER0002E3" && w.At = fallStart.AddSeconds -1.0)
        Assert.Contains(inWindow "week" watches, fun w -> w.Episode = "GSER0002E2" && w.At = now.AddDays -7.0)
        Assert.DoesNotContain(inWindow "week" watches, fun w -> w.Episode = "GSER0002E2" && w.At = now)
    }

/// A tie fixture: the ids sort the opposite way to the titles
let private tieDb () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 3

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES
                     ('TIE00001', 'Zebra', NULL, now()), ('TIE00002', 'Aardvark', NULL, now()),
                     ('TIE00003', 'Mango', NULL, now()), ('TIE00004', 'Quince', NULL, now());
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES
                     ('TIE1E1', 'TIE00001', 'Zebra', 1440000), ('TIE1E2', 'TIE00001', 'Zebra', 1440000),
                     ('TIE2E1', 'TIE00002', 'Aardvark', 1440000), ('TIE2E2', 'TIE00002', 'Aardvark', 1440000),
                     ('TIE3E1', 'TIE00003', 'Mango', 1440000), ('TIE3E2', 'TIE00003', 'Mango', 1440000),
                     ('TIE4E1', 'TIE00004', 'Quince', 1440000), ('TIE4E2', 'TIE00004', 'Quince', 1440000)"""

        let at = now.AddDays -1.0

        // Mango: 3 people, 6 episodes. Quince: 3 people, 3 episodes.
        for user in 1..3 do
            for e in [ "TIE3E1"; "TIE4E1" ] do
                do! addWatch db (userGuid user) e at

        for user in 1..3 do
            do! addWatch db (userGuid user) "TIE3E2" at

        // Zebra and Aardvark: 3 people, 4 episodes each
        for user in 1..3 do
            do! addWatch db (userGuid user) "TIE1E1" at
            do! addWatch db (userGuid user) "TIE2E1" at

        do! addWatch db (userGuid 1) "TIE1E2" at
        do! addWatch db (userGuid 1) "TIE2E2" at
        return db
    }

[<Fact>]
let ``ties on people are broken by episodes, then by title, then by id`` () =
    task {
        let! db = tieDb ()
        use! app = appAt db [] now
        let! html = pageOf app.Client "en" "/charts"
        let rows = parseRows html

        // Mango has 3 people and 6 episodes, Aardvark and Zebra 3 people and 4 (Aardvark first by title although its
        // id sorts after Zebra's), Quince 3 people and 3
        Assert.Equal<string list>([ "TIE00003"; "TIE00002"; "TIE00001"; "TIE00004" ], rows |> List.map _.Id)
        Assert.Equal<int list>([ 6; 4; 4; 3 ], rows |> List.map _.Episodes)
        Assert.Equal<int list>([ 1; 2; 3; 4 ], rows |> List.map _.Rank)
    }

[<Fact>]
let ``the viewer's link shows only for a signed-in viewer who watched the series, the Crunchyroll link only with a slug`` () =
    task {
        let! db = fixtureDb ()
        use! app = appAt db [ "Frunchy:Charts:MinPeople", "2" ] now
        let! viewer = signedUp app "sub-viewer" "viewer"
        use viewer = viewer
        let! viewerId = Db.scalar<Guid> db "SELECT id FROM users WHERE username = @u" [ "u", box "viewer" ]

        // the viewer watched series 1 and 3 only
        do! addWatch db viewerId "GSER0001E1" (now.AddDays -2.0)
        do! addWatch db viewerId "GSER0003E1" (now.AddDays -300.0)

        let! watches = loadWatches db
        let expected = expectedChart 2 (inWindow "all" watches)
        Assert.Contains(expected, fun e -> e.Id = "GSER0001")
        Assert.Contains(expected, fun e -> e.Id = "GSER0002")
        Assert.Contains(expected, fun e -> e.Id = "GSER0007")

        let! signedIn = viewer.Get "/charts/all"
        Assert.Equal(HttpStatusCode.OK, signedIn.Status)
        let html = WebUtility.HtmlDecode signedIn.Html
        Assert.Equal<Expected list>(expected, asExpected (parseRows html))

        for r in parseRows html do
            let mine = r.Block.Contains "data-chart-mine"
            Assert.Equal(r.Id = "GSER0001" || r.Id = "GSER0003", mine)

            if mine then
                Assert.Contains($"href=\"/u/viewer/anime/{r.Id}\"", r.Block)

            // Crunchyroll: with a slug only (GSER0007 has none)
            let hasLink = r.Block.Contains "data-cr-series-link"
            Assert.Equal(r.Id <> "GSER0007", hasLink)

            if hasLink then
                let slug = catalog |> List.pick (fun (id, _, s) -> if id = r.Id then s else None)
                Assert.Contains($"href=\"https://www.crunchyroll.com/series/{r.Id}/{slug}\"", r.Block)
                Assert.Contains("rel=\"noopener\"", r.Block)
                Assert.Contains("target=\"_blank\"", r.Block)

        // a signed-out visitor sees no personal link, no user link and no username on the chart
        let! anonymous = pageOf app.Client "en" "/charts/all"
        Assert.DoesNotContain("data-chart-mine", anonymous)
        Assert.DoesNotContain("/u/", anonymous)
        Assert.DoesNotContain("User 1", anonymous)
    }

[<Fact>]
let ``an empty chart shows the empty state and a season name follows the clock at its boundaries`` () =
    task {
        let! db = newDatabase ()

        for window in [ "week"; "season"; "all" ] do
            use! app = appAt db [] now
            let! html = pageOf app.Client "en" (path window)
            Assert.Contains("data-chart-empty", html)
            Assert.Empty(parseRows html)

        let seasonAt (at: DateTimeOffset) =
            task {
                use! app = appAt db [] at
                let! html = pageOf app.Client "en" "/charts/season"
                return attrOf "season" html
            }

        let! lastSecondOfSummer = seasonAt (DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero))
        let! firstSecondOfFall = seasonAt (DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))
        let! lastSecondOfFall = seasonAt (DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero))
        let! firstSecondOfWinter = seasonAt (DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))
        Assert.Equal("summer-2026", lastSecondOfSummer)
        Assert.Equal("fall-2026", firstSecondOfFall)
        Assert.Equal("fall-2026", lastSecondOfFall)
        Assert.Equal("winter-2027", firstSecondOfWinter)

        // only the season chart names a season
        use! app = appAt db [] now
        let! week = pageOf app.Client "en" "/charts"
        let! all = pageOf app.Client "en" "/charts/all"
        Assert.DoesNotContain("data-season", week)
        Assert.DoesNotContain("data-season", all)
    }

[<Fact>]
let ``the Charts link is in every page's header, for visitors and signed-in users, and the tabs mark the active chart`` () =
    task {
        let! app = newApp ()
        use app = app
        let! viewer = signedUp app "sub-nav" "navuser"
        use viewer = viewer

        let hasLink (html: string) =
            Regex.Matches(html, "<a\\b[^>]*>")
            |> Seq.exists (fun m -> m.Value.Contains "data-nav=\"charts\"" && m.Value.Contains "href=\"/charts\"")

        for url in [ "/"; "/u/navuser"; "/charts"; "/signin" ] do
            let! anonymous = pageOf app.Client "en" url
            Assert.True(hasLink anonymous, $"signed out, {url}")
            let! page = viewer.Get url
            Assert.True(hasLink page.Html, $"signed in, {url}")

        for window in [ "week"; "season"; "all" ] do
            let! html = pageOf app.Client "en" (path window)

            let tabs =
                [ for m in Regex.Matches(html, "<a\\b[^>]*\\bdata-chart-tab=\"([^\"]*)\"[^>]*>") -> m.Groups[1].Value, m.Value.Contains "aria-current=\"page\"" ]

            Assert.Equal<(string * bool) list>([ "week", (window = "week"); "season", (window = "season"); "all", (window = "all") ], tabs)
    }

[<Fact>]
let ``the charts speak English and Spanish from the language header`` () =
    task {
        let! db = tieDb ()
        use! app = appAt db [] now

        let! en = pageOf app.Client "en" "/charts"
        Assert.Contains("This week", en)
        Assert.Contains("This season", en)
        Assert.Contains("All time", en)
        Assert.Contains("3 people · 6 episodes", en)
        Assert.Contains("Series appear once 3 people have watched them.", en)

        let! es = pageOf app.Client "es" "/charts"
        Assert.Contains("Esta semana", es)
        Assert.Contains("Esta temporada", es)
        Assert.Contains("Histórico", es)
        Assert.Contains("3 personas · 6 episodios", es)
        Assert.Contains("lang=\"es\"", es)

        let! season = pageOf app.Client "es" "/charts/season"
        Assert.Contains("Otoño 2026", season)
        let! seasonEn = pageOf app.Client "en" "/charts/season"
        Assert.Contains("Fall 2026", seasonEn)

        // the empty state is localized too
        let! emptyDb = newDatabase ()
        use! empty = appAt emptyDb [] now
        let! emptyEs = pageOf empty.Client "es" "/charts"
        Assert.Contains("Todavía no hay series en este ranking", emptyEs)
    }

// ===========================================================================
// T008: movement and trend on the week chart, and the speed of the three charts at 2 million events
// ===========================================================================

/// The week before the current one: [now - 14 days, now - 7 days)
let private previousWeek (watches: Watch list) =
    watches |> List.filter (fun w -> w.At >= now.AddDays -14.0 && w.At < now.AddDays -7.0)

/// Last week's ranking with no limit: series id to its 1-based rank
let private previousRanks (minimum: int) (watches: Watch list) : Map<string, int> =
    previousWeek watches
    |> List.groupBy _.Series
    |> List.map (fun (series, ws) -> series, (ws |> List.map _.User |> List.distinct |> List.length), ws.Length)
    |> List.filter (fun (_, people, _) -> people >= minimum)
    |> List.sortBy (fun (series, people, episodes) -> -people, -episodes, titles[series], series)
    |> List.mapi (fun i (series, _, _) -> series, i + 1)
    |> Map.ofList

/// (move, by) for a series ranked `rank` now
let private expectedMove (previous: Map<string, int>) (series: string) (rank: int) : string * int option =
    match previous.TryFind series with
    | None -> "new", None
    | Some before when before > rank -> "up", Some(before - rank)
    | Some before when before < rank -> "down", Some(rank - before)
    | Some _ -> "same", None

/// The 8 weekly blocks, oldest first: block i is [now - 7 * (8 - i) days, now - 7 * (7 - i) days)
let private expectedTrend (series: string) (watches: Watch list) : int list =
    [ for i in 0..7 ->
          let from = now.AddDays(-7.0 * float (8 - i))
          let until = now.AddDays(-7.0 * float (7 - i))

          watches
          |> List.filter (fun w -> w.Series = series && w.At >= from && w.At < until)
          |> List.map _.User
          |> List.distinct
          |> List.length ]

let private moveOf (r: Parsed) : (string * int option) option =
    let kind = Regex.Match(r.Block, "data-move=\"([^\"]*)\"")

    if kind.Success then
        let by = Regex.Match(r.Block, "data-move-by=\"([^\"]*)\"")
        Some(kind.Groups[1].Value, (if by.Success then Some(int by.Groups[1].Value) else None))
    else
        None

let private trendOf (r: Parsed) : int list option =
    let m = Regex.Match(r.Block, "data-trend=\"([^\"]*)\"")
    if m.Success then Some(m.Groups[1].Value.Split ',' |> Array.map int |> List.ofArray) else None

[<Fact>]
let ``movement and the 8-week trend of every row of the week chart equal the figures from the watch rows`` () =
    task {
        let! db = fixtureDb ()
        let! watches = loadWatches db

        for minimum in 2..4 do
            use! app = appAt db [ "Frunchy:Charts:MinPeople", string minimum ] now
            let! html = pageOf app.Client "en" "/charts"
            let rows = parseRows html
            let previous = previousRanks minimum watches
            Assert.NotEmpty rows

            for r in rows do
                Assert.Equal(Some(expectedMove previous r.Id r.Rank), moveOf r)
                Assert.Equal(Some(expectedTrend r.Id watches), trendOf r)

                // one accessible image per row: role, a text label with the same values, a single polyline
                let values = (trendOf r).Value
                Assert.Contains("role=\"img\"", r.Block)
                Assert.Contains("aria-label=\"Last 8 weeks: " + String.Join(", ", values) + "\"", r.Block)
                Assert.Equal(1, Regex.Matches(r.Block, "<polyline\\b").Count)
                Assert.Contains("stroke=\"currentColor\"", r.Block)

                // the points: 8, left to right, the row's own maximum at the top and zero at the bottom
                let points =
                    Regex.Match(r.Block, "points=\"([^\"]*)\"").Groups[1].Value.Split ' '
                    |> Array.map (fun p -> let xy = p.Split ',' in int xy[0], int xy[1])

                Assert.Equal(8, points.Length)
                Assert.Equal<int[]>(Array.sort (points |> Array.map fst), points |> Array.map fst)
                Assert.Equal(points |> Array.map fst |> Array.distinct |> Array.length, 8)
                Assert.All(points, fun (x, y) -> Assert.True(x >= 0 && x <= 80 && y >= 0 && y <= 24))
                let top = List.max values

                for (_, y), v in Array.zip points (Array.ofList values) do
                    if v = top then Assert.Equal(2, y)
                    if v = 0 then Assert.Equal(22, y)
    }

[<Fact>]
let ``up, down, same and new from two weeks of known ranks, a series below last week's minimum counting as new`` () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 5

        let names = [ 1, "Alpha"; 2, "Bravo"; 3, "Charlie"; 4, "Delta"; 5, "Echo"; 6, "Foxtrot" ]

        for k, name in names do
            do! Db.execute db $"INSERT INTO cr_series (id, title, slug, source_at) VALUES ('MOV0000{k}', '{name}', NULL, now())"

            for e in 1..3 do
                do!
                    Db.execute
                        db
                        $"INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('MOV{k}E{e}', 'MOV0000{k}', '{name}', 1440000)"

        let thisWeek = now.AddDays -2.0
        let lastWeek = now.AddDays -9.0
        let watch (users: int list) (episode: string) (at: DateTimeOffset) =
            task {
                for u in users do
                    do! addWatch db (userGuid u) episode at
            }

        // last week at minimum 3: Alpha 5 people, Bravo 4, Charlie 3, Foxtrot 3 (Charlie before Foxtrot by title);
        // Delta 2 people, below the minimum, Echo none
        do! watch [ 1; 2; 3; 4; 5 ] "MOV1E2" lastWeek
        do! watch [ 1; 2; 3; 4 ] "MOV2E2" lastWeek
        do! watch [ 1; 2; 3 ] "MOV3E2" lastWeek
        do! watch [ 1; 2; 3 ] "MOV6E2" lastWeek
        do! watch [ 1; 2 ] "MOV4E2" lastWeek

        // this week: Foxtrot 5 people and 6 episodes, Bravo 5 and 5, Alpha 4, Charlie 3 and 4 episodes,
        // Delta 3 and 3, Echo 3 and 3
        do! watch [ 1; 2; 3; 4; 5 ] "MOV6E1" thisWeek
        do! watch [ 1 ] "MOV6E3" thisWeek
        do! watch [ 1; 2; 3; 4; 5 ] "MOV2E1" thisWeek
        do! watch [ 1; 2; 3; 4 ] "MOV1E1" thisWeek
        do! watch [ 1; 2; 3 ] "MOV3E1" thisWeek
        do! watch [ 1 ] "MOV3E3" thisWeek
        do! watch [ 1; 2; 3 ] "MOV4E1" thisWeek
        do! watch [ 3; 4; 5 ] "MOV5E1" thisWeek

        use! app = appAt db [] now
        let! html = pageOf app.Client "en" "/charts"
        let rows = parseRows html

        Assert.Equal<string list>(
            [ "MOV00006"; "MOV00002"; "MOV00001"; "MOV00003"; "MOV00004"; "MOV00005" ],
            rows |> List.map _.Id
        )

        let moves = rows |> List.map (fun r -> r.Id, moveOf r) |> Map.ofList
        Assert.Equal(Some("up", Some 3), moves["MOV00006"]) // 4th last week, 1st now
        Assert.Equal(Some("same", None), moves["MOV00002"]) // 2nd both weeks
        Assert.Equal(Some("down", Some 2), moves["MOV00001"]) // 1st last week, 3rd now
        Assert.Equal(Some("down", Some 1), moves["MOV00003"]) // 3rd last week, 4th now
        Assert.Equal(Some("new", None), moves["MOV00004"]) // 2 people last week: below the minimum
        Assert.Equal(Some("new", None), moves["MOV00005"]) // unwatched last week

        // the trend of Alpha: 5 people the week before last... in the 7th block, 4 in the 8th
        let alpha = rows |> List.find (fun r -> r.Id = "MOV00001")
        Assert.Equal(Some [ 0; 0; 0; 0; 0; 0; 5; 4 ], trendOf alpha)
        let echo = rows |> List.find (fun r -> r.Id = "MOV00005")
        Assert.Equal(Some [ 0; 0; 0; 0; 0; 0; 0; 3 ], trendOf echo)

        // the hooks carry the number only for up and down; the icon and the words are shown
        Assert.Contains("Up 3", (rows |> List.find (fun r -> r.Id = "MOV00006")).Block)
        Assert.Contains("Down 2", alpha.Block)
        Assert.Contains("Same", (rows |> List.find (fun r -> r.Id = "MOV00002")).Block)
        Assert.Contains("New", echo.Block)
        Assert.Contains("trending_up", (rows |> List.find (fun r -> r.Id = "MOV00006")).Block)
        Assert.Contains("trending_down", alpha.Block)

        let! es = pageOf app.Client "es" "/charts"
        Assert.Contains("Sube 3", es)
        Assert.Contains("Baja 2", es)
        Assert.Contains("Igual", es)
        Assert.Contains("Nuevo", es)
        Assert.Contains("Últimas 8 semanas: 0, 0, 0, 0, 0, 0, 5, 4", es)
    }

[<Fact>]
let ``the season and all-time charts carry no movement and no trend`` () =
    task {
        let! db = fixtureDb ()
        use! app = appAt db [ "Frunchy:Charts:MinPeople", "2" ] now

        for window in [ "season"; "all" ] do
            let! html = pageOf app.Client "en" (path window)
            Assert.NotEmpty(parseRows html)
            Assert.DoesNotContain("data-move", html)
            Assert.DoesNotContain("data-trend", html)
            Assert.DoesNotContain("<svg viewBox=\"0 0 80 24\"", html)
    }

[<Fact>]
let ``no user text enters the trend graphic: a hostile title and a hostile slug stay out of it`` () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 3
        do! Db.execute db "INSERT INTO cr_series (id, title, slug, source_at) VALUES ('EVIL0001', '\"><script>alert(1)</script>', 'x\"><svg onload=alert(1)>', now())"
        do! Db.execute db "INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('EVIL1E1', 'EVIL0001', 'evil', 1440000)"

        for u in 1..3 do
            do! addWatch db (userGuid u) "EVIL1E1" (now.AddDays -1.0)

        use! app = appAt db [] now
        // the raw body: readHtml would decode the escapes back into markup
        let! response = getWithLanguage app.Client "en" "/charts"
        let! html = response.Content.ReadAsStringAsync()
        let row = parseRows html |> List.exactlyOne
        let svg = Regex.Match(row.Block, "<svg\\b.*?</svg>", RegexOptions.Singleline).Value
        Assert.NotEmpty svg
        Assert.DoesNotContain("script", svg)
        Assert.DoesNotContain("alert", svg)
        Assert.DoesNotContain("EVIL", svg)
        // outside the svg the hostile text is escaped, never markup
        Assert.DoesNotContain("<script>alert(1)</script>", row.Block)
        Assert.DoesNotContain("<svg onload", row.Block)
    }

[<Fact>]
let ``each chart answers in under a second over 2 million watch events`` () =
    task {
        let! db = newDatabase ()
        let setup = Diagnostics.Stopwatch.StartNew()

        // 20,000 users, 2,000 series, 24,000 episodes; user u watches 100 distinct episodes, spread over two
        // years before "now". Foreign keys are skipped for the bulk insert (replica role) to keep the setup fast.
        do!
            Db.execute
                db
                """SET session_replication_role = replica;
                   INSERT INTO users (id, username, display_name)
                   SELECT md5('user' || g)::uuid, 'u' || g, 'User ' || g FROM generate_series(0, 19999) g;
                   INSERT INTO cr_series (id, title, slug, source_at)
                   SELECT 'SPD' || lpad(g::text, 5, '0'), 'Series ' || g, 'series-' || g, now() FROM generate_series(0, 1999) g;
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms)
                   SELECT 'EPD' || lpad(g::text, 6, '0'), 'SPD' || lpad((g / 12)::text, 5, '0'), 'Series ' || (g / 12), 1440000
                   FROM generate_series(0, 23999) g;
                   INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
                   SELECT md5('user' || (i % 20000))::uuid,
                          'EPD' || lpad(((((i % 20000) * 37) + ((i / 20000) * 241)) % 24000)::text, 6, '0'),
                          '2026-10-16T12:00:00Z'::timestamptz - ((i::bigint * 104729) % 63072000) * interval '1 second',
                          1400, true, '00000000-0000-0000-0000-000000000001'
                   FROM generate_series(0, 1999999) i;
                   ANALYZE"""

        let! events = Db.count db "watch_events"
        Assert.Equal(2_000_000L, events)
        let setupTime = setup.Elapsed

        use! app = appAt db [] now

        // one warm-up GET per chart (plans, pool, JIT), then the timed ones
        let times = Collections.Generic.List<string * int64>()

        for window in [ "week"; "season"; "all" ] do
            let! _ = pageOf app.Client "en" (path window)

            let watch = Diagnostics.Stopwatch.StartNew()
            let! html = pageOf app.Client "en" (path window)
            watch.Stop()
            Assert.NotEmpty(parseRows html)
            times.Add((window, watch.ElapsedMilliseconds))

        let report = String.Join(", ", times |> Seq.map (fun (w, ms) -> $"{w} {ms} ms")) + $" (setup {int setupTime.TotalSeconds} s)"
        printfn "charts at 2M events: %s" report
        Assert.True(times |> Seq.forall (fun (_, ms) -> ms < 1000L), "a chart took 1 second or more over 2M events: " + report)
    }

// ===========================================================================
// T010: "Watched by N people on Frunchy" on the anime page and the episode page
// ===========================================================================

let private countOf (html: string) : string option =
    let m = Regex.Match(html, "data-people-count=\"([^\"]*)\"")
    if m.Success then Some m.Groups[1].Value else None

/// Two series for the anime page. "ANM00001": 3 distinct people (user 1 watched two episodes), user 4 only an
/// unresolved episode. "ANM00002": 2 people.
let private animeDb () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 4

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES
                     ('ANM00001', 'Counted', NULL, now()), ('ANM00002', 'Rare', NULL, now());
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES
                     ('ANM1E1', 'ANM00001', 'Counted', 1440000), ('ANM1E2', 'ANM00001', 'Counted', 1440000),
                     ('ANM2E1', 'ANM00002', 'Rare', 1440000);
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES
                     ('ANM1E3', 'ANM00001', 'Counted', NULL)"""

        let at = now.AddDays -3.0
        do! addWatch db (userGuid 1) "ANM1E1" at
        do! addWatch db (userGuid 1) "ANM1E2" at
        do! addWatch db (userGuid 2) "ANM1E1" at
        do! addWatch db (userGuid 3) "ANM1E2" at
        do! addWatch db (userGuid 4) "ANM1E3" at
        do! addWatch db (userGuid 1) "ANM2E1" at
        do! addWatch db (userGuid 2) "ANM2E1" at
        return db
    }

[<Fact>]
let ``the anime page counts the distinct people who watched the series, only at or above the minimum`` () =
    task {
        let! db = animeDb ()

        let! expected1 =
            Db.scalar<int64>
                db
                "SELECT count(DISTINCT w.user_id) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.series_id = 'ANM00001' AND e.duration_ms IS NOT NULL"
                []

        let! expected2 =
            Db.scalar<int64>
                db
                "SELECT count(DISTINCT w.user_id) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.series_id = 'ANM00002' AND e.duration_ms IS NOT NULL"
                []

        Assert.Equal(3L, expected1)
        Assert.Equal(2L, expected2)

        for minimum, first, second in [ 2, Some "3", Some "2"; 3, Some "3", None; 4, None, None ] do
            use! app = appAt db [ "Frunchy:Charts:MinPeople", string minimum ] now
            let! one = pageOf app.Client "en" "/u/user1/anime/ANM00001"
            let! two = pageOf app.Client "en" "/u/user1/anime/ANM00002"
            Assert.Equal(first, countOf one)
            Assert.Equal(second, countOf two)

        // the default minimum is 3
        use! app = appAt db [] now
        let! one = pageOf app.Client "en" "/u/user1/anime/ANM00001"
        let! two = pageOf app.Client "en" "/u/user1/anime/ANM00002"
        Assert.Equal(Some "3", countOf one)
        Assert.Equal(None, countOf two)
        Assert.Contains("Watched by 3 people on Frunchy", one)
        Assert.DoesNotContain("Watched by", two)

        // the count is the same for another user's page and in Spanish
        let! other = pageOf app.Client "es" "/u/user2/anime/ANM00001"
        Assert.Equal(Some "3", countOf other)
        Assert.Contains("Visto por 3 personas en Frunchy", other)
    }

/// An episode and its dub (same number, the dub season's matched row shares the original), plus a lone episode.
/// Users 1 and 2 watched the original, 3 the dub, 1 both; user 4 the lone episode.
let private episodeDb () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 4

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES ('EPS00001', 'Grouped', NULL, now());
                   INSERT INTO cr_seasons (id, series_id, season_number, title, audio_locale, source_at) VALUES
                     ('SEASON01', 'EPS00001', 1, 'Original', 'ja-JP', now()), ('SEASON02', 'EPS00001', 1, 'Dub', 'en-US', now());
                   INSERT INTO cr_episodes (id, series_id, season_id, series_title, episode_number, duration_ms) VALUES
                     ('EPSO0001', 'EPS00001', 'SEASON01', 'Grouped', 1, 1440000),
                     ('EPSD0001', 'EPS00001', 'SEASON02', 'Grouped', 1, 1440000),
                     ('EPSO0002', 'EPS00001', 'SEASON01', 'Grouped', 2, 1440000);
                   INSERT INTO mal_anime (mal_id, url, title, raw, data_hash, fetched_at, checked_at) VALUES
                     (900001, 'https://myanimelist.net/anime/900001', 'Grouped', '{}', 'x', now(), now());
                   INSERT INTO cr_season_matches (season_id, status, mal_id, method, shared_from) VALUES
                     ('SEASON01', 'matched', 900001, 'search', NULL),
                     ('SEASON02', 'matched', 900001, 'dub', 'SEASON01')"""

        let at = now.AddDays -3.0
        do! addWatch db (userGuid 1) "EPSO0001" at
        do! addWatch db (userGuid 2) "EPSO0001" at
        do! addWatch db (userGuid 3) "EPSD0001" at
        do! addWatch db (userGuid 1) "EPSD0001" at
        do! addWatch db (userGuid 4) "EPSO0002" at
        return db
    }

[<Fact>]
let ``the episode page counts the people who watched the episode or its dub, only at or above the minimum`` () =
    task {
        let! db = episodeDb ()

        // the group of the original and of the dub: users who watched either, each once
        let! expected =
            Db.scalar<int64>
                db
                "SELECT count(DISTINCT user_id) FROM watch_events WHERE cr_episode_id IN ('EPSO0001', 'EPSD0001')"
                []

        let! lone = Db.scalar<int64> db "SELECT count(DISTINCT user_id) FROM watch_events WHERE cr_episode_id = 'EPSO0002'" []
        Assert.Equal(3L, expected)
        Assert.Equal(1L, lone)

        for minimum, grouped in [ 2, Some "3"; 3, Some "3"; 4, None ] do
            use! app = appAt db [ "Frunchy:Charts:MinPeople", string minimum ] now

            // the page of the original and the page of the dub show the same number
            for episode in [ "EPSO0001"; "EPSD0001" ] do
                let! status, html = EpisodeHelpers.episodePage app "en" "EPS00001" episode
                Assert.Equal(HttpStatusCode.OK, status)
                Assert.Equal(grouped, countOf html)

            // one person (below any minimum): never shown
            let! _, alone = EpisodeHelpers.episodePage app "en" "EPS00001" "EPSO0002"
            Assert.Equal(None, countOf alone)
            Assert.DoesNotContain("Watched by", alone)

        use! app = appAt db [] now
        let! _, en = EpisodeHelpers.episodePage app "en" "EPS00001" "EPSO0001"
        let! _, es = EpisodeHelpers.episodePage app "es" "EPS00001" "EPSO0001"
        Assert.Contains("Watched by 3 people on Frunchy", en)
        Assert.Contains("Visto por 3 personas en Frunchy", es)
    }
