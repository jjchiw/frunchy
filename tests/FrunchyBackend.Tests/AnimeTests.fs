module FrunchyBackend.Tests.AnimeTests

open System
open System.Globalization
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 006, User Story 1: the anime page (contracts/anime-page.md). The 2026 sample with the feature 003
// MyAnimeList fixtures; every expectation is computed in F# from raw rows, independently of the slice's SQL.

let private withSample () =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        return app, key
    }

let private get (app: TestApp) (language: string) (path: string) =
    task {
        let! response = getWithLanguage app.Client language path
        let! html = readHtml response
        return response.StatusCode, html
    }

let private anime (app: TestApp) (language: string) (seriesId: string) =
    task {
        let! status, html = get app language $"/u/animenerd/anime/{seriesId}"
        Assert.Equal(HttpStatusCode.OK, status)
        return html
    }

/// The attributes of every element whose data-{hook} has a value (data-season="x", data-dub="")
let private elements (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}=\"[^\"]*\"[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private one hook html = elements hook html |> List.exactlyOne
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""
let private stat (name: string) (html: string) = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value

let private signature (html: string) =
    [ for m in Regex.Matches(html, "data-(?!address)[a-z-]+=\"[^\"]*\"") -> m.Value ]

/// The datetime of the time element inside a container hook
let private instantIn (hook: string) (html: string) =
    Regex.Match(html, $"data-{hook}=\"\"[^>]*>.*?datetime=\"([^\"]+)\"", RegexOptions.Singleline).Groups[1].Value

let private title (html: string) =
    Regex.Match(html, "data-anime-title=\"\"[^>]*>([^<]*)<").Groups[1].Value

// ---------------------------------------------------------------------------
// The page as a list of seasons, each with its MyAnimeList marker and its episodes, in document order
// ---------------------------------------------------------------------------

type private PageSeason =
    { Attrs: Map<string, string>
      Mal: string option
      MalCompact: bool
      MalMissing: bool
      Episodes: Map<string, string> list }

