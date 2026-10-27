module FrunchyBackend.Tests.DnaTests

open System
open System.Globalization
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 004, User Story 1: the Anime DNA page (contracts/dna-page.md)

let private withSample () =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animegolden"
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

let private dna (app: TestApp) = task { let! _, html = get app "en" "/u/animegolden/dna" in return html }

/// The data-* attributes of every element carrying data-{hook}, in document order
let private elements (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}(=\"[^\"]*\")?[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private one hook html = elements hook html |> List.exactlyOne
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""

/// Every hook the page renders, to compare two renderings
let private signature (html: string) =
    [ for m in Regex.Matches(html, "data-(?!address)[a-z-]+=\"[^\"]*\"") -> m.Value ]

// ---------------------------------------------------------------------------
// Expected figures, computed here in F# from raw rows, independently of the slice's SQL
// ---------------------------------------------------------------------------

type private Watched =
    { Id: string
      Series: string
      /// The original season (a dub's shared_from), else the season: what a watched season of an unmatched show is keyed on
      Season: string
      Key: string
      Ms: int64
      Fully: bool
      Mal: int option
      Aired: DateTime option }

let private round1 (d: decimal) = Math.Round(d, 1, MidpointRounding.AwayFromZero)
let private pct (part: int64) (whole: int64) = if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)
let private inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)

/// year * 4 + quarter (0 = winter) of an air date, the UTC quarter
let private seasonNo (aired: DateTime) = aired.Year * 4 + (aired.Month - 1) / 3

/// A dedup group of the DNA page: the best watch stands for it (most minutes, ties the smaller id); it aired when its
/// earliest version did and was fully watched when any version was
type private Group =
    { Best: Watched
      Aired: DateTime option
      Full: bool }

