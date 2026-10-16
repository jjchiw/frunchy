module FrunchyBackend.Tests.CompatibilityTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure.Compatibility
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Views

// Feature 011: `Compatibility.load` on real Postgres with the 2026 sample and the feature 003 fixtures,
// then the card (T005) and the compare page (T007) built on it.

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

// ---------------------------------------------------------------------------
// Expected figures, computed here in F# from raw rows, independently of Compatibility.fs
// (except the level's band function)
// ---------------------------------------------------------------------------

type private Facts =
    { Username: string
      DisplayName: string
      /// series id -> title, ms (a partial episode up to its playhead), fully watched episodes
      Series: Map<string, string * int64 * int64>
      Fully: int64
      /// genre -> deduplicated ms of matched seasons
      GenreMs: Map<string, int64>
      /// studio -> deduplicated ms, series covered
      Studios: Map<string, int64 * Set<string>>
      MatchedMs: int64
      DedupMs: int64 }

type private Watch =
    { Id: string
      Series: string
      Key: string
      Ms: int64
      Fully: bool
      Mal: int option
      Title: string }

let private factsOf (db: DbConfig) (username: string) : Task<Facts> =
    task {
        let! rows =
            Db.dump
                db
                $"""SELECT e.id, e.series_id, coalesce(m.shared_from, e.season_id, ''), coalesce(e.episode_number::text, ''),
                           w.fully_watched, w.playhead_s, e.duration_ms, coalesce(m.mal_id::text, ''),
                           coalesce(s.title, ''), coalesce(e.series_title, '')
                    FROM watch_events w JOIN users u ON u.id = w.user_id
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    LEFT JOIN cr_series s ON s.id = e.series_id
                    WHERE u.username = '{username}' AND e.duration_ms IS NOT NULL"""

        let all: Watch list =
            [ for row in rows ->
                  match row.Split '|' with
                  | [| id; series; original; number; fully; playhead; duration; mal; seriesTitle; episodeSeriesTitle |] ->
                      let fully = Boolean.Parse fully
                      let duration = int64 duration

                      { Id = id
                        Series = series
                        Key = (if number = "" || original = "" then id else $"{original}#{number}")
                        Ms = (if fully then duration else min (int64 playhead * 1000L) duration)
                        Fully = fully
                        Mal = (if mal = "" then None else Some(int mal))
                        Title = (if seriesTitle <> "" then seriesTitle elif episodeSeriesTitle <> "" then episodeSeriesTitle else series) }
                  | _ -> failwith row ]

        let! display = Db.scalar<string> db $"SELECT display_name FROM users WHERE username = '{username}'" []

        let! tagRows =
            Db.dump db "SELECT at.mal_id, at.kind, t.name FROM mal_anime_tags at JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id"

        let tags = [ for r in tagRows -> let p = r.Split '|' in int p[0], p[1], p[2] ]

        // A dub and its original: one episode, the larger minutes (ties: the smaller id)
        let dedup: Watch list =
            all |> List.groupBy _.Key |> List.map (fun (_, xs) -> xs |> List.sortBy (fun x -> -x.Ms, x.Id) |> List.head)

        let matched = dedup |> List.filter _.Mal.IsSome

        let byTag kind =
            [ for w in matched do
                  for mal, k, name in tags do
                      if k = kind && Some mal = w.Mal then yield name, w ]

        let series =
            all
            |> List.groupBy _.Series
            |> List.map (fun (id, xs) -> id, ((List.head xs).Title, xs |> List.sumBy _.Ms, xs |> List.filter _.Fully |> List.length |> int64))
            |> Map.ofList

        return
            { Username = username
              DisplayName = display
              Series = series
              Fully = all |> List.filter _.Fully |> List.length |> int64
              GenreMs = byTag "genre" |> List.groupBy fst |> List.map (fun (n, ws) -> n, ws |> List.sumBy (snd >> _.Ms)) |> Map.ofList
              Studios =
                byTag "studio"
                |> List.groupBy fst
                |> List.map (fun (n, ws) -> n, (ws |> List.sumBy (snd >> _.Ms), ws |> List.map (snd >> _.Series) |> Set.ofList))
                |> Map.ofList
              MatchedMs = matched |> List.sumBy _.Ms
              DedupMs = dedup |> List.sumBy _.Ms }
    }

type private Expected =
    { Level: int
      /// Every shared series: id, the owner's title, viewer episodes, owner episodes; most watched by both first
      Shared: (string * string * int64 * int64) list
      SharedGenres: string list
      /// Genre rows: name, viewer share, owner share (unrounded percent)
      Rows: (string * decimal * decimal) list
      /// Shared studios: name, shared series covered, summed ms
      Studios: (string * int * int64) list
      /// The owner's series the viewer never watched, most watched first (up to 8): id, title, owner episodes
      Theirs: (string * string * int64) list }