let private outline (html: string) =
    let items =
        [ for m in Regex.Matches(html, "<[a-z0-9]+\\b[^>]*\\bdata-(season|episode|mal|mal-missing)=\"[^\"]*\"[^>]*>") ->
              m.Groups[1].Value,
              [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
              |> Map.ofList ]

    items
    |> List.fold
        (fun (seasons: PageSeason list) (kind, attrs) ->
            match kind, seasons with
            | "season", _ -> seasons @ [ { Attrs = attrs; Mal = None; MalCompact = false; MalMissing = false; Episodes = [] } ]
            | "mal", _ :: _ ->
                let last = List.last seasons
                List.take (seasons.Length - 1) seasons @ [ { last with Mal = Some(attr "mal" attrs); MalCompact = attrs.ContainsKey "mal-compact" } ]
            | "mal-missing", _ :: _ ->
                let last = List.last seasons
                List.take (seasons.Length - 1) seasons @ [ { last with MalMissing = true } ]
            | "episode", _ :: _ ->
                let last = List.last seasons
                List.take (seasons.Length - 1) seasons @ [ { last with Episodes = last.Episodes @ [ attrs ] } ]
            | _ -> failwith "an anime page element came before any season")
        []

// ---------------------------------------------------------------------------
// Expected figures, from raw rows
// ---------------------------------------------------------------------------

type private Ep =
    { Id: string
      Season: string
      Number: int option
      Title: string
      Duration: int64
      Fully: bool
      Playhead: int64
      At: DateTime }

type private SeasonFacts =
    { Number: int option
      Audio: string
      Dub: bool
      Mal: string
      Known: int64 }

let private watched (db: DbConfig) (seriesId: string) =
    task {
        let! rows =
            Db.dump
                db
                $"""
                SELECT e.id, coalesce(e.season_id, ''), coalesce(e.episode_number::text, ''), replace(coalesce(e.title, ''), '|', '/'),
                       e.duration_ms, w.fully_watched, w.playhead_s,
                       to_char(w.watched_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS')
                FROM watch_events w JOIN users u ON u.id = w.user_id
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                WHERE u.username = 'animenerd' AND e.series_id = '{seriesId}' AND e.duration_ms IS NOT NULL
                """

        return
            [ for row in rows ->
                  match row.Split '|' with
                  | [| id; season; number; title; duration; fully; playhead; at |] ->
                      { Id = id
                        Season = season
                        Number = (if number = "" then None else Some(int number))
                        Title = title
                        Duration = int64 duration
                        Fully = Boolean.Parse fully
                        Playhead = int64 playhead
                        At = DateTime.Parse(at, CultureInfo.InvariantCulture) }
                  | _ -> failwith row ]
    }

let private seasonFacts (db: DbConfig) =
    task {
        let! seasons = Db.dump db "SELECT id, coalesce(season_number::text, ''), coalesce(audio_locale, '') FROM cr_seasons"
        let! matches = Db.dump db "SELECT season_id, coalesce(method, ''), mal_id::text FROM cr_season_matches WHERE status = 'matched'"
        let! known = Db.dump db "SELECT season_id, count(*) FROM cr_episodes WHERE duration_ms IS NOT NULL AND season_id IS NOT NULL GROUP BY 1"
        let matchOf = matches |> List.map (fun r -> let p = r.Split '|' in p[0], (p[1], p[2])) |> Map.ofList
        let knownOf = known |> List.map (fun r -> let p = r.Split '|' in p[0], int64 p[1]) |> Map.ofList

        return
            seasons
            |> List.map (fun r ->
                let p = r.Split '|'
                let method', mal = matchOf.TryFind p[0] |> Option.defaultValue ("", "")

                p[0],
                { Number = (if p[1] = "" then None else Some(int p[1]))
                  Audio = p[2]
                  Dub = (method' = "dub")
                  Mal = mal
                  Known = knownOf.TryFind p[0] |> Option.defaultValue 0L })
            |> Map.ofList
    }

let private percent (e: Ep) =
    if e.Fully then 100 else int (Math.Round(100m * decimal (min (e.Playhead * 1000L) e.Duration) / decimal e.Duration, MidpointRounding.AwayFromZero))

let private ms (e: Ep) =
    if e.Fully then e.Duration else min (e.Playhead * 1000L) e.Duration

let private instant (d: DateTime) = d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

/// Seasons in Crunchyroll's order (number, then id; episodes without a season last)
let private expectedSeasons (facts: Map<string, SeasonFacts>) (eps: Ep list) =
    eps
    |> List.groupBy _.Season
    |> List.sortBy (fun (id, _) -> id = "", (if id = "" then None else facts[id].Number) |> Option.defaultValue Int32.MaxValue, id)

let private expectedEpisodes (eps: Ep list) =
    eps
    |> List.sortWith (fun a b ->
        let n (e: Ep) = e.Number |> Option.defaultValue Int32.MaxValue

        match compare (n a) (n b) with
        | 0 ->
            match String.CompareOrdinal(a.Title, b.Title) with
            | 0 -> String.CompareOrdinal(a.Id, b.Id)
            | c -> c
        | c -> c)

/// Every figure, season and episode of a series' page against the expectation
let private assertPage (db: DbConfig) (seriesId: string) (html: string) =
    task {
        let! eps = watched db seriesId
        let! facts = seasonFacts db
        Assert.NotEmpty eps

        // header and totals (the dashboard's rule restricted to the series)
        Assert.Equal(seriesId, one "anime" html |> attr "anime")
        Assert.Equal(string (eps |> List.filter _.Fully |> List.length), stat "episodes" html)
        Assert.Equal((Math.Round(decimal (eps |> List.sumBy ms) / 3_600_000m, 1, MidpointRounding.AwayFromZero)).ToString("0.0", CultureInfo.InvariantCulture), stat "hours" html)
        Assert.Equal(instant (eps |> List.map _.At |> List.min), instantIn "first-watch" html)
        Assert.Equal(instant (eps |> List.map _.At |> List.max), instantIn "latest-watch" html)

        // seasons, in order
        let expected = expectedSeasons facts eps
        let page = outline html
        Assert.Equal<string list>(expected |> List.map (fun (id, _) -> if id = "" then "other" else id), page |> List.map (fun s -> attr "season" s.Attrs))

        for (id, seasonEps), shown in List.zip expected page do
            if id = "" then
                Assert.False(shown.Attrs.ContainsKey "audio")
                Assert.False(shown.Attrs.ContainsKey "dub")
                Assert.Equal(None, shown.Mal)
                Assert.False shown.MalMissing
            else
                let f = facts[id]
                Assert.Equal(string (seasonEps |> List.filter _.Fully |> List.length), attr "watched" shown.Attrs)
                Assert.Equal(string f.Known, attr "known" shown.Attrs)
                Assert.Equal(f.Audio, attr "audio" shown.Attrs)
                Assert.Equal(f.Dub, shown.Attrs.ContainsKey "dub")

                if f.Mal = "" then
                    Assert.True(shown.MalMissing, $"season {id} is unmatched")
                    Assert.Equal(None, shown.Mal)
                else
                    Assert.False shown.MalMissing
                    Assert.Equal(Some f.Mal, shown.Mal)

            // episodes, in order, with their state
            let expectedEps = expectedEpisodes seasonEps
            Assert.Equal<string list>(expectedEps |> List.map _.Id, shown.Episodes |> List.map (attr "episode"))
            Assert.Equal<string list>(expectedEps |> List.map (fun e -> string (percent e)), shown.Episodes |> List.map (attr "percent"))
            Assert.Equal<string list>(expectedEps |> List.map (fun e -> if e.Fully then "true" else "false"), shown.Episodes |> List.map (attr "fully"))

        return expected
    }

// ---------------------------------------------------------------------------
// Finding series in the sample, by what they must show
// ---------------------------------------------------------------------------

let private seriesOfSeason (db: DbConfig) (seasonId: string) =
    Db.scalar<string> db $"SELECT series_id FROM cr_seasons WHERE id = '{seasonId}'" []

/// A series the user watched that has a season with no MyAnimeList match
let private unmatchedSeries (db: DbConfig) =
    Db.scalar<string>
        db
        """
        SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
        LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
        WHERE e.duration_ms IS NOT NULL AND e.season_id IS NOT NULL AND m.mal_id IS NULL
        GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1
        """
        []

let private watchedSeries (db: DbConfig) =
    task {
        let! ids = Db.dump db "SELECT DISTINCT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY 1"
        return ids
    }

// ---------------------------------------------------------------------------
// Figures (SC-001, SC-002)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a series with several seasons shows every figure, season and episode`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "G6MGC3778"
        app.Mal.Clear()
        let! html = anime app "en" series
        let! expected = assertPage app.Db series html
        Assert.True(expected.Length > 1, "JUJUTSU KAISEN has more than one watched season")
        Assert.Contains("JUJUTSU KAISEN", title html)
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``a series with a dub marks the dub season and names both audio languages`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! html = anime app "en" series
        let! expected = assertPage app.Db series html

        let seasons = outline html
        let ids = seasons |> List.map (fun s -> attr "season" s.Attrs)
        Assert.Contains("GRK5CNJ14", ids)
        Assert.Contains("GYDQCGK5Q", ids)

        // exactly the seasons whose match method is 'dub' are marked, and a dub has its original's MyAnimeList entry
        let! dubs = Db.dump app.Db "SELECT season_id FROM cr_season_matches WHERE method = 'dub'"
        let shownDubs = seasons |> List.filter (fun s -> s.Attrs.ContainsKey "dub") |> List.map (fun s -> attr "season" s.Attrs)
        Assert.NotEmpty shownDubs
        Assert.Equal<string list>(ids |> List.filter (fun id -> List.contains id dubs), shownDubs)

        let audio = seasons |> List.map (fun s -> attr "audio" s.Attrs) |> List.distinct
        Assert.True(audio.Length > 1, "the original and the dub differ in audio language")
        Assert.True(expected.Length >= 2)
    }

[<Fact>]
let ``a series with an unmatched season says so plainly`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = unmatchedSeries app.Db
        let! html = anime app "en" series
        let! _ = assertPage app.Db series html

        let seasons = outline html
        Assert.Contains(true, seasons |> List.map _.MalMissing)
        Assert.Contains(Texts.en.NotOnMyAnimeList, html)
        // an unmatched season carries no MyAnimeList fields at all
        Assert.Equal(seasons |> List.filter (fun s -> s.Mal.IsSome) |> List.length, elements "mal-link" html |> List.length)
    }

[<Fact>]
let ``every watched series has a page, and their episodes add up to the dashboard`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! all = watchedSeries app.Db
        Assert.True(all.Length > 5)

        let mutable total = 0

        for series in all do
            let! html = anime app "en" series
            total <- total + int (stat "episodes" html)

        let! _, dashboard = get app "en" "/u/animenerd"
        Assert.Equal(stat "episodes" dashboard, string total)
    }

[<Fact>]
let ``a series watched only partly still has a page, with the progress of its episodes`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"

        do!
            Db.execute
                app.Db
                $"""
                UPDATE watch_events SET fully_watched = false, playhead_s = 60
                WHERE cr_episode_id IN (SELECT id FROM cr_episodes WHERE series_id = '{series}')
                """

        let! html = anime app "en" series
        let! _ = assertPage app.Db series html
        Assert.Equal("0", stat "episodes" html)

        let episodes = outline html |> List.collect _.Episodes
        Assert.NotEmpty episodes
        Assert.All(episodes, fun e -> Assert.Equal("false", attr "fully" e))
        Assert.All(episodes, fun e -> Assert.InRange(int (attr "percent" e), 0, 99))
        Assert.Contains(Texts.en.WatchedPercent(int (attr "percent" episodes.Head)), html)
    }

