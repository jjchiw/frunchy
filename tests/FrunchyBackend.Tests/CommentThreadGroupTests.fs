module FrunchyBackend.Tests.CommentThreadGroupTests

open System.Net
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 009, User Story 3: a dub and its original share one thread (research R2). The enriched 2026 sample:
// season GRK5CNJ14 is an original whose series also holds its dub (a season whose matched row has
// method = 'dub' and shared_from = 'GRK5CNJ14').

let private original = "GRK5CNJ14"

/// The dub season of the original, found through its matched row
let private dubSeason (app: TestApp) =
    Db.scalar<string>
        app.Db
        "SELECT season_id FROM cr_season_matches WHERE method = 'dub' AND shared_from = @o AND status = 'matched' ORDER BY season_id LIMIT 1"
        [ "o", box original ]

/// One dub episode and the original's episode with the same number
let private pair (app: TestApp) : Task<EpisodeRef * EpisodeRef> =
    task {
        let! rows =
            Db.dump
                app.Db
                """SELECT dub.series_id, dub.id, orig.id
                   FROM cr_episodes dub
                   JOIN cr_season_matches m ON m.season_id = dub.season_id AND m.status = 'matched' AND m.method = 'dub'
                   JOIN cr_episodes orig ON orig.season_id = m.shared_from AND orig.episode_number = dub.episode_number
                   WHERE m.shared_from = 'GRK5CNJ14' AND dub.duration_ms IS NOT NULL AND orig.duration_ms IS NOT NULL
                     AND dub.series_id = orig.series_id
                   ORDER BY dub.id, orig.id LIMIT 1"""

        match rows with
        | [ row ] ->
            let p = row.Split '|'
            return { Series = p[0]; Episode = p[1] }, { Series = p[0]; Episode = p[2] }
        | _ -> return failwith "the sample has no dub episode with an original of the same number"
    }

let private signedIn (app: TestApp) =
    task {
        let! alice = signedUp app "sub-a" "alice"
        let! bob = signedUp app "sub-b" "bob"
        return alice, bob
    }

[<Fact>]
let ``a dub and its original with the same number show each other's comments, each with its own episode`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! dub, orig = pair app
        let! alice, bob = signedIn app
        use alice = alice
        use bob = bob
        let! _ = postComment alice orig.Series orig.Episode "on the original" false
        let! _ = postComment bob dub.Series dub.Episode "on the dub" false

        for page in [ orig; dub ] do
            let! status, html = episodePage app "en" page.Series page.Episode
            Assert.Equal(HttpStatusCode.OK, status)
            let thread = comments html
            Assert.Equal("2", hookValue "thread" html)
            Assert.Equal<string list>([ "alice"; "bob" ], thread |> List.map _.Author)
            Assert.Equal<string list>([ orig.Episode; dub.Episode ], thread |> List.map _.Episode)
    }

[<Fact>]
let ``another number in the same seasons stays out of the thread`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! dub, orig = pair app

        // the sample holds one episode of that number per season: add one with another number
        do!
            Db.execute
                app.Db
                $"""INSERT INTO cr_episodes (id, series_id, season_id, title, episode_number, duration_ms)
                    SELECT 'OTHERNUMBER1', series_id, season_id, 'Another number', episode_number + 100, duration_ms
                    FROM cr_episodes WHERE id = '{orig.Episode}'"""

        let! other = pickEpisode app.Db "e.id = 'OTHERNUMBER1'"

        let! alice, _ = signedIn app
        use alice = alice
        let! _ = postComment alice other.Series other.Episode "another episode" false

        for page in [ orig; dub ] do
            let! _, html = episodePage app "en" page.Series page.Episode
            Assert.Equal("0", hookValue "thread" html)
    }

[<Fact>]
let ``unlinking the dub gives each page its own comments, relinking shares them again, and no comment changes`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! dub, orig = pair app
        let! alice, bob = signedIn app
        use alice = alice
        use bob = bob
        let! _ = postComment alice orig.Series orig.Episode "on the original" false
        let! _ = postComment bob dub.Series dub.Episode "on the dub" false
        let! before = rawCommentRows app.Db

        let! season = dubSeason app
        let! saved = Db.dump app.Db $"SELECT status, mal_id, method, shared_from FROM cr_season_matches WHERE season_id = '{season}'"
        let saved = (List.exactlyOne saved).Split '|'

        // unlinked: no_match, no MyAnimeList id, no method, no original
        do!
            Db.execute
                app.Db
                $"UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, shared_from = NULL WHERE season_id = '{season}'"

        let! _, origHtml = episodePage app "en" orig.Series orig.Episode
        let! _, dubHtml = episodePage app "en" dub.Series dub.Episode
        Assert.Equal<string list>([ "alice" ], comments origHtml |> List.map _.Author)
        Assert.Equal<string list>([ "bob" ], comments dubHtml |> List.map _.Author)
        Assert.Equal("1", hookValue "thread" origHtml)
        Assert.Equal("1", hookValue "thread" dubHtml)

        let! unlinked = rawCommentRows app.Db
        Assert.Equal<string list>(before, unlinked)

        // relinked
        do!
            Db.execute
                app.Db
                $"UPDATE cr_season_matches SET status = '{saved[0]}', mal_id = {saved[1]}, method = '{saved[2]}', shared_from = '{saved[3]}' WHERE season_id = '{season}'"

        for page in [ orig; dub ] do
            let! _, html = episodePage app "en" page.Series page.Episode
            Assert.Equal("2", hookValue "thread" html)
            Assert.Equal<string list>([ "alice"; "bob" ], comments html |> List.map _.Author)

        let! relinked = rawCommentRows app.Db
        Assert.Equal<string list>(before, relinked)
    }

[<Fact>]
let ``a numberless episode and a seasonless one keep their own thread`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! alice, _ = signedIn app
        use alice = alice

        // two numberless episodes: no number to share, so no shared thread
        let! special = pickEpisode app.Db "e.episode_number IS NULL AND e.season_id IS NOT NULL"
        let! sibling = pickEpisode app.Db $"e.episode_number IS NULL AND e.id <> '{special.Episode}'"

        // a seasonless episode numbered like an episode of the original season
        do!
            Db.execute
                app.Db
                $"""INSERT INTO cr_episodes (id, series_id, season_id, title, episode_number, duration_ms)
                    SELECT 'SEASONLESS1', series_id, NULL, 'Seasonless', episode_number, duration_ms
                    FROM cr_episodes WHERE season_id = '{original}' AND episode_number IS NOT NULL AND duration_ms IS NOT NULL
                    ORDER BY id LIMIT 1"""

        let! seasonless = pickEpisode app.Db "e.id = 'SEASONLESS1'"

        let! twin =
            pickEpisode
                app.Db
                $"e.season_id = '{original}' AND e.episode_number = (SELECT episode_number FROM cr_episodes WHERE id = 'SEASONLESS1')"

        let all = [ special, "special"; sibling, "sibling"; seasonless, "seasonless"; twin, "twin" ]

        for ep, text in all do
            let! response = postComment alice ep.Series ep.Episode text false
            Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        for ep, text in all do
            let! _, html = episodePage app "en" ep.Series ep.Episode
            let thread = comments html
            Assert.Equal(1, thread.Length)
            Assert.Equal(ep.Episode, thread.Head.Episode)
            Assert.Contains(text, thread.Head.Text)
    }
