module FrunchyBackend.Tests.YearTests

open System
open System.Globalization
open System.Net
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 005, User Story 1: the year page (contracts/year-page.md). The 2026 sample spans 2021–2026.

let private now = DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)

let private appWithClock () =
    task {
        let! db = newDatabase ()
        let clock = TestClock(Now = now)
        return! createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
    }

let private withSample () =
    task {
        let! app = appWithClock ()
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

let private page (app: TestApp) (year: int) = task { let! _, html = get app "en" $"/u/animegolden/year/{year}" in return html }

let private elements (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}(=\"[^\"]*\")?[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private one hook html = elements hook html |> List.exactlyOne
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""
let private stat (name: string) (html: string) = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value

let private signature (html: string) =
    [ for m in Regex.Matches(html, "data-(?!address)[a-z-]+=\"[^\"]*\"") -> m.Value ]

// ---------------------------------------------------------------------------
// Expected figures, computed in F# from raw rows, independently of the slice's SQL
// ---------------------------------------------------------------------------

type private Watched =
    { Id: string
      Series: string
      /// The original season (a dub's shared_from), else the season, else the series: an unmatched show's watched season
      Season: string
      Key: string
      Ms: int64
      Fully: bool
      At: DateTime
      Mal: int option
      Aired: DateTime option }

let private round1 (d: decimal) = Math.Round(d, 1, MidpointRounding.AwayFromZero)
let private pct (part: int64) (whole: int64) = if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)
let private inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)

let private watched (db: DbConfig) =
    task {
        let! rows =
            Db.dump
                db
                """
                SELECT e.id, e.series_id, coalesce(m.shared_from, e.season_id, ''), coalesce(e.episode_number::text, ''),
                       w.fully_watched, w.playhead_s, e.duration_ms, coalesce(m.mal_id::text, ''),
                       to_char(w.watched_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'),
                       coalesce(to_char(e.aired_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS'), '')
                FROM watch_events w JOIN users u ON u.id = w.user_id
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE u.username = 'animegolden' AND e.duration_ms IS NOT NULL
                """

        return
            [ for row in rows ->
                  match row.Split '|' with
                  | [| id; series; original; number; fully; playhead; duration; mal; at; aired |] ->
                      let fully = Boolean.Parse fully
                      let duration = int64 duration

                      { Id = id
                        Series = series
                        Season = (if original = "" then series else original)
                        Key = (if number = "" || original = "" then id else $"{original}#{number}")
                        Ms = (if fully then duration else min (int64 playhead * 1000L) duration)
                        Fully = fully
                        At = DateTime.Parse(at, CultureInfo.InvariantCulture)
                        Mal = (if mal = "" then None else Some(int mal))
                        Aired = (if aired = "" then None else Some(DateTime.Parse(aired, CultureInfo.InvariantCulture))) }
                  | _ -> failwith row ]
    }