let private shares (genreMs: Map<string, int64>) =
    let total = genreMs |> Map.toList |> List.sumBy snd
    if total = 0L then Map.empty else genreMs |> Map.map (fun _ ms -> 100m * decimal ms / decimal total)

let private top (n: int) (s: Map<string, decimal>) =
    s |> Map.toList |> List.sortBy (fun (name, share) -> -share, name) |> List.truncate n |> List.map fst

let private expectedOf (viewer: Facts) (owner: Facts) : Expected =
    let seriesIds (f: Facts) = f.Series |> Map.toList |> List.map fst |> Set.ofList
    let a, b = seriesIds viewer, seriesIds owner
    let common = Set.intersect a b
    // shared over the smaller history, scaled down while it has fewer than 10 series
    let smaller = min a.Count b.Count
    let signal = if smaller = 0 then 0.0 else float common.Count / float smaller * min 1.0 (float smaller / 10.0)

    let cosine =
        let dot =
            viewer.GenreMs
            |> Map.toList
            |> List.sumBy (fun (g, x) ->
                match owner.GenreMs.TryFind g with
                | Some y -> float x * float y
                | None -> 0.0)

        let norm (m: Map<string, int64>) = m |> Map.toList |> List.sumBy (fun (_, x) -> float x * float x) |> sqrt
        dot / (norm viewer.GenreMs * norm owner.GenreMs)

    let score =
        if viewer.GenreMs.IsEmpty || owner.GenreMs.IsEmpty then signal else (signal + cosine) / 2.0

    let vs, os = shares viewer.GenreMs, shares owner.GenreMs
    let share (m: Map<string, decimal>) g = m.TryFind g |> Option.defaultValue 0m

    let shared =
        [ for id in common ->
              let title, ownerMs, ownerEpisodes = owner.Series[id]
              let _, viewerMs, viewerEpisodes = viewer.Series[id]
              id, title, viewerEpisodes, ownerEpisodes, viewerMs, ownerMs ]
        |> List.sortBy (fun (id, title, _, _, viewerMs, ownerMs) -> -(min viewerMs ownerMs), title, id)
        |> List.map (fun (id, title, ve, oe, _, _) -> id, title, ve, oe)

    { Level = level score
      Shared = shared
      SharedGenres =
        top 5 vs
        |> List.filter (set (top 5 os)).Contains
        |> List.sortBy (fun g -> -(share vs g + share os g), g)
        |> List.truncate 3
      Rows =
        Set.union (set (top 4 vs)) (set (top 4 os))
        |> Set.toList
        |> List.sortBy (fun g -> -(share vs g + share os g), g)
        |> List.truncate 6
        |> List.map (fun g -> g, share vs g, share os g)
      Studios =
        [ for KeyValue(name, (ownerMs, ownerSeries)) in owner.Studios do
              match viewer.Studios.TryFind name with
              | Some(viewerMs, viewerSeries) ->
                  yield name, (Set.intersect common (Set.intersect viewerSeries ownerSeries)).Count, viewerMs + ownerMs
              | None -> () ]
        |> List.sortBy (fun (name, n, ms) -> -n, -ms, name)
        |> List.truncate 5
      Theirs =
        [ for KeyValue(id, (title, ms, episodes)) in owner.Series do
              if not (viewer.Series.ContainsKey id) then
                  yield id, title, ms, episodes ]
        |> List.sortBy (fun (id, title, ms, _) -> -ms, title, id)
        |> List.truncate 8
        |> List.map (fun (id, title, _, episodes) -> id, title, episodes) }

