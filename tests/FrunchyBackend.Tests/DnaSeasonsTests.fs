module FrunchyBackend.Tests.DnaSeasonsTests

open System
open System.Globalization
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Handlers.Users
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 027: taste figures count watched seasons, the flagship skips long runners, the long-run card and
// "Your seasons" (specs/027-seasons/contracts/hooks.md). Every world is built from synthetic pages with air dates
// and MyAnimeList rows inserted directly, and the page is read through its data-* hooks only.

type private Ep =
    { Number: int
      Aired: DateTime option
      Watched: DateTime
      Full: bool }

type private Show =
    { Series: string
      /// The Crunchyroll season id (a dub is a season of its own)
      Season: string
      Mal: int
      /// The original season a dub shares its episodes with
      SharedFrom: string option
      Studio: string
      Genre: string
      Type: string
      MalYear: int
      MalEpisodes: int
      Eps: Ep list }

let private day (y: int) (m: int) (d: int) = DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc)

/// A show with `count` episodes, all fully watched, aired weekly from `first`, each watched `lag` days after airing
let private weekly (series: string) (mal: int) (studio: string) (first: DateTime) (count: int) (lag: int) =
    { Series = series
      Season = series + "S1"
      Mal = mal
      SharedFrom = None
      Studio = studio
      Genre = "Action"
      Type = "TV"
      MalYear = first.Year
      MalEpisodes = 12
      Eps =
        [ for i in 1..count ->
              let aired = first.AddDays(7.0 * float (i - 1))

              { Number = i
                Aired = Some aired
                Watched = aired.AddDays(float lag)
                Full = true } ] }

let private withUndated (show: Show) =
    { show with Eps = show.Eps |> List.map (fun e -> { e with Aired = None }) }

let private entriesOf (show: Show) =
    [ for e in show.Eps ->
          datedEntry $"{show.Season}E{e.Number:D4}" show.Series show.Season e.Number e.Aired e.Watched e.Full ]

/// Import the shows, then give their seasons MyAnimeList entries (studio, genre, type, year, episodes) and matches
let private build (shows: Show list) : Task<TestApp * string> =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let entries = shows |> List.collect entriesOf

        // pages of 100 entries, as the extension would send them
        let pages = entries |> List.chunkBySize 100 |> List.map (fun chunk -> historyUrl RealAccountId, pageOf chunk)
        let! _ = submitAccepted app key (envelopeOf pages)
        let! _ = processAll app.Db

        let names = shows |> List.collect (fun s -> [ "studio", s.Studio; "genre", s.Genre ]) |> List.distinct
        let tagId (kind: string, name: string) = 1 + (names |> List.findIndex ((=) (kind, name)))

        for kind, name in names do
            do! Db.execute app.Db $"INSERT INTO mal_tags (kind, tag_id, name) VALUES ('{kind}', {tagId (kind, name)}, '{name}')"

        for show in shows |> List.distinctBy _.Mal do
            do!
                Db.execute
                    app.Db
                    $"""INSERT INTO mal_anime (mal_id, url, title, type, episodes, year, raw, data_hash, fetched_at, checked_at)
                        VALUES ({show.Mal}, 'https://myanimelist.net/anime/{show.Mal}', 'Anime {show.Mal}', '{show.Type}',
                                {show.MalEpisodes}, {show.MalYear}, '{{}}'::json, 'h', now(), now());
                        INSERT INTO mal_anime_tags (mal_id, kind, tag_id, position) VALUES
                            ({show.Mal}, 'studio', {tagId ("studio", show.Studio)}, 0),
                            ({show.Mal}, 'genre', {tagId ("genre", show.Genre)}, 0)"""

        // originals first: a dub's row references its original season
        for show in shows |> List.sortBy (fun s -> s.SharedFrom.IsSome) do
            match show.SharedFrom with
            | Some original ->
                do!
                    Db.execute
                        app.Db
                        $"INSERT INTO cr_season_matches (season_id, status, mal_id, method, shared_from) VALUES ('{show.Season}', 'matched', {show.Mal}, 'dub', '{original}')"
            | None ->
                do!
                    Db.execute
                        app.Db
                        $"INSERT INTO cr_season_matches (season_id, status, mal_id, method) VALUES ('{show.Season}', 'matched', {show.Mal}, 'search')"

        return app, key
    }

let private get (app: TestApp) (language: string) =
    task {
        let! response = getWithLanguage app.Client language "/u/animegolden/dna"
        return! readHtml response
    }

let private dna (app: TestApp) = get app "en"

/// Every hook the page renders, to compare two renderings
let private hooks (html: string) =
    [ for m in Regex.Matches(html, "data-(?!address)[a-z-]+=\"[^\"]*\"") -> m.Value ]

// ---------------------------------------------------------------------------
// Reading hooks. Exact names only: `data-season` is not `data-season-shows`.
// ---------------------------------------------------------------------------

