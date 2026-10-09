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

let private dna (app: TestApp) = task { let! _, html = get app "en" "/u/animenerd/dna" in return html }

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
      Key: string
      Ms: int64
      Fully: bool
      Mal: int option }

let private round1 (d: decimal) = Math.Round(d, 1, MidpointRounding.AwayFromZero)
let private pct (part: int64) (whole: int64) = if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)
let private inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)

let private expected (db: DbConfig) =
    task {
        let! rows =
            Db.dump
                db
                """
                SELECT e.id, e.series_id, coalesce(m.shared_from, e.season_id, ''), coalesce(e.episode_number::text, ''),
                       w.fully_watched, w.playhead_s, e.duration_ms, coalesce(m.mal_id::text, '')
                FROM watch_events w JOIN users u ON u.id = w.user_id
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE u.username = 'animenerd' AND e.duration_ms IS NOT NULL
                """

        let all =
            [ for row in rows ->
                  match row.Split '|' with
                  | [| id; series; original; number; fully; playhead; duration; mal |] ->
                      let fully = Boolean.Parse fully
                      let duration = int64 duration

                      { Id = id
                        Series = series
                        Key = (if number = "" || original = "" then id else $"{original}#{number}")
                        Ms = (if fully then duration else min (int64 playhead * 1000L) duration)
                        Fully = fully
                        Mal = (if mal = "" then None else Some(int mal)) }
                  | _ -> failwith row ]

        // A dub and its original: one episode, the larger minutes (ties: the smaller id)
        let dedup =
            all |> List.groupBy _.Key |> List.map (fun (_, xs) -> xs |> List.sortBy (fun x -> -x.Ms, x.Id) |> List.head)

        let! tags = Db.dump db "SELECT at.mal_id, at.kind, t.name FROM mal_anime_tags at JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id"
        let tags = [ for r in tags -> let p = r.Split '|' in int p[0], p[1], p[2] ]
        let! anime = Db.dump db "SELECT mal_id, coalesce(type, ''), coalesce(year, extract(year FROM aired_from)::int)::text FROM mal_anime"
        let anime = anime |> List.map (fun r -> let p = r.Split '|' in int p[0], (p[1], p[2])) |> Map.ofList
        let! titles = Db.dump db "SELECT e.series_id, coalesce(max(s.title), min(e.series_title), e.series_id) FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id GROUP BY e.series_id"
        let titles = titles |> List.map (fun r -> let p = r.Split '|' in p[0], p[1]) |> Map.ofList

        let matched = dedup |> List.filter _.Mal.IsSome
        let allMs = dedup |> List.sumBy _.Ms
        let matchedMs = matched |> List.sumBy _.Ms
        let ranked (xs: (string * int64) list) = xs |> List.sortBy (fun (n, ms) -> -ms, n)

        let byTag kind =
            [ for w in matched do
                  for mal, k, name in tags do
                      if k = kind && Some mal = w.Mal then yield name, w ]

        let studios = byTag "studio" |> List.groupBy fst |> List.map (fun (n, ws) -> n, ws |> List.sumBy (snd >> _.Ms)) |> ranked

        let genres = byTag "genre" |> List.groupBy fst |> List.map (fun (n, ws) -> n, ws |> List.sumBy (snd >> _.Ms)) |> ranked
        let genreTotal = genres |> List.sumBy snd

        let genreTop =
            byTag "genre"
            |> List.groupBy fst
            |> List.map (fun (n, ws) ->
                let top =
                    ws
                    |> List.map snd
                    |> List.groupBy _.Series
                    |> List.map (fun (s, xs) -> s, xs |> List.sumBy _.Ms)
                    |> List.sortBy (fun (s, ms) -> -ms, titles[s], s)
                    |> List.head
                    |> fst

                n, top)
            |> Map.ofList

        let yearOf (w: Watched) = w.Mal |> Option.bind anime.TryFind |> Option.map snd |> Option.filter ((<>) "")
        let withYear = matched |> List.filter (yearOf >> Option.isSome)
        let yearMs = withYear |> List.sumBy _.Ms

        let decades =
            withYear |> List.groupBy (fun w -> string (int (yearOf w).Value / 10 * 10)) |> List.map (fun (d, ws) -> d, ws |> List.sumBy _.Ms) |> ranked

        let formats =
            matched
            |> List.choose (fun w -> w.Mal |> Option.bind anime.TryFind |> Option.map fst |> Option.filter ((<>) "") |> Option.map (fun f -> f, w.Ms))
            |> List.groupBy fst
            |> List.map (fun (f, xs) -> f, xs |> List.sumBy snd)
            |> ranked

        let insignia =
            dedup
            |> List.groupBy _.Series
            |> List.map (fun (s, ws) -> s, ws |> List.sumBy _.Ms, ws |> List.filter _.Fully |> List.length)
            |> List.sortBy (fun (s, ms, _) -> -ms, titles[s], s)
            |> List.head

        return
            {| All = all
               Dedup = dedup
               Coverage = pct matchedMs allMs
               YearCoverage = pct yearMs allMs
               Studios = studios |> List.truncate 2 |> List.map (fun (n, ms) -> n, inv (pct ms matchedMs))
               Decades = decades |> List.map (fun (d, ms) -> d, inv (pct ms yearMs))
               Genres = genres |> List.truncate 5 |> List.map (fun (n, ms) -> n, inv (pct ms genreTotal), genreTop[n])
               Others = genreTotal - (genres |> List.truncate 5 |> List.sumBy snd)
               GenreTotal = genreTotal
               Formats = formats |> List.map (fun (f, ms) -> f, inv (pct ms matchedMs))
               FormatsByEpisodes =
                matched
                |> List.choose (fun w -> w.Mal |> Option.bind anime.TryFind |> Option.map fst)
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

        Assert.Equal(inv e.Coverage, attr "coverage" (one "dna-studio" html))
        Assert.Equal(inv e.YearCoverage, attr "coverage" (one "dna-decade" html))

        let studios = elements "rank" html |> List.map (fun m -> attr "name" m, attr "share" m)
        Assert.Equal<(string * string) list>(e.Studios, studios)

        let decades = elements "decade" html |> List.map (fun m -> attr "decade" m, attr "share" m)
        Assert.Equal<(string * string) list>(e.Decades, decades)
        Assert.Equal(fst e.Decades.Head, attr "name" (one "dna-decade" html))

        let genres = elements "genre" html
        let named = genres |> List.filter (fun m -> attr "genre" m <> "others")
        Assert.Equal<(string * string * string) list>(e.Genres, named |> List.map (fun m -> attr "genre" m, attr "share" m, attr "top-series" m))

        if e.Others > 0L then
            Assert.Equal(inv (pct e.Others e.GenreTotal), genres |> List.last |> attr "share")

        let total = genres |> List.sumBy (fun m -> Decimal.Parse(attr "share" m, CultureInfo.InvariantCulture))
        Assert.InRange(total, 99.5m, 100.5m)

        let formats = elements "format" html |> List.map (fun m -> attr "format" m, attr "share" m)
        Assert.Equal<(string * string) list>(e.Formats, formats)

        let series, _, fully = e.Insignia
        let insignia = one "dna-insignia" html
        Assert.Equal(series, attr "series" insignia)
        Assert.Equal(string fully, attr "episodes" insignia)
    }

[<Fact>]
let ``the formats weigh minutes watched, not episodes`` () =
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

        // An episode-count share would be far smaller than the movie's share of watch time
        let byEpisodes =
            pct (int64 e.FormatsByEpisodes["Movie"]) (int64 (e.FormatsByEpisodes |> Map.toList |> List.sumBy snd))

        let shown = Decimal.Parse(attr "share" movie.Value, CultureInfo.InvariantCulture)
        Assert.Equal(e.Formats |> List.find (fst >> (=) "Movie") |> snd, attr "share" movie.Value)
        Assert.True(shown > byEpisodes, $"movie share {shown} should exceed its episode share {byEpisodes}")
    }