/// The data-* attributes of every element carrying data-{hook}, in document order
let private elements (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}(=\"[^\"]*\")?[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private one hook html = elements hook html |> List.exactlyOne
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""
let private attrs (hook: string) (name: string) (html: string) = elements hook html |> List.map (attr name)

// ---------------------------------------------------------------------------
// Fixtures: five signed-in users on one app
// ---------------------------------------------------------------------------

/// viewera has the whole 2026 sample; ownerb the same capture cut down to its 6 most watched series plus
/// 20 synthetic episodes; loner 20 synthetic episodes of its own; the two newbies nothing
type private Pair =
    { App: TestApp
      A: Browser
      B: Browser
      Loner: Browser
      NewbieA: Browser
      NewbieB: Browser }

let private otherAccount = "11111111-2222-3333-4444-555555555555"

let private withPair () =
    task {
        let! app = newApp ()
        let! a = signedUp app "sub-viewera" "viewera"
        let! b = signedUp app "sub-ownerb" "ownerb"
        let! loner = signedUp app "sub-loner" "loner"
        let! newbieA = signedUp app "sub-newbiea" "newbiea"
        let! newbieB = signedUp app "sub-newbieb" "newbieb"
        let! keyA = connectExtension a
        let! keyB = connectExtension b
        let! keyLoner = connectExtension loner

        let! _ = submitAccepted app keyA (load2026Sample ())
        let! _ = submitAccepted app keyB (load2026SampleRaw () |> withProfile "profile-second")
        let! _ = processAll app.Db

        routeSampleCatalog app.Mal
        let! _ = enrichAll app

        // ownerb keeps 6 series, matched ones first so it has genres to share, except the most watched matched
        // one (only viewera has it: it shows as "in their history, not yours" with a MyAnimeList score from the
        // other side)
        do!
            Db.execute
                app.Db
                """WITH topmatched AS (
                       SELECT e.series_id FROM watch_events w JOIN users u ON u.id = w.user_id
                       JOIN cr_episodes e ON e.id = w.cr_episode_id
                       JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                       WHERE u.username = 'ownerb' GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1),
                   kept AS (
                       SELECT e2.series_id FROM watch_events w2 JOIN users u2 ON u2.id = w2.user_id
                       JOIN cr_episodes e2 ON e2.id = w2.cr_episode_id
                       LEFT JOIN cr_season_matches m2 ON m2.season_id = e2.season_id AND m2.status = 'matched'
                       WHERE u2.username = 'ownerb' AND e2.series_id NOT IN (SELECT series_id FROM topmatched)
                       GROUP BY e2.series_id ORDER BY bool_or(m2.mal_id IS NOT NULL) DESC, count(*) DESC, e2.series_id LIMIT 6)
                   DELETE FROM watch_events w USING users u
                   WHERE w.user_id = u.id AND u.username = 'ownerb'
                     AND w.cr_episode_id NOT IN (SELECT e.id FROM cr_episodes e WHERE e.series_id IN (SELECT series_id FROM kept))"""

        let! _ = submitAccepted app keyB (envelopeForProfile "profile-second" (syntheticPages RealAccountId 2 10))
        let! _ = submitAccepted app keyLoner (envelopeForProfile "profile-loner" (syntheticPages otherAccount 2 10))
        let! _ = processAll app.Db
        let! _ = enrichAll app

        return
            { App = app
              A = a
              B = b
              Loner = loner
              NewbieA = newbieA
              NewbieB = newbieB }
    }

let private dashboard (browser: Browser) (username: string) =
    task {
        let! page = browser.Get $"/u/{username}"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        return page.Html
    }

/// The same user in another language: a second sign-in through the fake provider
let private inLanguage (app: TestApp) (language: string) (subject: string) =
    task {
        let browser = app.NewBrowser language
        let! _ = signInWith browser subject
        return browser
    }

// ---------------------------------------------------------------------------
// The card (T005)
// ---------------------------------------------------------------------------

let private assertCard (expected: Expected) (owner: Facts) (html: string) =
    let card = one "compat-card" html
    Assert.Equal(owner.Username, attr "compat-card" card)
    Assert.Equal(string expected.Level, attr "level" card)
    Assert.Equal(string expected.Level, attr "level-meter" (one "level-meter" html))
    Assert.Equal(string expected.Level, attr "level-name" (one "level-name" html))
    Assert.Equal(string expected.Shared.Length, attr "shared-count" (one "shared-count" html))
    Assert.Equal<string list>(expected.Shared |> List.truncate 4 |> List.map (fun (id, _, _, _) -> id), attrs "shared-series" "shared-series" html)
    Assert.Equal<string list>(expected.SharedGenres, attrs "shared-genre" "shared-genre" html)

    match expected.Studios with
    | (name, n, _) :: _ ->
        let studio = one "shared-studio" html
        Assert.Equal(name, attr "shared-studio" studio)
        Assert.Equal(string n, attr "studio-series" studio)
    | [] -> Assert.Empty(elements "shared-studio" html)

    // each shown series title links to the owner's anime page
    for id, title, _, _ in expected.Shared |> List.truncate 4 do
        Assert.Contains($"href=\"/u/{owner.Username}/anime/{id}\"", html)
        Assert.Contains(title, html)

    // the way to the page
    one "compare-link" html |> ignore
    Assert.Contains($"href=\"/u/{owner.Username}/compare\"", html)
    Assert.Empty(elements "compat-not-enough" html)
    Assert.Empty(elements "shared-none" html)

[<Fact>]
let ``the card on a dashboard shows every figure, equal to an independent computation, and both sides see the same level`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! a = factsOf app.Db "viewera"
        let! b = factsOf app.Db "ownerb"
        Assert.True(a.Fully >= 15L && b.Fully >= 15L, "both have enough history")
        let! cardAB = dashboard p.A "ownerb"
        let! cardBA = dashboard p.B "viewera"
        let ab, ba = expectedOf a b, expectedOf b a

        // the fixtures give the comparison something to say
        Assert.True(ab.Shared.Length >= 2 && ab.Shared.Length < a.Series.Count, "ownerb shares some, not all, of viewera's series")
        Assert.NotEmpty ab.SharedGenres

        assertCard ab b cardAB
        assertCard ba a cardBA

        // the same level and the same shared series from both sides (SC-002)
        Assert.Equal(ab.Level, ba.Level)
        Assert.Equal(attr "level" (one "compat-card" cardAB), attr "level" (one "compat-card" cardBA))
        Assert.Equal(attr "shared-count" (one "shared-count" cardAB), attr "shared-count" (one "shared-count" cardBA))
        Assert.Equal<string list>(
            ab.Shared |> List.map (fun (id, _, _, _) -> id) |> List.sort,
            ba.Shared |> List.map (fun (id, _, _, _) -> id) |> List.sort
        )

        // the title and the level read in the owner's name, and the meter is one image with an accessible name
        Assert.Contains(Texts.en.CompatTitle b.DisplayName, cardAB)
        Assert.Contains(Texts.en.CompatLevel ab.Level, cardAB)
        Assert.Contains(Texts.en.CompatSeeComparison, cardAB)
        Assert.Contains($"aria-label=\"{Texts.en.CompatMeterLabel ab.Level}\"", cardAB)
        Assert.Contains("role=\"img\"", cardAB)
    }

[<Fact>]
let ``the card shows for a signed-in non-owner only: signed out and on your own dashboard there is none`` () =
    task {
        let! p = withPair ()
        use app = p.App

        let! anonymous = getWithLanguage app.Client "en" "/u/ownerb"
        let! html = readHtml anonymous
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode)
        Assert.Empty(elements "compat-card" html)
        Assert.DoesNotContain(Texts.en.CompatSeeComparison, html)

        let! own = dashboard p.A "viewera"
        Assert.Empty(elements "compat-card" own)
        Assert.Empty(elements "compare-link" own)
        // the dashboard is still the dashboard
        Assert.NotEmpty(elements "stat" own)
    }

[<Fact>]
let ``a history under 15 fully watched episodes says whose, with no level`` () =
    task {
        let! p = withPair ()
        use app = p.App

        let check (html: string) (who: string) (text: string) =
            let card = one "compat-card" html
            Assert.Equal("", attr "level" card)
            Assert.Equal(who, attr "compat-not-enough" (one "compat-not-enough" html))
            Assert.Contains(text, html)
            Assert.Empty(elements "level-meter" html)
            Assert.Empty(elements "level-name" html)
            Assert.Empty(elements "shared-count" html)
            Assert.Empty(elements "shared-series" html)
            // the way to the page stays
            Assert.Contains("/compare\"", html)

        let! viewerShort = dashboard p.NewbieA "ownerb"
        check viewerShort "viewer" Texts.en.CompatNotEnoughViewer

        let! ownerShort = dashboard p.A "newbieb"
        check ownerShort "owner" (Texts.en.CompatNotEnoughOwner "NEWBIEB")

        let! both = dashboard p.NewbieA "newbieb"
        check both "both" (Texts.en.CompatNotEnoughBoth "NEWBIEB")
    }

[<Fact>]
let ``no series in common gives no empty lists and says so`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! loner = factsOf app.Db "loner"
        let! a = factsOf app.Db "viewera"
        Assert.True(loner.Fully >= 15L)
        let expected = expectedOf loner a
        Assert.Empty expected.Shared

        let! html = dashboard p.Loner "viewera"
        let card = one "compat-card" html
        Assert.Equal(string expected.Level, attr "level" card)
        let count = one "shared-count" html
        Assert.Equal("0", attr "shared-count" count)
        Assert.True(count.ContainsKey "shared-none")
        Assert.Contains(Texts.en.CompatSharedCount 0, html)
        Assert.Empty(elements "shared-series" html)
        Assert.Empty(elements "shared-genre" html)
        Assert.Empty(elements "shared-studio" html)
        let start = html.IndexOf "data-compat-card"
        Assert.DoesNotContain("<ul", html.Substring(start, html.IndexOf "data-compare-link" - start))
    }