/// The attributes of every element carrying exactly `data-{hook}`, in document order, with its position in the page
let private tagsWith (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\sdata-{hook}(=\"[^\"]*\")?(?=[\\s>/])[^>]*>") ->
          m.Index,
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private elements hook html = tagsWith hook html |> List.map snd
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""
let private has hook html = not (tagsWith hook html).IsEmpty

/// The listed seasons: (key, shows as (series, episodes, while airing)), in page order
let private listedSeasons (html: string) =
    let seasons = tagsWith "season" html
    let ends = (seasons |> List.map fst |> List.skip 1) @ [ html.Length ]

    [ for (start, m), stop in List.zip seasons ends do
          let block = html.Substring(start, stop - start)

          let shows =
              [ for s in elements "season-show" block -> attr "season-show" s, attr "show-episodes" s, s.ContainsKey "while-airing" ]

          attr "season" m, attr "season-shows" m, shows ]

// ---------------------------------------------------------------------------
// User story 1: taste figures aren't swallowed by a long show
// ---------------------------------------------------------------------------

/// A long runner (1,100 episodes on MyAnimeList, started in 1999, studio A) watched through 12 airing seasons of
/// 22 episodes, and 20 short shows (studio B) of one season each; the first of them has one episode more
let private longAndShort () =
    let longRunner =
        { Series = "LONGRUN"
          Season = "LONGRUNS1"
          Mal = 100
          SharedFrom = None
          Studio = "Studio A"
          Genre = "Adventure"
          Type = "TV"
          MalYear = 1999
          MalEpisodes = 1100
          Eps =
            [ for q in 0..11 do
                  for i in 1..22 ->
                      // 22 episodes in each quarter from Winter 2021: one every 4 days
                      let aired = DateTime(2021 + q / 4, (q % 4) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(4.0 * float (i - 1))

                      { Number = q * 22 + i
                        Aired = Some aired
                        Watched = aired.AddDays 40.0
                        Full = true } ] }

    let shorts =
        [ for n in 1..20 ->
              let id = $"SHORT{n:D2}"

              { weekly id (200 + n) "Studio B" (day 2024 1 10) (if n = 1 then 4 else 3) 30 with
                  Genre = "Comedy"
                  MalYear = 2023 } ]

    longRunner :: shorts

[<Fact>]
let ``a long show doesn't decide the studio: its seasons weigh one each`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! html = dna app

        // 12 + 20 watched seasons; studio B has 20 of 32
        let studios = elements "rank" html
        Assert.Equal("Studio B", attr "name" studios[0])
        Assert.Equal("20", attr "studio-seasons" studios[0])
        Assert.Equal("62.5", attr "share" studios[0])
        Assert.Equal("Studio A", attr "name" studios[1])
        Assert.Equal("12", attr "studio-seasons" studios[1])
        Assert.Equal("37.5", attr "share" studios[1])

        // every card says it is based on all 32 (the long show's 12 airing seasons and the 20 short ones)
        let notes = elements "coverage-seasons" html |> List.map (attr "coverage-seasons")
        Assert.Equal<string list>(List.replicate 4 "32/32", notes)
        Assert.Equal("100.0", attr "coverage" (List.exactlyOne (elements "dna-studio" html)))

        // genres count seasons too: Comedy 20, Adventure 12
        let genres = elements "genre" html
        Assert.Equal<(string * string) list>([ "Comedy", "20"; "Adventure", "12" ], genres |> List.map (fun g -> attr "genre" g, attr "genre-seasons" g))
        Assert.Equal("SHORT01", attr "top-series" genres[0])
        Assert.Equal("LONGRUN", attr "top-series" genres[1])
    }

[<Fact>]
let ``a decade is the airing year of the watched season, not the year the show started`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! html = dna app

        // the long runner started in 1999, but all 12 of its watched seasons aired in 2021-2023
        let decades = elements "decade" html |> List.map (fun d -> attr "decade" d, attr "decade-seasons" d)
        Assert.Equal<(string * string) list>([ "2020", "32" ], decades)
    }

[<Fact>]
let ``hours and totals still follow the minutes watched`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! html = dna app
        let! response = getWithLanguage app.Client "en" "/u/animegolden"
        let! dashboard = readHtml response

        for name in [ "hours"; "days"; "episodes" ] do
            let onDna = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value
            let onDashboard = Regex.Match(dashboard, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value
            Assert.NotEqual<string>("", onDna)
            Assert.Equal(onDashboard, onDna)

        // 264 long episodes + 61 short ones
        Assert.Equal("325", Regex.Match(html, "data-stat=\"episodes\"[^>]*data-value=\"([^\"]+)\"").Groups[1].Value)
    }

[<Fact>]
let ``a season whose episodes have no air date counts once, its decade from the MyAnimeList year`` () =
    task {
        let undated = { withUndated (weekly "OLDIE" 300 "Studio C" (day 2010 1 1) 20 3) with MalYear = 2011 }
        let! app, _ = build [ undated; weekly "NEWER" 301 "Studio B" (day 2024 1 10) 3 1 ]
        use app = app
        let! html = dna app

        let decades = elements "decade" html |> List.map (fun d -> attr "decade" d, attr "decade-seasons" d)
        Assert.Equal<(string * string) list>([ "2010", "1"; "2020", "1" ], decades |> List.sortBy fst)
        Assert.Equal<string list>(List.replicate 4 "2/2", elements "coverage-seasons" html |> List.map (attr "coverage-seasons"))

        // the undated show is in no airing season, so only the dated one is listed
        let seasons = listedSeasons html
        Assert.Equal<string list>([ "winter-2024" ], seasons |> List.map (fun (k, _, _) -> k))
    }

/// A show with 3 episodes that have an air date (one airing season) and 2 that don't
let private mixedDates (series: string) (mal: int) (studio: string) =
    let dated = weekly series mal studio (day 2025 1 8) 3 1

    let undated =
        [ for i in 4..5 ->
              { Number = i
                Aired = None
                Watched = day 2025 2 12
                Full = true } ]

    { dated with Eps = dated.Eps @ undated }

[<Fact>]
let ``an undated episode adds no season when its anime has dated ones`` () =
    task {
        // 3 dated + 2 undated episodes of one anime are one watched season, not two
        let! app, _ = build [ mixedDates "MIXED" 980 "Studio A"; weekly "FILLER" 981 "Studio B" (day 2025 4 5) 12 1 ]
        use app = app
        let! html = dna app

        let studios = elements "rank" html |> List.map (fun r -> attr "name" r, attr "studio-seasons" r)
        Assert.Equal<(string * string) list>([ "Studio A", "1"; "Studio B", "1" ], studios |> List.sortBy fst)
        Assert.Equal<string list>(List.replicate 4 "2/2", elements "coverage-seasons" html |> List.map (attr "coverage-seasons"))

        // the airing seasons list the dated episodes only
        Assert.Equal<string list>([ "spring-2025"; "winter-2025" ], listedSeasons html |> List.map (fun (k, _, _) -> k))
    }

[<Fact>]
let ``an anime with only undated episodes is still one watched season`` () =
    task {
        let undated = withUndated (weekly "ALLUNDATED" 982 "Studio A" (day 2025 1 8) 5 1)
        let! app, _ = build [ undated; weekly "FILLER" 983 "Studio B" (day 2025 4 5) 12 1 ]
        use app = app
        let! html = dna app

        let studios = elements "rank" html |> List.map (fun r -> attr "name" r, attr "studio-seasons" r)
        Assert.Equal<(string * string) list>([ "Studio A", "1"; "Studio B", "1" ], studios |> List.sortBy fst)
        Assert.Equal<string list>(List.replicate 4 "2/2", elements "coverage-seasons" html |> List.map (attr "coverage-seasons"))
    }

[<Fact>]
let ``the year page follows the same rule within the year's rows`` () =
    task {
        let! app, _ = build [ mixedDates "MIXED" 984 "Studio A"; weekly "FILLER" 985 "Studio B" (day 2025 4 5) 12 1 ]
        use app = app
        let! response = getWithLanguage app.Client "en" "/u/animegolden/year/2025"
        let! html = readHtml response

        let studios = elements "rank" html |> List.map (fun r -> attr "name" r, attr "studio-seasons" r)
        Assert.Equal<(string * string) list>([ "Studio A", "1"; "Studio B", "1" ], studios |> List.sortBy fst)
        let notes = elements "coverage-seasons" html |> List.map (attr "coverage-seasons")
        Assert.NotEmpty notes
        Assert.All(notes, fun n -> Assert.Equal("2/2", n))
    }

[<Fact>]
let ``a dub and its original are one watched season`` () =
    task {
        let original = weekly "DUBBED" 400 "Studio B" (day 2025 4 5) 12 2
        // the dub aired later and shares the original's episode numbers
        let dub =
            { original with
                Season = "DUBBEDS1DUB"
                SharedFrom = Some "DUBBEDS1"
                Eps = original.Eps |> List.map (fun e -> { e with Aired = e.Aired |> Option.map (fun d -> d.AddDays 20.0); Watched = e.Watched.AddDays 25.0 }) }

        let! app, _ = build [ original; dub ]
        use app = app
        let! html = dna app

        // the group aired when the original did (5 April to 21 June: spring 2025), so one watched season, not two
        Assert.Equal<string list>(List.replicate 4 "1/1", elements "coverage-seasons" html |> List.map (attr "coverage-seasons"))
        Assert.Equal("1", attr "studio-seasons" (List.head (elements "rank" html)))

        // and 12 episodes, not 24
        let seasons = listedSeasons html
        Assert.Equal<(string * string) list>([ "spring-2025", "1" ], seasons |> List.map (fun (k, n, _) -> k, n))
        let _, _, shows = seasons.Head
        Assert.Equal<(string * string) list>([ "DUBBED", "12" ], shows |> List.map (fun (s, n, _) -> s, n))
    }

// ---------------------------------------------------------------------------
// User story 2: the flagship and the long run
// ---------------------------------------------------------------------------

[<Fact>]
let ``the flagship is the top regular show and the long run is its own card`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! html = dna app

        // One Piece-like: most minutes, but a long runner
        let insignia = (elements "dna-insignia" html) |> List.exactlyOne
        Assert.Equal("SHORT01", attr "insignia-series" insignia)
        Assert.Equal("SHORT01", attr "series" insignia)

        let longRun = (elements "dna-long-run" html) |> List.exactlyOne
        Assert.Equal("LONGRUN", attr "dna-long-run" longRun)
        Assert.Equal("264", attr "long-run-episodes" (List.exactlyOne (elements "long-run-episodes" html)))
        Assert.Equal("winter-2021/fall-2023", attr "long-run-span" (List.exactlyOne (elements "long-run-span" html)))

        // its title links to the user's anime page like every series on the page
        Assert.Contains("<a href=\"/u/animegolden/anime/LONGRUN\" class=\"fr-link\" data-series-link=\"LONGRUN\">", html)
    }

[<Fact>]
let ``a long runner watched less than 24 episodes has no card, and is still not the flagship`` () =
    task {
        let marathon = { weekly "MARATHON" 500 "Studio A" (day 2025 1 1) 23 1 with MalEpisodes = 500 }
        let! app, _ = build [ marathon; weekly "SMALLER" 501 "Studio B" (day 2025 1 1) 15 1 ]
        use app = app
        let! html = dna app

        Assert.False(has "dna-long-run" html)
        Assert.Equal("SMALLER", attr "insignia-series" (List.exactlyOne (elements "dna-insignia" html)))
    }

[<Fact>]
let ``a long runner is found by an episode numbered above 100 even without a MyAnimeList entry`` () =
    task {
        let! app = newApp ()
        use app = app
        let! key = createUser app.Db "animegolden"

        let entries =
            [ for i in 1..30 -> datedEntry $"UNM{i:D4}" "UNMATCHED" "UNMATCHEDS1" (100 + i) (Some(day 2026 1 1)) (day 2026 1 8) true ]
            @ [ for i in 1..16 -> datedEntry $"REG{i:D4}" "REGULAR" "REGULARS1" i (Some(day 2026 4 1)) (day 2026 4 2) true ]

        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf entries ])
        let! _ = processAll app.Db
        let! html = dna app

        Assert.Equal("UNMATCHED", attr "dna-long-run" (List.exactlyOne (elements "dna-long-run" html)))
        Assert.Equal("REGULAR", attr "insignia-series" (List.exactlyOne (elements "dna-insignia" html)))
        // no MyAnimeList entry anywhere: no taste figures, but the seasons section needs only the air dates
        Assert.Empty(elements "rank" html)
        Assert.True(has "dna-seasons" html)
    }

[<Fact>]
let ``only long runners: no flagship, the long-run card shows`` () =
    task {
        let a = { weekly "ENDLESSA" 600 "Studio A" (day 2025 1 1) 30 1 with MalEpisodes = 900 }
        let b = { weekly "ENDLESSB" 601 "Studio A" (day 2025 1 1) 26 1 with MalEpisodes = 900 }
        let! app, _ = build [ a; b ]
        use app = app
        let! html = dna app

        Assert.False(has "dna-insignia" html)
        Assert.Equal("ENDLESSA", attr "dna-long-run" (List.exactlyOne (elements "dna-long-run" html)))
    }

[<Fact>]
let ``no long runner: the flagship is as before and there is no long-run card`` () =
    task {
        let! app, _ = build [ weekly "SHOWA" 700 "Studio A" (day 2025 1 1) 12 1; weekly "SHOWB" 701 "Studio B" (day 2025 4 1) 10 1 ]
        use app = app
        let! html = dna app

        Assert.Equal("SHOWA", attr "insignia-series" (List.exactlyOne (elements "dna-insignia" html)))
        Assert.False(has "dna-long-run" html)
    }

// ---------------------------------------------------------------------------
// User story 3: your seasons
// ---------------------------------------------------------------------------

[<Fact>]
let ``the seasons are listed newest first with their shows`` () =
    task {
        let fall25 = weekly "FALLTWENTYFIVE" 800 "Studio A" (day 2025 10 5) 6 1
        let winter26 = weekly "WINTERSHOW" 801 "Studio B" (day 2026 1 8) 8 1
        let winter26b = weekly "WINTERSHOWTWO" 802 "Studio B" (day 2026 1 9) 3 1
        let fall26 = weekly "FALLTWENTYSIX" 803 "Studio A" (day 2026 10 4) 2 1
        let! app, _ = build [ fall25; winter26; winter26b; fall26 ]
        use app = app
        let! html = dna app

        Assert.Equal("3", attr "dna-seasons" (List.exactlyOne (elements "dna-seasons" html)))
        let seasons = listedSeasons html
        Assert.Equal<string list>([ "fall-2026"; "winter-2026"; "fall-2025" ], seasons |> List.map (fun (k, _, _) -> k))

        let _, winterCount, winterShows = seasons[1]
        Assert.Equal("2", winterCount)
        // the shows of a season: most episodes first
        Assert.Equal<(string * string) list>([ "WINTERSHOW", "8"; "WINTERSHOWTWO", "3" ], winterShows |> List.map (fun (s, n, _) -> s, n))

        // the season with the most shows is the top one (winter 2026: two)
        Assert.Equal("winter-2026", attr "top-season" (List.exactlyOne (elements "top-season" html)))
        Assert.False(has "earlier-seasons" html)
    }

[<Fact>]
let ``a show is watched while airing within 7 days of an episode's air date, not 30 days later`` () =
    task {
        let quick = weekly "QUICKWATCH" 810 "Studio A" (day 2026 1 8) 8 3
        let slow = weekly "SLOWWATCH" 811 "Studio B" (day 2026 1 8) 8 30
        let! app, _ = build [ quick; slow ]
        use app = app
        let! html = dna app

        let _, _, shows = (listedSeasons html) |> List.exactlyOne
        let airing = shows |> List.map (fun (s, _, w) -> s, w) |> Map.ofList
        Assert.True airing["QUICKWATCH"]
        Assert.False airing["SLOWWATCH"]

        // the hook is only present on the show that has it (the top block repeats the season's tiles, so within the row)
        Assert.Equal(1, shows |> List.filter (fun (_, _, w) -> w) |> List.length)
    }

[<Fact>]
let ``two seasons with as many shows: the newer is the top season`` () =
    task {
        let! app, _ = build [ weekly "OLDERA" 820 "Studio A" (day 2025 4 5) 4 1; weekly "OLDERB" 821 "Studio A" (day 2025 4 6) 4 1; weekly "NEWERA" 822 "Studio B" (day 2025 10 5) 5 1; weekly "NEWERB" 823 "Studio B" (day 2025 10 6) 5 1 ]
        use app = app
        let! html = dna app

        Assert.Equal("fall-2025", attr "top-season" (List.exactlyOne (elements "top-season" html)))
    }

[<Fact>]
let ``eleven airing seasons list the newest eight and count the rest`` () =
    task {
        // one show per quarter, from Winter 2023 to Summer 2025: 11 seasons
        let shows =
            [ for q in 0..10 ->
                  let aired = DateTime(2023 + q / 4, (q % 4) * 3 + 2, 10, 0, 0, 0, DateTimeKind.Utc)
                  weekly $"SEASONAL{q:D2}" (900 + q) "Studio A" aired 2 1 ]

        let! app, _ = build shows
        use app = app
        let! html = dna app

        Assert.Equal("11", attr "dna-seasons" (List.exactlyOne (elements "dna-seasons" html)))
        let seasons = listedSeasons html
        Assert.Equal(8, seasons.Length)
        Assert.Equal("summer-2025", seasons.Head |> fun (k, _, _) -> k)
        Assert.Equal("fall-2023", seasons |> List.last |> fun (k, _, _) -> k)
        Assert.Equal("3", attr "earlier-seasons" (List.exactlyOne (elements "earlier-seasons" html)))
    }

[<Fact>]
let ``no air date anywhere: no seasons section`` () =
    task {
        let! app, _ = build [ withUndated (weekly "NODATES" 950 "Studio A" (day 2025 1 1) 20 1) ]
        use app = app
        let! html = dna app

        Assert.False(has "dna-seasons" html)
        Assert.False(has "top-season" html)
        // the taste cards still show, counting the undated season once
        Assert.Equal("1", attr "studio-seasons" (List.head (elements "rank" html)))
    }

[<Fact>]
let ``a part-watched episode is no watched season`` () =
    task {
        let partial = { weekly "PARTIAL" 960 "Studio A" (day 2025 7 5) 3 1 with Eps = [ for i in 1..3 -> { Number = i; Aired = Some(day 2025 7 5); Watched = day 2025 7 6; Full = false } ] }
        let! app, _ = build [ partial; weekly "FULLA" 961 "Studio B" (day 2025 1 5) 8 1; weekly "FULLB" 962 "Studio B" (day 2025 1 6) 8 1 ]
        use app = app
        let! html = dna app

        // only the fully watched show makes a season; the partly watched one doesn't appear in any figure but the minutes
        Assert.Equal<string list>([ "winter-2025" ], listedSeasons html |> List.map (fun (k, _, _) -> k))
        Assert.Equal<string list>(List.replicate 4 "2/2", elements "coverage-seasons" html |> List.map (attr "coverage-seasons"))
    }

// ---------------------------------------------------------------------------
// Language, stability, empty states
// ---------------------------------------------------------------------------

[<Fact>]
let ``the new sections speak Spanish and keep the same hooks`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! en = get app "en"
        let! es = get app "es-ES,es;q=0.9"

        Assert.Contains("Your seasons", en)
        Assert.Contains("Long run", en)
        Assert.Contains("Fall 2023", en)
        Assert.Contains("Tus temporadas", es)
        Assert.Contains("Larga duración", es)
        Assert.Contains("Otoño 2023", es)
        Assert.DoesNotContain("Your seasons", es)

        // the hooks of this feature are the same in both languages (the rest of the page has its own tests)
        let ours (html: string) =
            hooks html |> List.filter (fun h -> Regex.IsMatch(h, "season|long-run|insignia"))

        Assert.NotEmpty(ours en)
        Assert.Equal<string list>(ours en, ours es)
    }