[<Fact>]
let ``seasons that share a MyAnimeList entry show its details once and a link on the others`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! html = anime app "en" series
        let! _ = assertPage app.Db series html

        let seasons = outline html |> List.filter (fun s -> s.Mal.IsSome)
        let ids = seasons |> List.map (fun s -> s.Mal.Value)
        Assert.True(ids.Length > (ids |> List.distinct |> List.length), "the original and its dub share an entry")

        // the first season of each entry is the full block; every later one is the compact form
        let expectedCompact = ids |> List.mapi (fun i id -> List.contains id (List.take i ids))
        Assert.Equal<bool list>(expectedCompact, seasons |> List.map _.MalCompact)
        Assert.Contains(true, expectedCompact)

        // one link per season, but the score (and so the rest of the details) once per entry
        Assert.Equal(seasons.Length, elements "mal-link" html |> List.length)
        let! scored = Db.dump app.Db "SELECT mal_id FROM mal_anime WHERE score IS NOT NULL"
        let distinctScored = ids |> List.distinct |> List.filter (fun id -> List.contains id scored)
        Assert.Equal(distinctScored.Length, elements "mal-score" html |> List.length)
    }

// ---------------------------------------------------------------------------
// Not found (FR-002)
// ---------------------------------------------------------------------------

[<Fact>]
let ``an unknown user, a malformed id and an unwatched series are not found`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! _ = createUser app.Db "userb"

        let! status, html = get app "en" $"/u/nobody/anime/{series}"
        Assert.Equal(HttpStatusCode.NotFound, status)
        Assert.Contains(Texts.en.UserNotFoundTitle, html)

        for id in [ series.ToLowerInvariant(); "ABCDE"; String('A', 25); "GR-K5CNJ14"; "GRK5CNJ14%20" ] do
            let! status, html = get app "en" $"/u/animenerd/anime/{id}"
            Assert.Equal(HttpStatusCode.NotFound, status)
            Assert.Contains(Texts.en.NotFoundTitle, html)

        // well-formed, but not watched by this user (nobody has watched it, or only someone else)
        for path in [ "/u/animenerd/anime/ZZZZZZZZZ"; $"/u/userb/anime/{series}" ] do
            let! status, html = get app "en" path
            Assert.Equal(HttpStatusCode.NotFound, status)
            Assert.Contains(Texts.en.NotFoundTitle, html)
    }