let private expected (db: DbConfig) (year: int) =
    task {
        let! all = watched db
        let inYear = all |> List.filter (fun w -> w.At.Year = year)
        // a dub and its original are one episode: the best watch stands for it, it aired when its earliest version
        // did and was fully watched when any version was
        let groups =
            inYear
            |> List.groupBy _.Key
            |> List.map (fun (_, xs) ->
                xs |> List.sortBy (fun x -> -x.Ms, x.Id) |> List.head,
                xs |> List.choose _.Aired |> List.sort |> List.tryHead,
                xs |> List.exists _.Fully)

        let dedup = groups |> List.map (fun (best, _, _) -> best)

        let! tags = Db.dump db "SELECT at.mal_id, at.kind, t.name FROM mal_anime_tags at JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id"
        let tags = [ for r in tags -> let p = r.Split '|' in int p[0], p[1], p[2] ]
        let! types = Db.dump db "SELECT mal_id, coalesce(type, '') FROM mal_anime"
        let types = types |> List.map (fun r -> let p = r.Split '|' in int p[0], p[1]) |> Map.ofList
        let! titles = Db.dump db "SELECT e.series_id, coalesce(max(s.title), min(e.series_title), e.series_id) FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id GROUP BY e.series_id"
        let titles = titles |> List.map (fun r -> let p = r.Split '|' in p[0], p[1]) |> Map.ofList

        // watched seasons of the year: a MyAnimeList entry in an airing season (year * 4 + quarter, None undated)
        let seasonNo (d: DateTime) = d.Year * 4 + (d.Month - 1) / 3

        let watchedSeasons =
            groups
            |> List.filter (fun (best, _, full) -> full && best.Mal.IsSome)
            |> List.map (fun (best, aired, _) -> best.Mal.Value, aired |> Option.map seasonNo)
            |> List.distinct

        let unmatchedSeasons =
            groups
            |> List.filter (fun (best, _, full) -> full && best.Mal.IsNone)
            |> List.map (fun (best, aired, _) -> best.Season, aired |> Option.map seasonNo)
            |> List.distinct

        let seasonsMatched = int64 watchedSeasons.Length
        let seasonsTotal = seasonsMatched + int64 unmatchedSeasons.Length
        let ranked (xs: (string * int64) list) = xs |> List.sortBy (fun (n, c) -> -c, n)

        let counted kind =
            [ for mal, sn in watchedSeasons do
                  for m, k, name in tags do
                      if k = kind && m = mal then yield name, (mal, sn) ]
            |> List.groupBy fst
            |> List.map (fun (n, xs) -> n, int64 xs.Length)
            |> ranked

        let genres = counted "genre"
        let genreTotal = genres |> List.sumBy snd

        let fullyDays = inYear |> List.filter _.Fully |> List.map (fun w -> DateOnly.FromDateTime w.At) |> List.distinct |> List.sort

        let streak =
            fullyDays
            |> List.fold
                (fun (runs: (DateOnly * DateOnly) list) d ->
                    match runs with
                    | (start, last) :: rest when last.AddDays 1 = d -> (start, d) :: rest
                    | _ -> (d, d) :: runs)
                []
            |> List.map (fun (a, b) -> b.DayNumber - a.DayNumber + 1, a, b)
            |> List.sortBy (fun (n, a, _) -> -n, a.DayNumber)
            |> List.tryHead

        let seriesYear = inYear |> List.filter _.Fully |> List.map _.Series |> List.distinct
        let firstYear s = all |> List.filter (fun w -> w.Series = s) |> List.map _.At.Year |> List.min

        let top =
            dedup
            |> List.groupBy _.Series
            |> List.map (fun (s, ws) -> s, ws |> List.sumBy _.Ms, ws |> List.filter _.Fully |> List.length)
            |> List.sortBy (fun (s, ms, _) -> -ms, titles[s], s)
            |> List.head

        return
            {| Hours = inv (round1 (decimal (inYear |> List.sumBy _.Ms) / 3_600_000m))
               Episodes = inYear |> List.filter _.Fully |> List.length
               Series = seriesYear.Length
               New = seriesYear |> List.filter (fun s -> firstYear s = year) |> List.length
               Continued = seriesYear |> List.filter (fun s -> firstYear s < year) |> List.length
               Months = [ for m in 1..12 -> string ((inYear |> List.filter (fun w -> w.At.Month = m) |> List.sumBy _.Ms) / 60_000L) ]
               Streak = streak
               Genres = genres |> List.truncate 5 |> List.map (fun (n, c) -> n, inv (pct c genreTotal), c)
               Studios = counted "studio" |> List.truncate 3 |> List.map (fun (n, c) -> n, inv (pct c seasonsMatched), c)
               Formats =
                watchedSeasons
                |> List.choose (fun (mal, _) -> types.TryFind mal |> Option.filter ((<>) ""))
                |> List.countBy id
                |> List.map (fun (f, c) -> f, int64 c)
                |> ranked
                |> List.map (fun (f, c) -> f, inv (pct c seasonsMatched), c)
               SeasonsMatched = seasonsMatched
               SeasonsTotal = seasonsTotal
               Coverage = inv (pct seasonsMatched seasonsTotal)
               Top = top |}
    }

// ---------------------------------------------------------------------------
// Figures (SC-001, SC-002)
// ---------------------------------------------------------------------------