[<Fact>]
let ``the figures are identical after a rebuild and after re-sending the history`` () =
    task {
        let! app, key = build (longAndShort ())
        use app = app
        let! before = dna app

        let entries = longAndShort () |> List.collect entriesOf
        let pages = entries |> List.chunkBySize 100 |> List.map (fun chunk -> historyUrl RealAccountId, pageOf chunk)
        let! _ = submitAccepted app key (envelopeOf pages)
        let! _ = processAll app.Db
        let! resent = dna app
        Assert.Equal<string list>(hooks before, hooks resent)

        let! queued = RebuildUser.rebuildUser app.Db "animegolden"
        Assert.True queued.IsSome
        let! _ = processAll app.Db
        let! rebuilt = dna app
        Assert.Equal<string list>(hooks before, hooks rebuilt)
    }

[<Fact>]
let ``below 15 fully watched episodes the empty state hides every new section`` () =
    task {
        let! app, _ = build [ weekly "TINY" 970 "Studio A" (day 2025 1 5) 5 1 ]
        use app = app
        let! html = dna app

        Assert.True(has "dna-empty" html)
        Assert.False(has "dna-seasons" html)
        Assert.False(has "dna-long-run" html)
    }

[<Fact>]
let ``foldSeasons ranks by shows, then newer, lists eight and counts the rest`` () =
    let show id : Dna.SeasonShow =
        { SeriesId = id
          Title = id
          Poster = None
          Episodes = 1L
          WhileAiring = false }

    Assert.Equal(None, Dna.foldSeasons [])

    // numbers are year * 4 + quarter: 2025 spring = 8101, summer = 8102
    let rows = [ 8101, show "A"; 8101, show "B"; 8102, show "C"; 8102, show "D"; 8099, show "E" ]
    let section = (Dna.foldSeasons rows).Value
    Assert.Equal(3, section.Total)
    Assert.Equal(("summer", 2025), (section.Top.Name, section.Top.Year))
    Assert.Equal<(string * int) list>([ "summer", 2025; "spring", 2025; "fall", 2024 ], section.Listed |> List.map (fun s -> s.Name, s.Year))
    Assert.Equal(0, section.Earlier)