[<Fact>]
let ``the card reads in English and Spanish`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! a = factsOf app.Db "viewera"
        let! b = factsOf app.Db "ownerb"
        let expected = expectedOf a b
        let! spanish = inLanguage app "es" "sub-viewera"
        let! es = dashboard spanish "ownerb"
        let! en = dashboard p.A "ownerb"

        assertCard expected b es
        assertCard expected b en
        Assert.Contains(Texts.es.CompatTitle b.DisplayName, es)
        Assert.Contains(Texts.es.CompatLevel expected.Level, es)
        Assert.Contains(Texts.es.CompatSeeComparison, es)
        Assert.Contains($"aria-label=\"{Texts.es.CompatMeterLabel expected.Level}\"", es)
        Assert.Contains(Texts.es.CompatSharedCount expected.Shared.Length, es)
        Assert.DoesNotContain(Texts.en.CompatSeeComparison, es)
        Assert.Contains(Texts.en.CompatSharedCount expected.Shared.Length, en)
        Assert.DoesNotContain(Texts.es.CompatSeeComparison, en)

        // not enough history reads in Spanish too
        let! spanishNewbie = inLanguage app "es" "sub-newbiea"
        let! short = dashboard spanishNewbie "ownerb"
        Assert.Contains(Texts.es.CompatNotEnoughViewer, short)
    }

// ---------------------------------------------------------------------------
// The compare page (T007)
// ---------------------------------------------------------------------------

let private comparePage (browser: Browser) (owner: string) =
    task {
        let! page = browser.Get $"/u/{owner}/compare"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        return page.Html
    }

let private round1 (d: decimal) = Math.Round(d, 1, MidpointRounding.AwayFromZero)
let private inv (d: decimal) = d.ToString("0.0", Globalization.CultureInfo.InvariantCulture)

let private coverageOf (f: Facts) = if f.DedupMs = 0L then 0m else round1 (100m * decimal f.MatchedMs / decimal f.DedupMs)

/// `data-compare` also prefixes data-compare-summary and friends: the root is the one carrying the key itself
let private root (html: string) =
    elements "compare" html |> List.filter (fun m -> m.ContainsKey "compare") |> List.exactlyOne

/// MyAnimeList scores of a user's matched seasons, per series (formatted as the page's hook is)
let private scoresOf (db: DbConfig) (username: string) =
    task {
        let! rows =
            Db.dump
                db
                $"""SELECT DISTINCT e.series_id, to_char(a.score, 'FM990.00')
                    FROM watch_events w JOIN users u ON u.id = w.user_id
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    JOIN mal_anime a ON a.mal_id = m.mal_id
                    WHERE u.username = '{username}' AND e.duration_ms IS NOT NULL AND a.score IS NOT NULL"""

        return rows |> List.map (fun r -> let p = r.Split '|' in p[0], p[1]) |> List.groupBy fst |> List.map (fun (k, vs) -> k, vs |> List.map snd |> set) |> Map.ofList
    }

/// Every figure of the page, against the independent computation
let private assertPage (t: Texts.Texts) (viewer: Facts) (owner: Facts) (ownerScores: Map<string, Set<string>>) (expected: Expected) (html: string) =
    let page = root html
    Assert.Equal(owner.Username, attr "compare" page)
    Assert.Equal(string expected.Level, attr "level" page)
    Assert.Equal(string expected.Level, attr "level-meter" (one "level-meter" html))
    Assert.Equal(string expected.Level, attr "level-name" (one "level-name" html))
    Assert.Contains($"aria-label=\"{t.CompatMeterLabel expected.Level}\"", html)
    Assert.Contains(t.CompatLevel expected.Level, html)

    // the summary sentence: the shared count and the first shared genre
    let summary = t.CompatSummary owner.DisplayName expected.Shared.Length (List.tryHead expected.SharedGenres)
    Assert.Matches($"<h1[^>]*data-compare-summary[^>]*>\\s*{Regex.Escape summary}\\s*</h1>", html)

    // both people, with their series counts
    let people = elements "person" html
    Assert.Equal<string list>([ "viewer"; "owner" ], people |> List.map (attr "person"))
    Assert.Equal<string list>([ string viewer.Series.Count; string owner.Series.Count ], attrs "series-count" "series-count" html)
    Assert.Contains($"@{viewer.Username}", html)
    Assert.Contains($"@{owner.Username}", html)
    Assert.Contains(viewer.DisplayName, html)
    Assert.Contains(owner.DisplayName, html)

    // genres: up to 6 rows, both shares with one decimal, and a coverage note per person
    let rows = elements "genre-compare" html
    Assert.Equal<string list>(expected.Rows |> List.map (fun (g, _, _) -> g), rows |> List.map (attr "genre-compare"))
    Assert.Equal<string list>(expected.Rows |> List.map (fun (_, v, _) -> inv (round1 v)), rows |> List.map (attr "viewer-share"))
    Assert.Equal<string list>(expected.Rows |> List.map (fun (_, _, o) -> inv (round1 o)), rows |> List.map (attr "owner-share"))
    Assert.True(rows.Length <= 6)
    Assert.Equal(inv (coverageOf viewer), attr "viewer-coverage" (one "viewer-coverage" html))
    Assert.Equal(inv (coverageOf owner), attr "owner-coverage" (one "owner-coverage" html))
    Assert.Contains(t.CompatCoverage viewer.DisplayName (coverageOf viewer), html)
    Assert.Contains(t.CompatCoverage owner.DisplayName (coverageOf owner), html)
    Assert.Contains(t.CompatTimeTitle, html)

    // shared series: the 10 both watched most, each person's fully watched episodes, then "and N more"
    Assert.Equal(string expected.Shared.Length, attr "shared-count" (one "shared-count" html))
    let shown = expected.Shared |> List.truncate 10
    let sharedRows = elements "shared-series" html
    Assert.Equal<string list>(shown |> List.map (fun (id, _, _, _) -> id), sharedRows |> List.map (attr "shared-series"))
    Assert.Equal<string list>(shown |> List.map (fun (_, _, v, _) -> string v), sharedRows |> List.map (attr "viewer-episodes"))
    Assert.Equal<string list>(shown |> List.map (fun (_, _, _, o) -> string o), sharedRows |> List.map (attr "owner-episodes"))

    for id, title, _, _ in shown do
        Assert.Contains($"href=\"/u/{owner.Username}/anime/{id}\"", html)
        Assert.Contains(title, html)

    if expected.Shared.Length > 10 then
        Assert.Equal(string (expected.Shared.Length - 10), attr "shared-more" (one "shared-more" html))
        Assert.Contains(t.CompatMore(expected.Shared.Length - 10), html)
    else
        Assert.Empty(elements "shared-more" html)

    if expected.Shared.IsEmpty then
        Assert.True((one "shared-count" html).ContainsKey "shared-none")
    else
        Assert.Empty(elements "shared-none" html)

    // their series, not yours: the owner's most watched, with MyAnimeList's score when matched
    let theirs = elements "their-series" html
    Assert.Equal<string list>(expected.Theirs |> List.map (fun (id, _, _) -> id), theirs |> List.map (attr "their-series"))

    for t', (id, title, _) in List.zip theirs expected.Theirs do
        Assert.Contains($"href=\"/u/{owner.Username}/anime/{id}\"", html)
        Assert.Contains(title, html)

        // the score is one of the series' matched seasons' scores; a series with none shows no score
        match t'.TryFind "mal-score" with
        | Some score -> Assert.Contains(score, ownerScores.TryFind id |> Option.defaultValue Set.empty)
        | None -> ()

    if expected.Theirs.IsEmpty then
        Assert.DoesNotContain(t.CompatTheirsTitle owner.DisplayName, html)
    else
        Assert.Contains(t.CompatTheirsTitle owner.DisplayName, html)

    // studios both watch, with the number of shared series each covers
    let studios = elements "shared-studio" html
    Assert.Equal<string list>(expected.Studios |> List.map (fun (n, _, _) -> n), studios |> List.map (attr "shared-studio"))
    Assert.Equal<string list>(expected.Studios |> List.map (fun (_, n, _) -> string n), studios |> List.map (attr "studio-series"))

    // no raw score anywhere, and the way back
    Assert.DoesNotContain("data-score", html)
    one "dashboard-link" html |> ignore
    Assert.Contains($"href=\"/u/{owner.Username}\"", html)
    Assert.Empty(elements "compat-not-enough" html)

[<Fact>]
let ``the page shows every figure, equal to an independent computation, from both sides`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! a = factsOf app.Db "viewera"
        let! b = factsOf app.Db "ownerb"
        let! scoresA = scoresOf app.Db "viewera"
        let! scoresB = scoresOf app.Db "ownerb"
        let ab, ba = expectedOf a b, expectedOf b a

        let! pageAB = comparePage p.A "ownerb"
        let! pageBA = comparePage p.B "viewera"

        // viewera -> ownerb: ownerb only has synthetic series the viewer lacks; viewera -> ownerb shares a few
        Assert.NotEmpty ab.Shared
        Assert.NotEmpty ab.Theirs
        Assert.NotEmpty ab.Rows
        assertPage Texts.en a b scoresB ab pageAB

        // ownerb -> viewera: viewera's other, matched series are "their series", with MyAnimeList scores
        Assert.Equal(8, ba.Theirs.Length)
        assertPage Texts.en b a scoresA ba pageBA
        Assert.NotEmpty(elements "their-series" pageBA |> List.filter (fun m -> m.ContainsKey "mal-score"))

        // the same level from both sides
        Assert.Equal(attr "level" (root pageAB), attr "level" (root pageBA))
    }