[<Fact>]
let ``the totals equal the dashboard's`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! html = dna app
        let! _, dashboard = get app "en" "/u/animenerd"

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
        let! key = createUser app.Db "animenerd"
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
        let! _, dashboard = get app "en" "/u/animenerd"
        Assert.Matches("<a href=\"/u/animenerd/dna\"[^>]*data-dna-link", dashboard)
        let! html = dna app
        Assert.Matches("<a href=\"/u/animenerd\"[^>]*data-dashboard-link", html)
        Assert.Matches("data-copy-link", html)
        Assert.Contains("data-address=\"http://localhost/u/animenerd/dna\"", html)
    }

[<Fact>]
let ``the page is localized; MyAnimeList names are not`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! _, en = get app "en" "/u/animenerd/dna"
        let! _, es = get app "es-ES,es;q=0.9" "/u/animenerd/dna"
        Assert.Contains("lang=\"es\"", es)
        Assert.Contains("ADN anime", es)
        Assert.Contains("Estudio favorito", es)
        Assert.Contains("Favourite studio", en)

        let genres html = elements "genre" html |> List.map (attr "genre")
        Assert.Equal<string list>(genres en, genres es)
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

        let! queued = RebuildUser.rebuildUser app.Db "animenerd"
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
        let! _ = get app "es" "/u/animenerd/dna"
        Assert.Empty app.Mal.Requests
    }