// ---------------------------------------------------------------------------
// The design review: a collapsed timeline, the top block, airing marks (specs/027-seasons/design-direction.md).
// Hooks and attributes only, never classes.
// ---------------------------------------------------------------------------

/// Whether each listed season's <details> carries the `open` attribute: (key, open), in page order
let private seasonRowsOpen (html: string) =
    [ for m in Regex.Matches(html, "<details\\b[^>]*\\sdata-season=\"([^\"]*)\"[^>]*>") ->
          m.Groups[1].Value, Regex.IsMatch(m.Value, "\\sopen(=\"[^\"]*\")?(?=[\\s>/])") ]

let private innerText (html: string) =
    Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), "\\s+", " ").Trim()

/// The text of a season row's <summary>, without its heading and the chevron icon: (key, text)
let private seasonSummaries (html: string) =
    [ for m in Regex.Matches(html, "<details\\b[^>]*\\sdata-season=\"([^\"]*)\"[^>]*>\\s*<summary\\b[^>]*>(.*?)</summary>", RegexOptions.Singleline) ->
          let withoutHeading = Regex.Replace(m.Groups[2].Value, "<h3\\b.*?</h3>", "", RegexOptions.Singleline)
          let withoutIcon = Regex.Replace(withoutHeading, "<span[^>]*material-symbols-outlined[^>]*>[^<]*</span>", "")
          m.Groups[1].Value, innerText withoutIcon ]