[<Fact>]
let ``more than 10 shared series show 10 and how many more`` () =
    task {
        let! app = newApp ()
        use app = app
        let! viewer = signedUp app "sub-twina" "twina"
        let! keyA = connectExtension viewer
        let! keyB = createUser app.Db "twinb"
        let! _ = submitAccepted app keyA (load2026Sample ())
        let! _ = submitAccepted app keyB (load2026SampleRaw () |> withProfile "profile-second")
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        let! a = factsOf app.Db "twina"
        let! b = factsOf app.Db "twinb"
        let! scores = scoresOf app.Db "twinb"
        let expected = expectedOf a b
        Assert.True(expected.Shared.Length > 10, "the sample has more than 10 series")
        Assert.Equal(a.Series.Count, expected.Shared.Length)

        let! html = comparePage viewer "twinb"
        assertPage Texts.en a b scores expected html
        Assert.Equal(10, (elements "shared-series" html).Length)
        // nothing is only theirs: that section is absent, not empty
        Assert.Empty(elements "their-series" html)
        Assert.Equal(5, expected.Level)
    }

[<Fact>]
let ``signed out goes to sign-in and back, yourself goes to your dashboard, unknown and invalid usernames are not found`` () =
    task {
        let! p = withPair ()
        use app = p.App

        // signed out: sign-in first, whoever the username is
        let anonymous = app.NewBrowser()
        let! response = anonymous.GetRaw "/u/ownerb/compare"
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal("/signin?returnUrl=%2Fu%2Fownerb%2Fcompare", string response.Headers.Location)
        let! unknown = anonymous.GetRaw "/u/nobody-here/compare"
        Assert.Equal(HttpStatusCode.Found, unknown.StatusCode)
        Assert.StartsWith("/signin?returnUrl=", string unknown.Headers.Location)

        // (a local returnUrl is honoured after sign-in: SignInTests)
        // yourself: your dashboard (the username is read case-insensitively)
        for path in [ "/u/viewera/compare"; "/u/VIEWERA/compare" ] do
            let! own = p.A.GetRaw path
            Assert.Equal(HttpStatusCode.Found, own.StatusCode)
            Assert.Equal("/u/viewera", string own.Headers.Location)

        // unknown, invalid, reserved and deleted: the not-found page
        for path in [ "/u/nobody-here/compare"; "/u/a/compare"; "/u/admin/compare"; "/u/has space/compare" ] do
            let! missing = p.A.Get path
            Assert.Equal(HttpStatusCode.NotFound, missing.Status)
            Assert.Contains(Texts.en.UserNotFoundTitle, missing.Html)
            Assert.Empty(elements "compare" missing.Html)

        do! Db.execute app.Db "DELETE FROM users WHERE username = 'newbieb'"
        let! deleted = p.A.Get "/u/newbieb/compare"
        Assert.Equal(HttpStatusCode.NotFound, deleted.Status)
        Assert.Contains(Texts.en.UserNotFoundTitle, deleted.Html)
    }

