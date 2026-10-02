module FrunchyBackend.Tests.EnrichmentTests

open System
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Workers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// A fresh app with the 2026 sample imported for "animenerd"
let private withSample () =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        return app
    }

let private figures (db: DbConfig) =
    Db.dump
        db
        """
        SELECT count(DISTINCT e.series_id) FILTER (WHERE w.fully_watched), count(*) FILTER (WHERE w.fully_watched),
               sum(CASE WHEN w.fully_watched THEN e.duration_ms ELSE least(w.playhead_s * 1000, e.duration_ms) END)
        FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL
        """

[<Fact>]
let ``the sweep queues every season once, without touching imports`` () =
    task {
        use! app = withSample ()
        let! seasons = Db.count app.Db "cr_seasons"
        Assert.True(seasons > 50L)
        let! before = Db.count app.Db "cr_season_matches"
        Assert.Equal(0L, before) // the import itself never writes enrichment rows (FR-002)

        do! Enrichment.sweep app.Db
        do! Enrichment.sweep app.Db
        let! pending = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE status = 'pending'" []
        Assert.Equal(seasons, pending)
    }

[<Fact>]
let ``the sample is enriched: search, sequel links, the movie, dubs, one-entry shows`` () =
    task {
        use! app = withSample ()
        routeSampleCatalog app.Jikan
        let! figuresBefore = figures app.Db
        let! _ = enrichAll app

        let! frieren = matchOf app.Db "GYE5CQMQ5"
        Assert.Equal("GYE5CQMQ5 matched 52991 search -", frieren)
        let! jjk2 = matchOf app.Db "G6MGC3778"
        Assert.Equal("G6MGC3778 matched 51009 link -", jjk2)
        let! jjk0 = matchOf app.Db "GMSE00194450JAJP"
        Assert.Equal("GMSE00194450JAJP matched 48561 link -", jjk0)
        let! tamerJa = matchOf app.Db "GRK5CNJ14"
        Assert.Equal("GRK5CNJ14 matched 54744 search -", tamerJa)
        let! tamerDub = matchOf app.Db "GYDQCGK5Q"
        Assert.Equal("GYDQCGK5Q matched 54744 dub GRK5CNJ14", tamerDub)
        let! rollJa = matchOf app.Db "GS00365593JAJP"
        Assert.Equal("GS00365593JAJP matched 55998 search -", rollJa) // tier 2: subtitle cut
        let! rollEs = matchOf app.Db "GS00365593ES419"
        Assert.Equal("GS00365593ES419 matched 55998 dub GS00365593JAJP", rollEs)
        let! eyeshield2 = matchOf app.Db "G649M5G0Y"
        Assert.Equal("G649M5G0Y matched 15 link -", eyeshield2) // no sequel on MyAnimeList: same entry

        // Every season is in a definite state; unknown titles are "no match", never guessed
        let! open' = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE status IN ('pending', 'failed')" []
        Assert.Equal(0L, open')
        let! wrong = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_season_matches WHERE status = 'matched' AND mal_id NOT IN (52991, 59978, 40748, 51009, 48561, 54744, 55998, 15)" []
        Assert.Equal(0L, wrong)

        // Details stored with the verbatim response, tags in MyAnimeList's order, relations
        let! raw = Db.scalar<string> app.Db "SELECT raw::text FROM mal_anime WHERE mal_id = 52991" []
        Assert.Equal(FrunchyBackend.Tests.Infrastructure.FakeJikan.readFixture "full-52991.json", raw)
        let! genres = Db.dump app.Db "SELECT t.name FROM mal_anime_tags a JOIN mal_tags t ON t.kind = a.kind AND t.tag_id = a.tag_id WHERE a.mal_id = 52991 AND a.kind = 'genre' ORDER BY a.position"
        Assert.Equal<string list>([ "Adventure"; "Award Winning"; "Drama"; "Fantasy" ], genres)
        let! score = Db.scalar<decimal> app.Db "SELECT score FROM mal_anime WHERE mal_id = 52991" []
        Assert.Equal(9.26m, score)
        let! sequel = Db.scalar<int> app.Db "SELECT related_id FROM mal_relations WHERE mal_id = 52991 AND relation = 'Sequel'" []
        Assert.Equal(59978, sequel)
        let! noScore = Db.scalar<int64> app.Db "SELECT count(*) FROM mal_anime WHERE mal_id = 55998 AND score IS NULL" []
        Assert.Equal(1L, noScore)

        // A dub makes no request of its own; the figures are untouched (SC-009)
        Assert.Equal(1, app.Jikan.Count "search:The Weakest Tamer Began a Journey to Pick Up Trash")
        let! figuresAfter = figures app.Db
        Assert.Equal<string list>(figuresBefore, figuresAfter)

        // Running again makes no requests (SC-005)
        app.Jikan.Clear()
        let! again = enrichAll app
        Assert.Empty again
        Assert.Empty app.Jikan.Requests
    }

[<Fact>]
let ``queue order: operator requests, then the most recently watched; waiting rows are skipped`` () =
    task {
        use! app = withSample ()
        do! Enrichment.sweep app.Db
        let now = DateTimeOffset.UtcNow

        // Most recently watched season of the sample first
        let! newest =
            Db.scalar<string>
                app.Db
                "SELECT e.season_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.season_id IS NOT NULL ORDER BY w.watched_at DESC, e.season_id LIMIT 1"
                []

        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET requested_at = now() WHERE season_id = 'G675KXENR' RETURNING 1" []
        let! first = Enrichment.claimSeason app.Db now false
        Assert.Equal(Some "G675KXENR", first)
        let! second = Enrichment.claimSeason app.Db now false
        Assert.Equal(Some newest, second)

        // A row waiting for its next attempt isn't claimed
        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET next_attempt_at = now() + interval '1 hour', claimed_at = NULL RETURNING 1" []
        let! none = Enrichment.claimSeason app.Db now false
        Assert.Equal(None, none)
    }

[<Fact>]
let ``parallel claims never take the same season; stale claims are taken again`` () =
    task {
        use! app = withSample ()
        do! Enrichment.sweep app.Db
        let now = DateTimeOffset.UtcNow
        let! claims = Task.WhenAll [ for _ in 1..8 -> Enrichment.claimSeason app.Db now false ]
        let ids = claims |> Array.choose id
        Assert.Equal(8, ids.Length)
        Assert.Equal(8, ids |> Array.distinct |> Array.length)

        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET claimed_at = now() - interval '11 minutes' WHERE season_id = @id RETURNING 1" [ "id", box ids[0] ]
        let! _ = Db.scalar<int> app.Db "UPDATE cr_season_matches SET next_attempt_at = now() + interval '1 hour' WHERE claimed_at IS NULL RETURNING 1" []
        let! again = Enrichment.claimSeason app.Db now false
        Assert.Equal(Some ids[0], again)
    }

[<Fact>]
let ``no match is final; a title change queues it again`` () =
    task {
        use! app = withSample ()
        let! _ = enrichAll app
        let! platinum = matchOf app.Db "GYK5CNZ9G"
        Assert.StartsWith("GYK5CNZ9G no_match", platinum)

        app.Jikan.Clear()
        let! _ = enrichAll app
        Assert.Empty app.Jikan.Requests

        let! _ = Db.scalar<int> app.Db "UPDATE cr_seasons SET title = 'Platinum End (2021)' WHERE id = 'GYK5CNZ9G' RETURNING 1" []
        do! Enrichment.sweep app.Db
        let! requeued = matchOf app.Db "GYK5CNZ9G"
        Assert.StartsWith("GYK5CNZ9G pending", requeued)
    }

[<Fact>]
let ``deleting a user leaves the MyAnimeList catalog and the matches`` () =
    task {
        use! app = withSample ()
        routeSampleCatalog app.Jikan
        let! _ = enrichAll app
        let! animeBefore = Db.count app.Db "mal_anime"
        let! matchesBefore = Db.count app.Db "cr_season_matches"
        let! deleted = FrunchyBackend.Operations.DeleteUser.run app.Db [ "animenerd"; "--yes" ]
        Assert.Equal(0, deleted)
        let! animeAfter = Db.count app.Db "mal_anime"
        let! matchesAfter = Db.count app.Db "cr_season_matches"
        Assert.Equal(animeBefore, animeAfter)
        Assert.Equal(matchesBefore, matchesAfter)
    }