[<Fact>]
let ``the newest season row starts open and the others closed, every show inside`` () =
    task {
        let! app, _ =
            build [ weekly "FALLTWENTYFIVE" 800 "Studio A" (day 2025 10 5) 6 1
                    weekly "WINTERSHOW" 801 "Studio B" (day 2026 1 8) 8 1
                    weekly "WINTERSHOWTWO" 802 "Studio B" (day 2026 1 9) 3 1
                    weekly "FALLTWENTYSIX" 803 "Studio A" (day 2026 10 4) 2 1 ]

        use app = app
        let! html = dna app

        Assert.Equal<(string * bool) list>([ "fall-2026", true; "winter-2026", false; "fall-2025", false ], seasonRowsOpen html)

        // closed rows still hold every show: nothing leaves the page, it only collapses
        let _, _, winterShows = (listedSeasons html)[1]
        Assert.Equal<string list>([ "WINTERSHOW"; "WINTERSHOWTWO" ], winterShows |> List.map (fun (s, _, _) -> s))
    }

[<Fact>]
let ``the top season shows six tiles, then the rest behind a Show all details`` () =
    task {
        // eight shows aired in Winter 2026 (the top season), one in Fall 2025
        let winter = [ for i in 1..8 -> weekly $"TOPSHOW{i}" (1000 + i) "Studio A" (day 2026 1 8) 2 1 ]
        let! app, _ = build (weekly "OLDSHOW" 1100 "Studio B" (day 2025 10 5) 2 1 :: winter)
        use app = app
        let! html = dna app

        let topStart = fst (List.exactlyOne (tagsWith "top-season" html))
        let seasonStart = tagsWith "season" html |> List.head |> fst
        let region = html.Substring(topStart, seasonStart - topStart)

        // the first six sit before the details, the other two inside it
        let detailsStart = region.IndexOf "<details"
        Assert.True(detailsStart > 0)
        let before = region.Substring(0, detailsStart)
        let inside = region.Substring detailsStart
        Assert.Equal(6, (tagsWith "season-show" before).Length)
        Assert.Equal(2, (tagsWith "season-show" inside).Length)

        Assert.Equal<string list>(
            [ "TOPSHOW1"; "TOPSHOW2"; "TOPSHOW3"; "TOPSHOW4"; "TOPSHOW5"; "TOPSHOW6" ],
            elements "season-show" before |> List.map (attr "season-show")
        )

        // closed by default (no `open`), and its summary names the whole count
        Assert.DoesNotMatch("^<details[^>]*\\sopen", inside)
        Assert.Contains("Show all 8 shows", innerText (Regex.Match(inside, "<summary.*?</summary>", RegexOptions.Singleline).Value))

        let! es = get app "es-ES,es;q=0.9"
        Assert.Contains("Ver las 8 series", es)
    }