let private expected (db: DbConfig) =
    task {
        let! rows =
            Db.dump
                db
                """
                SELECT e.id, e.series_id, coalesce(m.shared_from, e.season_id, ''), coalesce(e.episode_number::text, ''),
                       w.fully_watched, w.playhead_s, e.duration_ms, coalesce(m.mal_id::text, ''),
                       coalesce(to_char(e.aired_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'), '')
                FROM watch_events w JOIN users u ON u.id = w.user_id
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE u.username = 'animegolden' AND e.duration_ms IS NOT NULL
                """

        let all =
            [ for row in rows ->
                  match row.Split '|' with
                  | [| id; series; original; number; fully; playhead; duration; mal; aired |] ->
                      let fully = Boolean.Parse fully
                      let duration = int64 duration

                      { Id = id
                        Series = series
                        Season = (if original = "" then series else original)
                        Key = (if number = "" || original = "" then id else $"{original}#{number}")
                        Ms = (if fully then duration else min (int64 playhead * 1000L) duration)
                        Fully = fully
                        Mal = (if mal = "" then None else Some(int mal))
                        Aired = (if aired = "" then None else Some(DateTime.Parse(aired, CultureInfo.InvariantCulture))) }
                  | _ -> failwith row ]

        // A dub and its original: one episode, the larger minutes (ties: the smaller id)
        let groups =
            all
            |> List.groupBy _.Key
            |> List.map (fun (_, xs) ->
                { Best = xs |> List.sortBy (fun x -> -x.Ms, x.Id) |> List.head
                  Aired = xs |> List.choose _.Aired |> List.sort |> List.tryHead
                  Full = xs |> List.exists _.Fully })

        let dedup = groups |> List.map _.Best

        let! tags = Db.dump db "SELECT at.mal_id, at.kind, t.name FROM mal_anime_tags at JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id"
        let tags = [ for r in tags -> let p = r.Split '|' in int p[0], p[1], p[2] ]
        let! anime = Db.dump db "SELECT mal_id, coalesce(type, ''), coalesce(year, extract(year FROM aired_from)::int)::text, coalesce(episodes, 0) FROM mal_anime"
        let anime = anime |> List.map (fun r -> let p = r.Split '|' in int p[0], (p[1], p[2], int p[3])) |> Map.ofList
        let! titles = Db.dump db "SELECT e.series_id, coalesce(max(s.title), min(e.series_title), e.series_id) FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id GROUP BY e.series_id"
        let titles = titles |> List.map (fun r -> let p = r.Split '|' in p[0], p[1]) |> Map.ofList

        // Long runners: a matched season of the series has more than 100 episodes on MyAnimeList, or any catalog
        // episode of it is numbered above 100
        let! numbered = Db.dump db "SELECT DISTINCT series_id FROM cr_episodes WHERE episode_number > 100"
        let! malRunners =
            Db.dump
                db
                "SELECT DISTINCT sea.series_id FROM cr_seasons sea JOIN cr_season_matches m ON m.season_id = sea.id AND m.status = 'matched' JOIN mal_anime a ON a.mal_id = m.mal_id WHERE a.episodes > 100"

        let longRunners = Set.ofList (numbered @ malRunners)

        let seasonOf (g: Group) = g.Aired |> Option.map seasonNo

        // Watched seasons: a MyAnimeList entry in an airing season (None without an air date)
        let watchedSeasons =
            groups
            |> List.filter (fun g -> g.Full && g.Best.Mal.IsSome)
            |> List.map (fun g -> g.Best.Mal.Value, seasonOf g)
            |> List.distinct

        let unmatchedSeasons =
            groups
            |> List.filter (fun g -> g.Full && g.Best.Mal.IsNone)
            |> List.map (fun g -> g.Best.Season, seasonOf g)
            |> List.distinct

        let seasonsMatched = int64 watchedSeasons.Length
        let seasonsTotal = seasonsMatched + int64 unmatchedSeasons.Length
        let ranked (xs: (string * int64) list) = xs |> List.sortBy (fun (n, c) -> -c, n)

        let byTag kind =
            [ for mal, sn in watchedSeasons do
                  for m, k, name in tags do
                      if k = kind && m = mal then yield name, (mal, sn) ]

        let counted kind =
            byTag kind |> List.groupBy fst |> List.map (fun (n, xs) -> n, int64 xs.Length) |> ranked

        let studios = counted "studio"
        let genres = counted "genre"
        let genreTotal = genres |> List.sumBy snd

        // a genre's top series: the most watched seasons in it, then the most minutes in all, then title, then id
        let seriesMs = dedup |> List.groupBy _.Series |> List.map (fun (s, ws) -> s, ws |> List.sumBy _.Ms) |> Map.ofList

        let genreTop =
            tags
            |> List.filter (fun (_, k, _) -> k = "genre")
            |> List.map (fun (_, _, n) -> n)
            |> List.distinct
            |> List.choose (fun name ->
                let perSeries =
                    groups
                    |> List.filter (fun g -> g.Full && g.Best.Mal.IsSome)
                    |> List.filter (fun g -> tags |> List.exists (fun (m, k, n) -> k = "genre" && n = name && m = g.Best.Mal.Value))
                    |> List.map (fun g -> g.Best.Series, g.Best.Mal.Value, seasonOf g)
                    |> List.distinct
                    |> List.countBy (fun (s, _, _) -> s)

                match perSeries with
                | [] -> None
                | _ -> Some(name, perSeries |> List.sortBy (fun (s, c) -> -c, -seriesMs[s], titles[s], s) |> List.head |> fst))
            |> Map.ofList

        let yearOf (mal: int, sn: int option) =
            match sn with
            | Some n -> Some(n / 4)
            | None -> anime.TryFind mal |> Option.map (fun (_, y, _) -> y) |> Option.filter ((<>) "") |> Option.map int

        let withYear = watchedSeasons |> List.filter (yearOf >> Option.isSome)

        let decades =
            withYear
            |> List.groupBy (fun ws -> string (yearOf ws).Value |> int |> fun y -> string (y / 10 * 10))
            |> List.map (fun (d, xs) -> d, int64 xs.Length)
            |> ranked

        let formats =
            watchedSeasons
            |> List.choose (fun (mal, _) -> anime.TryFind mal |> Option.map (fun (t, _, _) -> t) |> Option.filter ((<>) ""))
            |> List.countBy id
            |> List.map (fun (f, c) -> f, int64 c)
            |> ranked

        let insignia =
            dedup
            |> List.filter (fun w -> not (longRunners.Contains w.Series))
            |> List.groupBy _.Series
            |> List.map (fun (s, ws) -> s, ws |> List.sumBy _.Ms, ws |> List.filter _.Fully |> List.length)
            |> List.sortBy (fun (s, ms, _) -> -ms, titles[s], s)
            |> List.tryHead

        let allMs = dedup |> List.sumBy _.Ms
        let matchedMs = dedup |> List.filter _.Mal.IsSome |> List.sumBy _.Ms

        return
            {| All = all
               Dedup = dedup
               Groups = groups
               LongRunners = longRunners
               SeasonsTotal = seasonsTotal
               SeasonsMatched = seasonsMatched
               SeasonsWithYear = int64 withYear.Length
               Coverage = pct matchedMs allMs
               Studios = studios |> List.truncate 2 |> List.map (fun (n, c) -> n, inv (pct c seasonsMatched), c)
               Decades = decades |> List.map (fun (d, c) -> d, inv (pct c (int64 withYear.Length)), c)
               Genres = genres |> List.truncate 5 |> List.map (fun (n, c) -> n, inv (pct c genreTotal), genreTop[n], c)
               Others = genreTotal - (genres |> List.truncate 5 |> List.sumBy snd)
               GenreTotal = genreTotal
               Formats = formats |> List.map (fun (f, c) -> f, inv (pct c seasonsMatched), c)
               FormatsByEpisodes =
                dedup
                |> List.choose (fun w -> w.Mal |> Option.bind anime.TryFind |> Option.map (fun (t, _, _) -> t))
                |> List.countBy id
                |> Map.ofList
               Insignia = insignia |}
    }