[<Fact>]
let ``a history under 15 fully watched episodes shows the not-enough state in place of the sections`` () =
    task {
        let! p = withPair ()
        use app = p.App

        let check (html: string) (who: string) (text: string) =
            let page = root html
            Assert.Equal("", attr "level" page)
            Assert.Equal(who, attr "compat-not-enough" (one "compat-not-enough" html))
            Assert.Contains(text, html)

            for hook in [ "level-meter"; "level-name"; "genre-compare"; "shared-series"; "their-series"; "shared-studio"; "compare-summary" ] do
                Assert.Empty(elements hook html)

        let! viewerShort = comparePage p.NewbieA "ownerb"
        check viewerShort "viewer" Texts.en.CompatNotEnoughViewer
        let! ownerShort = comparePage p.A "newbieb"
        check ownerShort "owner" (Texts.en.CompatNotEnoughOwner "NEWBIEB")
        let! both = comparePage p.NewbieA "newbieb"
        check both "both" (Texts.en.CompatNotEnoughBoth "NEWBIEB")
    }

[<Fact>]
let ``with no series in common the page says so and lists nothing`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! loner = factsOf app.Db "loner"
        let! a = factsOf app.Db "viewera"
        let! scores = scoresOf app.Db "viewera"
        let expected = expectedOf loner a
        let! html = comparePage p.Loner "viewera"
        assertPage Texts.en loner a scores expected html
        Assert.Empty(elements "shared-series" html)
        Assert.Contains(Texts.en.CompatSummary a.DisplayName 0 None, html)
        // the loner has no MyAnimeList genres: the page says the level rests on shared series alone
        Assert.Contains(Texts.en.CompatNoGenres, html)
    }

[<Fact>]
let ``a compare page between two users with 2,000 watched episodes each is ready in under a second`` () =
    task {
        let! app = newApp ()
        use app = app
        let! viewer = signedUp app "sub-speeda" "speeda"
        let! keyA = connectExtension viewer
        let! keyB = createUser app.Db "speedb"
        // 20 pages of 100 episodes each: 100 series of 20 episodes, the same on both sides
        let! _ = submitAccepted app keyA (envelopeOf (syntheticPages RealAccountId 20 100))
        let! _ = submitAccepted app keyB (envelopeForProfile "profile-speed" (syntheticPages otherAccount 20 100))
        let! _ = processAll app.Db
        let! episodes = Db.scalar<int64> app.Db "SELECT count(*) FROM watch_events w JOIN users u ON u.id = w.user_id WHERE u.username = 'speedb'" []
        Assert.Equal(2000L, episodes)

        // a warm-up, then the timed request
        let! _ = comparePage viewer "speedb"
        let clock = Diagnostics.Stopwatch.StartNew()
        let! html = comparePage viewer "speedb"
        clock.Stop()

        Assert.True(clock.ElapsedMilliseconds < 1000L, $"took {clock.ElapsedMilliseconds} ms")
        Assert.Equal("100", attr "shared-count" (one "shared-count" html))
        Assert.Equal("90", attr "shared-more" (one "shared-more" html))
    }

[<Fact>]
let ``the card's link opens the compare page`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! html = dashboard p.A "ownerb"
        let tag = Regex.Match(html, "<a\\b[^>]*data-compare-link[^>]*>").Value
        let href = Regex.Match(tag, "href=\"([^\"]+)\"").Groups[1].Value
        Assert.Equal("/u/ownerb/compare", href)

        let! page = p.A.Get href
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Equal("ownerb", attr "compare" (root page.Html))
        // and the page leads back to the dashboard
        let! dashboardAgain = p.A.Get "/u/ownerb"
        Assert.Equal(HttpStatusCode.OK, dashboardAgain.Status)
        Assert.Contains("/u/ownerb\"", page.Html)
    }