[<Fact>]
let ``a top season of six shows or fewer has no Show all details`` () =
    task {
        let! app, _ = build [ for i in 1..6 -> weekly $"SIXSHOW{i}" (1200 + i) "Studio A" (day 2026 1 8) 3 1 ]
        use app = app
        let! html = dna app

        let topStart = fst (List.exactlyOne (tagsWith "top-season" html))
        let seasonStart = tagsWith "season" html |> List.head |> fst
        let region = html.Substring(topStart, seasonStart - topStart)
        Assert.DoesNotContain("<details", region)
        Assert.Equal(6, (tagsWith "season-show" region).Length)
    }

/// Winter 2026: one show watched within a day of airing, one a month later; Fall 2025: one watched late
let private airingWorld () =
    [ weekly "QUICKWATCH" 810 "Studio A" (day 2026 1 8) 3 1
      weekly "SLOWWATCH" 811 "Studio B" (day 2026 1 8) 3 30
      weekly "OLDSLOW" 812 "Studio B" (day 2025 10 5) 10 30 ]

[<Fact>]
let ``a season's summary counts its shows and those watched while airing, in both languages`` () =
    task {
        let! app, _ = build (airingWorld ())
        use app = app
        let! en = get app "en"
        let! es = get app "es-ES,es;q=0.9"

        Assert.Equal<(string * string) list>([ "winter-2026", "2 shows · 1 while airing"; "fall-2025", "1 show" ], seasonSummaries en)
        Assert.Equal<(string * string) list>([ "winter-2026", "2 series · 1 en emisión"; "fall-2025", "1 serie" ], seasonSummaries es)

        // the top season (two shows against one) says the same in its own line
        Assert.Contains("Your busiest season: 2 shows, 1 of them watched while airing.", en)
        Assert.Contains("Tu temporada con más series: 2 series, 1 de ellas vistas en emisión.", es)
    }