[<Fact>]
let ``every figure of a year equals an independent count over the same data`` () =
    task {
        let! app, _ = withSample ()
        use app = app

        for year in [ 2022; 2024; 2026 ] do
            let! html = page app year
            let! e = expected app.Db year
            Assert.Empty(elements "year-empty" html)

            Assert.Equal(e.Hours, stat "hours" html)
            Assert.Equal(string e.Episodes, stat "episodes" html)
            Assert.Equal(string e.Series, stat "series" html)

            let split = one "year-series-split" html
            Assert.Equal(string e.New, attr "new" split)
            Assert.Equal(string e.Continued, attr "continued" split)

            let months = elements "month" html |> List.map (attr "minutes")
            Assert.Equal<string list>(e.Months, months)

            let busiest = e.Months |> List.mapi (fun i m -> i + 1, int64 m) |> List.maxBy (fun (i, m) -> m, -i) |> fst
            Assert.Equal(string busiest, attr "busiest" (one "year-months" html))

            match e.Streak with
            | Some(days, from, to') ->
                let s = one "year-streak" html
                Assert.Equal(string days, attr "value" s)
                Assert.Equal(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), attr "from" s)
                Assert.Equal(to'.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), attr "to" s)
            | None -> failwith $"{year} should have a streak"

            let genres =
                elements "genre" html
                |> List.filter (fun m -> attr "genre" m <> "others")
                |> List.map (fun m -> attr "genre" m, attr "share" m, int64 (attr "genre-seasons" m))

            Assert.Equal<(string * string * int64) list>(e.Genres, genres)
            let genreTotal = elements "genre" html |> List.sumBy (fun m -> Decimal.Parse(attr "share" m, CultureInfo.InvariantCulture))
            Assert.InRange(genreTotal, 99.5m, 100.5m)

            let studios = elements "rank" html |> List.map (fun m -> attr "name" m, attr "share" m, int64 (attr "studio-seasons" m))
            Assert.Equal<(string * string * int64) list>(e.Studios, studios)
            Assert.True(studios.Length <= 3)

            let formats = elements "format" html |> List.map (fun m -> attr "format" m, attr "share" m, int64 (attr "format-seasons" m))
            Assert.Equal<(string * string * int64) list>(e.Formats, formats)

            // every taste card says which seasons it is based on
            Assert.Equal(e.Coverage, attr "coverage" (one "year-genres" html))
            Assert.Equal(e.Coverage, attr "coverage" (one "year-studios" html))
            Assert.Equal(e.Coverage, attr "coverage" (one "year-formats" html))

            // (a card with no rows says "not enough" instead, so a year may have fewer than three notes)
            let notes = elements "coverage-seasons" html |> List.map (attr "coverage-seasons")
            Assert.NotEmpty notes
            Assert.All(notes, fun n -> Assert.Equal($"{e.SeasonsMatched}/{e.SeasonsTotal}", n))

            let series, _, fully = e.Top
            let top = one "year-top-series" html
            Assert.Equal(series, attr "series" top)
            Assert.Equal(string fully, attr "episodes" top)

            Assert.Single(elements "rewatch-note" html) |> ignore
    }

[<Fact>]
let ``every year's hours add up to the dashboard's`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! _, dashboard = get app "en" "/u/animegolden"
        let total = Decimal.Parse(stat "hours" dashboard, CultureInfo.InvariantCulture)
        let! years = Db.dump app.Db "SELECT DISTINCT extract(year FROM watched_at AT TIME ZONE 'UTC')::int FROM watch_events"
        let mutable sum = 0m

        for y in years do
            let! html = page app (int y)
            let hours = stat "hours" html

            // a year below the minimum shows no figures; count it from the data instead
            let! e = expected app.Db (int y)
            sum <- sum + Decimal.Parse((if hours = "" then e.Hours else hours), CultureInfo.InvariantCulture)

        Assert.InRange(sum, total - 0.1m * decimal years.Length, total + 0.1m * decimal years.Length)
    }

// ---------------------------------------------------------------------------
// Coverage and states
// ---------------------------------------------------------------------------

[<Fact>]
let ``coverage drops with unmatched seasons; with none matched the MyAnimeList sections say so`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let coverage html = Decimal.Parse(attr "coverage" (one "year-genres" html), CultureInfo.InvariantCulture)
        let! before = page app 2026

        do! Db.execute app.Db "UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, shared_from = NULL WHERE season_id = 'GYE5CQMQ5' OR shared_from = 'GYE5CQMQ5'"
        let! fewer = page app 2026
        Assert.True(coverage fewer <= coverage before, $"{coverage fewer} <= {coverage before}")

        do! Db.execute app.Db "UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, shared_from = NULL"
        let! none = page app 2026

        for hook in [ "year-genres"; "year-studios"; "year-formats" ] do
            Assert.Matches($"<section[^>]*data-{hook}=\"\"", none)

        Assert.Equal(3, Regex.Matches(none, "data-not-enough").Count)
        Assert.Single(elements "year-totals" none) |> ignore
        Assert.Single(elements "year-top-series" none) |> ignore
    }