[<Fact>]
let ``the page reads in English and Spanish with the same figures`` () =
    task {
        let! p = withPair ()
        use app = p.App
        let! a = factsOf app.Db "viewera"
        let! b = factsOf app.Db "ownerb"
        let! scores = scoresOf app.Db "ownerb"
        let expected = expectedOf a b
        let! spanish = inLanguage app "es" "sub-viewera"
        let! es = comparePage spanish "ownerb"
        let! en = comparePage p.A "ownerb"

        assertPage Texts.en a b scores expected en
        assertPage Texts.es a b scores expected es

        for text in [ Texts.es.CompatTimeTitle; Texts.es.CompatSharedTitle; Texts.es.CompatTheirsTitle b.DisplayName; Texts.es.CompatExplain ] do
            Assert.Contains(text, es)

        Assert.DoesNotContain(Texts.en.CompatTimeTitle, es)
        Assert.DoesNotContain(Texts.es.CompatTimeTitle, en)
        Assert.Contains($"<title>{Texts.es.CompatPageTitle b.DisplayName}", es)

        // the hooks carry the same invariant values in both languages
        Assert.Equal<string list>(attrs "genre-compare" "viewer-share" en, attrs "genre-compare" "viewer-share" es)
        Assert.Equal<string list>(attrs "genre-compare" "owner-share" en, attrs "genre-compare" "owner-share" es)
        Assert.Equal(attr "viewer-coverage" (one "viewer-coverage" en), attr "viewer-coverage" (one "viewer-coverage" es))

        // not enough history reads in Spanish too
        let! spanishNewbie = inLanguage app "es" "sub-newbiea"
        let! short = comparePage spanishNewbie "ownerb"
        Assert.Contains(Texts.es.CompatNotEnoughViewer, short)
    }
