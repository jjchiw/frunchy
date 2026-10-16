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