[<Fact>]
let ``a top season with nothing watched while airing drops the clause`` () =
    task {
        let! app, _ = build [ weekly "SLOWONE" 820 "Studio A" (day 2026 1 8) 8 30; weekly "SLOWTWO" 821 "Studio B" (day 2026 1 8) 8 30 ]
        use app = app
        let! en = get app "en"

        Assert.Contains("Your busiest season: 2 shows.", en)
        Assert.DoesNotContain("while airing", en)
    }

[<Fact>]
let ``the earlier line says one season or many, in both languages`` () =
    task {
        let seasonal count =
            [ for q in 0 .. count - 1 ->
                  let aired = DateTime(2023 + q / 4, (q % 4) * 3 + 2, 10, 0, 0, 0, DateTimeKind.Utc)
                  weekly $"EARLIER{q:D2}" (1300 + q) "Studio A" aired 2 1 ]

        let earlier (html: string) =
            let g = Regex.Match(html, "data-earlier-seasons=\"(\\d+)\"[^>]*>([^<]*)<").Groups
            g[1].Value, g[2].Value

        // nine seasons: eight listed, one earlier
        let! one, _ = build (seasonal 9)
        use one = one
        let! en1 = get one "en"
        let! es1 = get one "es-ES,es;q=0.9"
        Assert.Equal(("1", "Plus 1 earlier season."), earlier en1)
        Assert.Equal(("1", "Y 1 temporada anterior."), earlier es1)

        // eleven: three earlier
        let! many, _ = build (seasonal 11)
        use many = many
        let! en3 = get many "en"
        let! es3 = get many "es-ES,es;q=0.9"
        Assert.Equal(("3", "Plus 3 earlier seasons."), earlier en3)
        Assert.Equal(("3", "Y 3 temporadas anteriores."), earlier es3)
    }

[<Fact>]
let ``the hidden airing text sits only on shows that have the airing hook`` () =
    task {
        let! app, _ = build (airingWorld ())
        use app = app
        let! en = get app "en"
        let! es = get app "es-ES,es;q=0.9"

        for html, text in [ en, "Watched while airing"; es, "Visto en emisión" ] do
            let tiles =
                [ for m in Regex.Matches(html, "<li\\b[^>]*\\sdata-season-show=\"([^\"]*)\"[^>]*>.*?</li>", RegexOptions.Singleline) ->
                      let hidden = Regex.Match(m.Value, "<span[^>]*\\bclass=\"sr-only\"[^>]*>([^<]*)</span>")
                      m.Groups[1].Value, m.Value.Contains "data-while-airing", hidden.Success && hidden.Groups[1].Value = text ]

            Assert.NotEmpty tiles

            for series, airing, hasText in tiles do
                Assert.True((airing = hasText), $"{series}: while-airing hook {airing}, hidden text {hasText}")

            // QUICKWATCH is in the top block and in its row; both carry the mark, nothing else does
            Assert.Equal<string list>([ "QUICKWATCH"; "QUICKWATCH" ], tiles |> List.filter (fun (_, a, _) -> a) |> List.map (fun (s, _, _) -> s))

            // the text exists nowhere else on the page (no pill, no legend)
            Assert.Equal(2, Regex.Matches(html, Regex.Escape text).Count)
    }