// ---------------------------------------------------------------------------
// Figures
// ---------------------------------------------------------------------------

[<Fact>]
let ``every figure equals an independent count over the same data`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! html = dna app
        let! e = expected app.Db

        // the sample exercises the dub rule
        Assert.True(e.All.Length > e.Dedup.Length, "the 2026 sample should contain a sub/dub pair")

        let seasonsCoverage = inv (pct e.SeasonsMatched e.SeasonsTotal)
        Assert.Equal(seasonsCoverage, attr "coverage" (one "dna-studio" html))
        Assert.Equal(seasonsCoverage, attr "coverage" (one "dna-genres" html))
        Assert.Equal(seasonsCoverage, attr "coverage" (one "dna-formats" html))
        Assert.Equal(inv (pct e.SeasonsWithYear e.SeasonsTotal), attr "coverage" (one "dna-decade" html))

        // the notes say which seasons the card is based on
        let notes = elements "coverage-seasons" html |> List.map (attr "coverage-seasons")
        Assert.Equal<string list>(
            [ $"{e.SeasonsMatched}/{e.SeasonsTotal}"; $"{e.SeasonsWithYear}/{e.SeasonsTotal}"; $"{e.SeasonsMatched}/{e.SeasonsTotal}"; $"{e.SeasonsMatched}/{e.SeasonsTotal}" ],
            notes
        )

        let studios = elements "rank" html |> List.map (fun m -> attr "name" m, attr "share" m, attr "studio-seasons" m)
        Assert.Equal<(string * string * string) list>(e.Studios |> List.map (fun (n, s, c) -> n, s, string c), studios)

        let decades = elements "decade" html |> List.filter (fun m -> m.ContainsKey "decade-seasons") |> List.map (fun m -> attr "decade" m, attr "share" m, attr "decade-seasons" m)
        Assert.Equal<(string * string * string) list>(e.Decades |> List.map (fun (d, s, c) -> d, s, string c), decades)
        let topDecade, _, _ = e.Decades.Head
        Assert.Equal(topDecade, attr "name" (one "dna-decade" html))

        let genres = elements "genre" html
        let named = genres |> List.filter (fun m -> attr "genre" m <> "others" && m.ContainsKey "genre-seasons")
        Assert.Equal<(string * string * string * string) list>(
            e.Genres |> List.map (fun (n, s, top, c) -> n, s, top, string c),
            named |> List.map (fun m -> attr "genre" m, attr "share" m, attr "top-series" m, attr "genre-seasons" m)
        )

        if e.Others > 0L then
            let last = genres |> List.filter (fun m -> m.ContainsKey "genre-seasons") |> List.last
            Assert.Equal(inv (pct e.Others e.GenreTotal), attr "share" last)
            Assert.Equal(string e.Others, attr "genre-seasons" last)

        let total = genres |> List.filter (fun m -> m.ContainsKey "genre-seasons") |> List.sumBy (fun m -> Decimal.Parse(attr "share" m, CultureInfo.InvariantCulture))
        Assert.InRange(total, 99.5m, 100.5m)

        let formats = elements "format" html |> List.filter (fun m -> m.ContainsKey "format-seasons") |> List.map (fun m -> attr "format" m, attr "share" m, attr "format-seasons" m)
        Assert.Equal<(string * string * string) list>(e.Formats |> List.map (fun (f, s, c) -> f, s, string c), formats)

        match e.Insignia with
        | Some(series, _, fully) ->
            let insignia = one "dna-insignia" html
            Assert.Equal(series, attr "series" insignia)
            Assert.Equal(series, attr "insignia-series" insignia)
            Assert.Equal(string fully, attr "episodes" insignia)
        | None -> Assert.Empty(elements "dna-insignia" html)
    }

[<Fact>]
let ``the formats count watched seasons, not minutes or episodes`` () =
    task {
        let! app, _ = withSample ()
        use app = app

        // The sample's movie (JUJUTSU KAISEN 0) is barely watched; watch it to the end
        do!
            Db.execute
                app.Db
                """
                UPDATE watch_events w SET fully_watched = true
                FROM cr_episodes e JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                JOIN mal_anime a ON a.mal_id = m.mal_id
                WHERE w.cr_episode_id = e.id AND a.type = 'Movie'
                """

        let! html = dna app
        let! e = expected app.Db
        let movie = elements "format" html |> List.tryFind (fun m -> attr "format" m = "Movie")
        Assert.True(movie.IsSome, "the 2026 sample includes a movie (JUJUTSU KAISEN 0)")

        // one watched season, however long the movie runs: its share is 1 over the matched seasons
        let name, share, seasons = e.Formats |> List.find (fun (f, _, _) -> f = "Movie")
        Assert.Equal("Movie", name)
        Assert.Equal(share, attr "share" movie.Value)
        Assert.Equal(string seasons, attr "format-seasons" movie.Value)
        Assert.Equal("1", attr "format-seasons" movie.Value)
        Assert.Equal(inv (pct 1L e.SeasonsMatched), attr "share" movie.Value)
    }

[<Fact>]
let ``the totals equal the dashboard's`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! html = dna app
        let! _, dashboard = get app "en" "/u/animegolden"

        for name in [ "hours"; "days"; "episodes" ] do
            let onDashboard = Regex.Match(dashboard, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value
            let onDna = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value
            Assert.NotEqual<string>("", onDashboard)
            Assert.Equal(onDashboard, onDna)
    }

