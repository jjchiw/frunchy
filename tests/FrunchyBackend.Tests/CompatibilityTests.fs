module FrunchyBackend.Tests.CompatibilityTests

open System
open Xunit
open FrunchyBackend.Infrastructure.Compatibility
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 011: `Compatibility.load` on real Postgres with the 2026 sample and the feature 003 fixtures.
// The card and page tests (T005, T007) build on this file.

let private userId (app: TestApp) (username: string) =
    Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box username ]

[<Fact>]
let ``load reads both histories, shares everything between identical ones and is symmetric`` () =
    task {
        let! app = newApp ()
        use app = app
        let! keyA = createUser app.Db "animea"
        let! keyB = createUser app.Db "animeb"
        let! _ = createUser app.Db "animenew"
        let! _ = submitAccepted app keyA (load2026Sample ())
        let! _ = submitAccepted app keyB (load2026SampleRaw () |> withProfile "profile-second")
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        let! a = userId app "animea"
        let! b = userId app "animeb"
        let! fresh = userId app "animenew"

        let! ab = load app.Db a b
        let! ba = load app.Db b a

        let! seriesCount =
            Db.scalar<int64>
                app.Db
                """SELECT count(DISTINCT e.series_id) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                   WHERE w.user_id = @u AND e.duration_ms IS NOT NULL"""
                [ "u", box a ]

        Assert.Equal("animea", ab.Viewer.Username)
        Assert.Equal("animeb", ab.Owner.Username)
        Assert.Equal(Both, ab.Enough)
        Assert.Equal(int seriesCount, ab.Viewer.Series.Count)
        // the same history on both sides: every series is shared, nothing is only theirs
        Assert.Equal(ab.Viewer.Series.Count, ab.Shared.Length)
        Assert.Empty ab.Theirs
        Assert.True ab.GenresCompared
        Assert.Equal(Some 5, ab.Level)
        Assert.Equal(ab.Level, ba.Level)
        Assert.Equal(ab.Score, ba.Score)
        Assert.NotEmpty ab.SharedGenres
        Assert.NotEmpty ab.GenreRows

        // the viewer drops the most watched series: it is now only the owner's, with its MyAnimeList details
        let! top =
            Db.scalar<string>
                app.Db
                """SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                   JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                   WHERE w.user_id = @u GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1"""
                [ "u", box b ]

        do!
            Db.execute
                app.Db
                $"""DELETE FROM watch_events WHERE user_id = '{a}'
                    AND cr_episode_id IN (SELECT id FROM cr_episodes WHERE series_id = '{top}')"""

        let! partial = load app.Db a b
        Assert.Equal(Both, partial.Enough)
        Assert.Equal(ab.Shared.Length - 1, partial.Shared.Length)
        let theirs = partial.Theirs |> List.exactlyOne
        Assert.Equal(top, theirs.SeriesId)
        Assert.True theirs.MalScore.IsSome
        Assert.NotEmpty theirs.Genres
        Assert.True(theirs.Genres.Length <= 3)

        // a user without history is short, and no level shows
        let! short = load app.Db fresh b
        Assert.Equal(ViewerShort, short.Enough)
        Assert.Equal(None, short.Level)
        let! theirShort = load app.Db a fresh
        Assert.Equal(OwnerShort, theirShort.Enough)
    }