[<Fact>]
let ``a series whose watches are all unresolved has no page`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        do! Db.execute app.Db $"UPDATE cr_episodes SET duration_ms = NULL WHERE series_id = '{series}'"
        let! status, _ = get app "en" $"/u/animenerd/anime/{series}"
        Assert.Equal(HttpStatusCode.NotFound, status)
    }

// ---------------------------------------------------------------------------
// The Crunchyroll link (FR-004)
// ---------------------------------------------------------------------------

let private crunchyrollTag (html: string) =
    Regex.Match(html, "<a\\b[^>]*data-crunchyroll-link=\"\"[^>]*>").Value

[<Fact>]
let ``the Crunchyroll link has the stored slug, and is absent without one`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! slug = Db.scalar<string> app.Db $"SELECT slug FROM cr_series WHERE id = '{series}'" []
        Assert.False(String.IsNullOrEmpty slug)

        let! html = anime app "en" series
        let tag = crunchyrollTag html
        Assert.Contains($"href=\"https://www.crunchyroll.com/series/{series}/{slug}\"", tag)
        Assert.Contains("target=\"_blank\"", tag)
        Assert.Contains("rel=\"noopener\"", tag)

        do! Db.execute app.Db $"UPDATE cr_series SET slug = NULL WHERE id = '{series}'"
        let! without = anime app "en" series
        Assert.Empty(elements "crunchyroll-link" without)
        Assert.Single(elements "dashboard-link" without) |> ignore
    }