[<Fact>]
let ``foldSeasons counts the shows watched while airing and breaks episode ties by title, then id`` () =
    let show id title eps airing : Dna.SeasonShow =
        { SeriesId = id
          Title = title
          Poster = None
          Episodes = eps
          WhileAiring = airing }

    let rows =
        [ 8101, show "ID3" "Beta" 4L true
          8101, show "ID2" "Alpha" 4L false
          8101, show "ID1" "Alpha" 4L true
          8101, show "ID4" "Zed" 9L false
          8102, show "ID5" "Solo" 1L false ]

    let section = (Dna.foldSeasons rows).Value
    let spring = section.Listed |> List.find (fun s -> s.Name = "spring")
    Assert.Equal<string list>([ "ID4"; "ID1"; "ID2"; "ID3" ], spring.Shows |> List.map _.SeriesId)
    Assert.Equal(2, spring.Airing)
    Assert.Equal(0, (section.Listed |> List.find (fun s -> s.Name = "summer")).Airing)
    // the top season (4 shows) carries its own count
    Assert.Equal(2, section.Top.Airing)

// ---------------------------------------------------------------------------
// The second look: open by default means at most six tiles; the long-run strip's plain episodes text
// ---------------------------------------------------------------------------

/// The html of one listed season row (from its tag to the next row's, or the end of the page)
let private seasonRow (key: string) (html: string) =
    let rows = tagsWith "season" html
    let i = rows |> List.findIndex (fun (_, m) -> attr "season" m = key)
    let start = fst rows[i]
    let stop = if i + 1 < rows.Length then fst rows[i + 1] else html.Length
    html.Substring(start, stop - start)

/// What follows the row's own <summary>: its content
let private rowContent (row: string) = row.Substring(row.IndexOf "</summary>")

[<Fact>]
let ``the open newest row shows six tiles, then a nested Show all with the rest; closed rows show everything`` () =
    task {
        // Fall 2026 (the newest, open) has eight shows; Winter 2026 (the top season, closed) has ten
        let fall = [ for i in 1..8 -> weekly $"NEWEST{i:D2}" (1400 + i) "Studio A" (day 2026 10 4) 2 1 ]
        let winter = [ for i in 1..10 -> weekly $"OLDER{i:D2}" (1420 + i) "Studio B" (day 2026 1 8) 2 1 ]
        let! app, _ = build (fall @ winter)
        use app = app
        let! html = dna app

        Assert.Equal<(string * bool) list>([ "fall-2026", true; "winter-2026", false ], seasonRowsOpen html)

        let open' = rowContent (seasonRow "fall-2026" html)
        let nested = open'.IndexOf "<details"
        Assert.True(nested > 0)
        Assert.Equal(6, (tagsWith "season-show" (open'.Substring(0, nested))).Length)
        Assert.Equal(2, (tagsWith "season-show" (open'.Substring nested)).Length)
        Assert.Contains("Show all 8 shows", innerText (Regex.Match(open'.Substring nested, "<summary.*?</summary>", RegexOptions.Singleline).Value))
        // the nested details is closed, and the row still counts all eight
        Assert.DoesNotMatch("^<details[^>]*\\sopen", open'.Substring nested)
        Assert.Equal("8", attr "season-shows" (snd (tagsWith "season" html |> List.head)))

        // a closed row, once opened, holds all its tiles and no nested details
        let closed = rowContent (seasonRow "winter-2026" html)
        Assert.DoesNotContain("<details", closed)
        Assert.Equal(10, (tagsWith "season-show" closed).Length)
    }

[<Fact>]
let ``the open newest row has no nested Show all at six shows or fewer`` () =
    task {
        let fall = [ for i in 1..6 -> weekly $"NEWSIX{i}" (1500 + i) "Studio A" (day 2026 10 4) 2 1 ]
        let winter = [ for i in 1..8 -> weekly $"OLDEIGHT{i}" (1520 + i) "Studio B" (day 2026 1 8) 2 1 ]
        let! app, _ = build (fall @ winter)
        use app = app
        let! html = dna app

        let open' = rowContent (seasonRow "fall-2026" html)
        Assert.DoesNotContain("<details", open')
        Assert.Equal(6, (tagsWith "season-show" open').Length)
    }

[<Fact>]
let ``the long-run strip says plain episodes, in both languages`` () =
    task {
        let! app, _ = build (longAndShort ())
        use app = app
        let! en = get app "en"
        let! es = get app "es-ES,es;q=0.9"

        let text (html: string) =
            Regex.Match(html, "data-long-run-episodes=\"264\"[^>]*>([^<]*)<").Groups[1].Value

        Assert.Equal("264 episodes", text en)
        Assert.Equal("264 episodios", text es)
    }