// ---------------------------------------------------------------------------
// Coverage and states
// ---------------------------------------------------------------------------

[<Fact>]
let ``explicit genres never appear`` () =
    task {
        let! app, _ = withSample ()
        use app = app

        do!
            Db.execute
                app.Db
                """
                INSERT INTO mal_tags (kind, tag_id, name) VALUES ('explicit_genre', 12, 'Hentai') ON CONFLICT DO NOTHING;
                INSERT INTO mal_anime_tags (mal_id, kind, tag_id, position)
                SELECT mal_id, 'explicit_genre', 12, 0 FROM cr_season_matches WHERE status = 'matched' ON CONFLICT DO NOTHING;
                """

        let! html = dna app
        Assert.DoesNotContain("Hentai", html)
        Assert.NotEmpty(elements "genre" html)
    }

[<Fact>]
let ``coverage drops with unmatched seasons; with none matched only the MyAnimeList sections say so`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! before = dna app
        let coverage html = Decimal.Parse(attr "coverage" (one "dna-studio" html), CultureInfo.InvariantCulture)

        do! Db.execute app.Db "UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, shared_from = NULL WHERE season_id = 'GYE5CQMQ5' OR shared_from = 'GYE5CQMQ5'"
        let! fewer = dna app
        Assert.True(coverage fewer < coverage before, $"{coverage fewer} < {coverage before}")

        do! Db.execute app.Db "UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, shared_from = NULL"
        let! none = dna app

        for hook in [ "dna-studio"; "dna-decade"; "dna-genres"; "dna-formats" ] do
            Assert.Single(elements hook none) |> ignore
            Assert.Contains($"data-{hook}=\"\"", Regex.Match(none, $"<section[^>]*data-{hook}=\"\"[^>]*>").Value)

        Assert.Equal(4, Regex.Matches(none, "data-not-enough").Count - Regex.Matches(none, "data-dna-pace=\"\"[^>]*data-not-enough").Count)
        Assert.Single(elements "dna-totals" none) |> ignore
        Assert.Single(elements "dna-insignia" none) |> ignore
    }

[<Fact>]
let ``fewer than 15 fully watched episodes shows the empty state with the count`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! html = dna app
        Assert.Equal("0", attr "value" (one "dna-empty" html))
        Assert.Empty(elements "dna-totals" html)

        let at = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let entries n = [ for i in 1..n -> syntheticEntry $"E{i:D3}" $"SER{i:D3}" (at.AddHours(float i)) true 1400 ]
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (entries 14) ])
        let! _ = processAll app.Db
        let! fourteen = dna app
        Assert.Equal("14", attr "value" (one "dna-empty" fourteen))
        Assert.Contains("14 of 15 fully watched episodes", fourteen)

        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (entries 15) ])
        let! _ = processAll app.Db
        let! fifteen = dna app
        Assert.Empty(elements "dna-empty" fifteen)
        Assert.Single(elements "dna-totals" fifteen) |> ignore
    }