[<Fact>]
let ``a series with no series row uses its episodes' series title and has no Crunchyroll link`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! fromEpisodes = Db.scalar<string> app.Db $"SELECT min(series_title) FROM cr_episodes WHERE series_id = '{series}'" []
        Assert.False(String.IsNullOrEmpty fromEpisodes)

        let! stored = Db.scalar<string> app.Db $"SELECT title FROM cr_series WHERE id = '{series}'" []
        let! withRow = anime app "en" series
        Assert.Equal(stored, title withRow)

        do! Db.execute app.Db $"DELETE FROM cr_series WHERE id = '{series}'"
        let! html = anime app "en" series
        Assert.Equal(fromEpisodes, title html)
        Assert.Empty(elements "crunchyroll-link" html)
        let! _ = assertPage app.Db series html
        ()
    }

// ---------------------------------------------------------------------------
// Languages (FR-013)
// ---------------------------------------------------------------------------

[<Fact>]
let ``audio languages are named in the viewer's language and MyAnimeList's names are not translated`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! en = anime app "en" series
        let! es = anime app "es" series
        Assert.Contains(Texts.en.AnimeSeasonsTitle, en)
        Assert.Contains(Texts.es.AnimeSeasonsTitle, es)
        Assert.NotEqual<string>(Texts.en.AnimeSeasonsTitle, Texts.es.AnimeSeasonsTitle)

        let codes = outline en |> List.map (fun s -> attr "audio" s.Attrs) |> List.filter ((<>) "") |> List.distinct
        Assert.NotEmpty codes

        for code in codes do
            Assert.Contains(Texts.en.AudioLanguage code, en)
            Assert.Contains(Texts.es.AudioLanguage code, es)

        Assert.Equal("Japanese", Texts.en.AudioLanguage "ja-JP")
        Assert.Equal("Japonés", Texts.es.AudioLanguage "ja-JP")
        Assert.Equal("xx-XX", Texts.es.AudioLanguage "xx-XX")

        // the stored code is the hook in both languages, and MyAnimeList's own names stay as they are
        Assert.Equal<string list>(signature en |> List.filter (fun s -> s.StartsWith "data-audio"), signature es |> List.filter (fun s -> s.StartsWith "data-audio"))
        Assert.Contains("Adventure", es)
        Assert.Contains("Fantasy", es)
        Assert.Contains("TV", es)
        Assert.Contains("Saijaku Tamer wa Gomi Hiroi no Tabi wo Hajimemashita.", es)

        // the same structure in both languages
        let structure html = outline html |> List.map (fun s -> attr "season" s.Attrs, s.Episodes |> List.map (attr "episode"))
        Assert.Equal<(string * string list) list>(structure en, structure es)
    }

// ---------------------------------------------------------------------------
// Stability (FR-012)
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is identical after re-sending and after a rebuild, and viewing never calls MyAnimeList`` () =
    task {
        let! app, key = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "G6MGC3778"
        let! before = anime app "en" series

        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! resent = anime app "en" series
        Assert.Equal<string list>(signature before, signature resent)

        let! queued = RebuildUser.rebuildUser app.Db "animenerd"
        Assert.True(queued.IsSome)
        let! _ = processAll app.Db
        app.Mal.Clear()
        let! rebuilt = anime app "en" series
        Assert.Equal<string list>(signature before, signature rebuilt)
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``the poster is decorative and falls back to its box when the image fails to load`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! series = seriesOfSeason app.Db "GRK5CNJ14"
        let! html = anime app "en" series
        let imgs = [ for m in Regex.Matches(html, "<img\\b[^>]*>") -> m.Value ]
        Assert.NotEmpty imgs

        for img in imgs do
            Assert.Contains("alt=\"\"", img)
            Assert.Contains("onerror=\"this.remove()\"", img)
    }