[<Fact>]
let ``a year with fewer than 15 fully watched episodes shows the empty state with the count`` () =
    task {
        use! app = appWithClock ()
        let! key = createUser app.Db "animegolden"
        let at = DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc)
        let entries n = [ for i in 1..n -> syntheticEntry $"Y{i:D3}" $"YS{i:D3}" (at.AddHours(float i)) true 1400 ]

        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (entries 14) ])
        let! _ = processAll app.Db
        let! fourteen = page app 2025
        Assert.Equal("14", attr "value" (one "year-empty" fourteen))
        Assert.Empty(elements "year-totals" fourteen)
        Assert.Single(elements "year-picker" fourteen) |> ignore
        Assert.Contains("14 of 15 fully watched episodes", fourteen)

        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (entries 15) ])
        let! _ = processAll app.Db
        let! fifteen = page app 2025
        Assert.Empty(elements "year-empty" fifteen)
        Assert.Single(elements "year-totals" fifteen) |> ignore
    }

[<Theory>]
[<InlineData("/u/nobody/year/2025", "User not found")>]
[<InlineData("/u/nobody/year", "User not found")>]
[<InlineData("/u/animegolden/year/abc", "Page not found")>]
[<InlineData("/u/animegolden/year/1999", "Page not found")>]
[<InlineData("/u/animegolden/year/2027", "Page not found")>]
let ``bad users and years are not found`` (path: string, text: string) =
    task {
        use! app = appWithClock ()
        let! _ = createUser app.Db "animegolden"
        let! status, html = get app "en" path
        Assert.Equal(HttpStatusCode.NotFound, status)
        Assert.Contains(text, html)
    }

[<Fact>]
let ``the page is localized; month names follow the language and MyAnimeList names don't`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! _, en = get app "en" "/u/animegolden/year/2026"
        let! _, es = get app "es-ES,es;q=0.9" "/u/animegolden/year/2026"
        Assert.Contains("lang=\"es\"", es)
        Assert.Contains("Racha más larga", es)
        Assert.Contains("Longest streak", en)
        Assert.Contains("Ene", es)
        Assert.Contains("Jan", en)

        let genres html = elements "genre" html |> List.map (attr "genre")
        Assert.Equal<string list>(genres en, genres es)
    }

// ---------------------------------------------------------------------------
// Stability (FR-014)
// ---------------------------------------------------------------------------

[<Fact>]
let ``the figures are identical after a rebuild and after re-sending, and viewing never calls MyAnimeList`` () =
    task {
        let! app, key = withSample ()
        use app = app
        let! before = page app 2022

        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! resent = page app 2022
        Assert.Equal<string list>(signature before, signature resent)

        let! queued = RebuildUser.rebuildUser app.Db "animegolden"
        Assert.True(queued.IsSome)
        let! _ = processAll app.Db
        app.Mal.Clear()
        let! rebuilt = page app 2022
        Assert.Equal<string list>(signature before, signature rebuilt)
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``months that haven't happened yet are marked only in the current year`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        // the clock is 9 October 2026: November and December are still ahead
        let! current = page app 2026
        let future = elements "month" current |> List.filter (fun m -> m.ContainsKey "future") |> List.map (attr "month")
        Assert.Equal<string list>([ "11"; "12" ], future)

        let! past = page app 2024
        Assert.Empty(elements "month" past |> List.filter (fun m -> m.ContainsKey "future"))
    }