[<Fact>]
let ``an unknown user gets the not-found page`` () =
    task {
        use! app = newApp ()
        let! status, html = get app "en" "/u/nobody/dna"
        Assert.Equal(HttpStatusCode.NotFound, status)
        Assert.Contains("User not found", html)
    }

[<Fact>]
let ``the dashboard and the DNA page link to each other, and the page has a copy-link control`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! _, dashboard = get app "en" "/u/animegolden"
        Assert.Matches("<a href=\"/u/animegolden/dna\"[^>]*data-dna-link", dashboard)
        let! html = dna app
        Assert.Matches("<a href=\"/u/animegolden\"[^>]*data-dashboard-link", html)
        Assert.Matches("data-copy-link", html)
        Assert.Contains("data-address=\"http://localhost/u/animegolden/dna\"", html)
    }

[<Fact>]
let ``the page is localized; MyAnimeList names are not`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! _, en = get app "en" "/u/animegolden/dna"
        let! _, es = get app "es-ES,es;q=0.9" "/u/animegolden/dna"
        Assert.Contains("lang=\"es\"", es)
        Assert.Contains("ADN anime", es)
        Assert.Contains("Estudio favorito", es)
        Assert.Contains("Favourite studio", en)

        let genres html = elements "genre" html |> List.map (attr "genre")
        Assert.Equal<string list>(genres en, genres es)

        // The insignia's season is our label, so it is translated; the year is not
        let season html = Regex.Match(html, "(Winter|Spring|Summer|Fall|Invierno|Primavera|Verano|Otoño) (\\d{4})").Value
        let seasonEn, seasonEs = season en, season es

        if seasonEn <> "" then
            Assert.NotEqual<string>(seasonEn, seasonEs)
            Assert.Equal(seasonEn.Split(' ')[1], seasonEs.Split(' ')[1])
    }

// ---------------------------------------------------------------------------
// Stability (FR-016) and no external calls
// ---------------------------------------------------------------------------

[<Fact>]
let ``the figures are identical after a rebuild and after re-sending the same history`` () =
    task {
        let! app, key = withSample ()
        use app = app
        let! before = dna app

        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! resent = dna app
        Assert.Equal<string list>(signature before, signature resent)

        let! queued = RebuildUser.rebuildUser app.Db "animegolden"
        Assert.True(queued.IsSome)
        let! _ = processAll app.Db
        let! rebuilt = dna app
        Assert.Equal<string list>(signature before, signature rebuilt)
    }

[<Fact>]
let ``viewing the page never calls MyAnimeList`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        app.Mal.Clear()
        let! _ = dna app
        let! _ = get app "es" "/u/animegolden/dna"
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``the insignia poster is decorative (empty alt) and falls back to its box when the image fails to load`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! html = dna app
        let imgs = [ for m in Regex.Matches(html, "<img\\b[^>]*>") -> m.Value ]
        Assert.NotEmpty imgs

        for img in imgs do
            Assert.Contains("alt=\"\"", img)
            Assert.Contains("onerror=\"this.remove()\"", img)
    }
