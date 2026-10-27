module FrunchyBackend.Tests.DashboardMalTests

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private withSample () =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        return app
    }

let private page (app: TestApp) (language: string) =
    task {
        let! response = getWithLanguage app.Client language "/u/animegolden"
        return! readHtml response
    }

let private cards (html: string) =
    Regex.Split(html, "data-recent-item") |> Array.skip 1 |> Array.map (fun c -> c.Substring(0, c.IndexOf "</time>")) |> List.ofArray

/// The seasons of the dashboard's recent cards, in card order
let private recentSeasons (db: DbConfig) =
    Db.dump
        db
        """
        SELECT coalesce(e.season_id, '-') FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
        WHERE e.duration_ms IS NOT NULL ORDER BY w.watched_at DESC, e.id LIMIT 6
        """

/// Store an anime and match a season to it, without the matcher
let private matchSeason (db: DbConfig) (seasonId: string) (s: Spec) =
    task {
        let body = fullBody s

        match MalApiModel.parseFull body with
        | Ok(anime, hash) -> do! MalCatalog.saveAnime db anime body hash None "mal-api" DateTimeOffset.UtcNow
        | Error e -> failwith e

        let! _ = MalCatalog.writeManual db seasonId (Some s.Id) DateTimeOffset.UtcNow
        ()
    }

let private stats (html: string) =
    [ for name in [ "series"; "episodes"; "hours"; "days" ] ->
          Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value ]

[<Fact>]
let ``matched cards show score, up to 3 genres and the MyAnimeList link; others render as before`` () =
    task {
        use! app = withSample ()
        let! before = page app "en-US"
        let! seasons = recentSeasons app.Db
        let first = seasons |> List.find ((<>) "-")
        let firstIndex = seasons |> List.findIndex ((=) first)
        let otherIndex = seasons |> List.findIndex (fun s -> s <> first)

        do!
            matchSeason
                app.Db
                first
                { spec 40748 "Jujutsu Kaisen" (Some "Jujutsu Kaisen") "TV" "2020-10-03" with
                    Score = Some 8.55
                    Genres = [ "Action"; "Supernatural"; "Drama"; "Fantasy" ] }

        let! after = page app "en-US"
        let card = (cards after)[firstIndex]
        Assert.Contains("data-mal-score=\"8.55\"", card)
        Assert.Contains("★ 8.55", card)
        Assert.Contains("aria-label=\"MyAnimeList score\"", card)
        Assert.Matches("data-mal-genres=\"\">Action / Supernatural / Drama<", card)
        Assert.Contains("href=\"https://myanimelist.net/anime/40748\"", card)
        Assert.Contains("target=\"_blank\"", card)

        // An unmatched card is exactly what it was; the figures don't move; no request was made
        Assert.Equal((cards before)[otherIndex], (cards after)[otherIndex])
        Assert.Equal<string list>(stats before, stats after)
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``a matched card shows its first studio, format and year; an unmatched card says it isn't on MyAnimeList yet`` () =
    task {
        use! app = withSample ()
        let! seasons = recentSeasons app.Db
        let first = seasons |> List.find ((<>) "-")
        let firstIndex = seasons |> List.findIndex ((=) first)
        let otherIndex = seasons |> List.findIndex (fun s -> s <> first)

        do!
            matchSeason
                app.Db
                first
                { spec 48561 "Jujutsu Kaisen 0 Movie" (Some "Jujutsu Kaisen 0") "Movie" "2021-12-24" with
                    Studios = [ "MAPPA"; "Second Studio" ] }

        let! html = page app "en-US"
        let card = (cards html)[firstIndex]
        Assert.Matches("data-mal-studio=\"\">MAPPA<", card)
        Assert.DoesNotContain("Second Studio", card)
        Assert.Matches("data-mal-format=\"\">Movie<", card)
        Assert.Matches("data-mal-year=\"\">2021<", card)
        Assert.DoesNotContain("Not on MyAnimeList yet", card)

        let other = (cards html)[otherIndex]
        Assert.DoesNotContain("data-mal-studio", other)
        Assert.Contains("Not on MyAnimeList yet", other)

        let! es = page app "es-ES"
        Assert.Contains("Aún no está en MyAnimeList", (cards es)[otherIndex])
    }

[<Fact>]
let ``no score means no score element`` () =
    task {
        use! app = withSample ()
        let! seasons = recentSeasons app.Db
        let first = seasons |> List.find ((<>) "-")
        do! matchSeason app.Db first { spec 7 "Unscored" None "TV" "2026-01-01" with Score = None }
        let! html = page app "en-US"
        let card = (cards html)[seasons |> List.findIndex ((=) first)]
        Assert.DoesNotContain("data-mal-score", card)
        Assert.Contains("data-mal-link", card)
    }

[<Fact>]
let ``Top genres equal a hand count over fully watched episodes in matched seasons`` () =
    task {
        use! app = withSample ()
        let! before = page app "en-US"
        Assert.DoesNotContain("data-top-genres", before) // nothing matched yet

        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        app.Mal.Clear()
        let! html = page app "en-US"
        Assert.Contains("data-top-genres", html)

        let! expected =
            Db.dump
                app.Db
                """
                SELECT t.name || '=' || count(*)
                FROM watch_events w
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                JOIN mal_anime_tags at ON at.mal_id = m.mal_id AND at.kind = 'genre'
                JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                WHERE w.fully_watched AND e.duration_ms IS NOT NULL
                GROUP BY t.tag_id, t.name ORDER BY count(*) DESC, t.name LIMIT 5
                """

        let shown =
            [ for m in Regex.Matches(html, "data-genre=\"([^\"]+)\"[\\s\\S]*?data-genre-episodes=\"(\\d+)\"") ->
                  m.Groups[1].Value + "=" + m.Groups[2].Value ]

        Assert.NotEmpty expected
        Assert.Equal<string list>(expected, shown)

        let! all = Db.scalar<int64> app.Db "SELECT count(*) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE w.fully_watched AND e.duration_ms IS NOT NULL" []

        let! matched =
            Db.scalar<int64>
                app.Db
                "SELECT count(*) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched' WHERE w.fully_watched AND e.duration_ms IS NOT NULL"
                []

        let coverage = int (Math.Round(100m * decimal matched / decimal all, MidpointRounding.AwayFromZero))
        Assert.Contains($"data-genres-coverage=\"{coverage}\"", html)
        Assert.Contains($"Based on {coverage}%% of fully watched episodes", html)
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``Spanish labels; MyAnimeList's genre names are not translated`` () =
    task {
        use! app = withSample ()
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        let! html = page app "es-ES"
        Assert.Contains("Géneros principales", html)
        Assert.Contains("Basado en el", html)
        Assert.Contains("data-genre=\"Adventure\"", html)
        Assert.Contains("episodios", html)
    }
