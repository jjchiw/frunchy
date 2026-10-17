module FrunchyBackend.Tests.SeriesPageTests

open System
open System.Diagnostics
open System.Net
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 013, User Story 1: the public series page (contracts/series-page.md). The 2026 sample with the feature
// 003 MyAnimeList fixtures, and small hand-seeded catalogs. Every expectation is computed in F# from raw rows or
// independent SQL, never from the slice's own statement.

let private now = DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)

let private get (app: TestApp) (language: string) (path: string) =
    task {
        let! response = getWithLanguage app.Client language path
        let! html = readHtml response
        return response.StatusCode, html
    }

let private seriesPage (app: TestApp) (language: string) (seriesId: string) =
    task {
        let! status, html = get app language $"/anime/{seriesId}"
        Assert.Equal(HttpStatusCode.OK, status)
        return html
    }

let private appAt (db: DbConfig) (config: (string * string) list) (at: DateTimeOffset) =
    let clock = TestClock(Now = at)
    createAppWith db config (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

// ---------------------------------------------------------------------------
// Reading the page through its data-* hooks
// ---------------------------------------------------------------------------

let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""

type private PageSeason =
    { Attrs: Map<string, string>
      Mal: string option
      MalCompact: bool
      MalMissing: bool
      /// A data-season-collapsed element is inside the season
      Collapsed: bool
      Episodes: Map<string, string> list }

/// The seasons of the page in document order, each with its MyAnimeList marker and its episodes
let private outline (html: string) =
    let items =
        [ for m in Regex.Matches(html, "<[a-z0-9]+\\b[^>]*\\bdata-(season|episode|mal|mal-missing|season-collapsed)=\"[^\"]*\"[^>]*>") ->
              m.Groups[1].Value,
              [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
              |> Map.ofList ]

    items
    |> List.fold
        (fun (seasons: PageSeason list) (kind, attrs) ->
            let update (f: PageSeason -> PageSeason) =
                match List.rev seasons with
                | last :: before -> List.rev (f last :: before)
                | [] -> failwith "a series page element came before any season"

            match kind with
            | "season" ->
                seasons
                @ [ { Attrs = attrs; Mal = None; MalCompact = false; MalMissing = false; Collapsed = false; Episodes = [] } ]
            | "mal" -> update (fun s -> { s with Mal = Some(attr "mal" attrs); MalCompact = attrs.ContainsKey "mal-compact" })
            | "mal-missing" -> update (fun s -> { s with MalMissing = true })
            | "season-collapsed" -> update (fun s -> { s with Collapsed = true })
            | _ -> update (fun s -> { s with Episodes = s.Episodes @ [ attrs ] }))
        []

let private title (html: string) =
    Regex.Match(html, "data-series-title=\"\"[^>]*>([^<]*)<").Groups[1].Value

let private peopleAttr (hook: string) (html: string) =
    let m = Regex.Match(html, $"data-{hook}=\"([^\"]*)\"")
    if m.Success then Some m.Groups[1].Value else None

/// href of every episode link of the page, in order
let private episodeHrefs (html: string) =
    [ for m in Regex.Matches(html, "<a\\b[^>]*data-episode-link=\"[^\"]*\"[^>]*>") ->
          Regex.Match(m.Value, "href=\"([^\"]*)\"").Groups[1].Value ]

// ---------------------------------------------------------------------------
// Expected figures from raw rows
// ---------------------------------------------------------------------------

type private Ep =
    { Id: string
      Series: string
      Season: string
      Number: int option
      Title: string
      /// An episode without a title sorts after the titled ones with the same number (NULLS LAST)
      Untitled: bool
      Comments: int }

let private knownEpisodes (db: DbConfig) =
    task {
        let! rows =
            Db.dump
                db
                """SELECT e.series_id, coalesce(e.season_id, ''), e.id, coalesce(e.episode_number::text, ''),
                          replace(coalesce(e.title, ''), '|', '/'), (e.title IS NULL),
                          (SELECT count(*) FROM comments c WHERE c.cr_episode_id = e.id AND c.deleted_at IS NULL
                                                             AND c.audience IN ('public', 'unlisted'))
                   FROM cr_episodes e WHERE e.duration_ms IS NOT NULL"""

        return
            [ for row in rows ->
                  match row.Split '|' with
                  | [| series; season; id; number; title; untitled; comments |] ->
                      { Id = id
                        Series = series
                        Season = season
                        Number = (if number = "" then None else Some(int number))
                        Title = title
                        Untitled = Boolean.Parse untitled
                        Comments = int comments }
                  | _ -> failwith row ]
    }

type private SeasonFacts =
    { Number: int option
      Audio: string
      Dub: bool
      Mal: string }

let private seasonFacts (db: DbConfig) =
    task {
        let! seasons = Db.dump db "SELECT id, coalesce(season_number::text, ''), coalesce(audio_locale, '') FROM cr_seasons"
        let! matches = Db.dump db "SELECT season_id, coalesce(method, ''), mal_id::text FROM cr_season_matches WHERE status = 'matched'"

        let matchOf =
            matches |> List.map (fun r -> let p = r.Split '|' in p[0], (p[1], p[2])) |> Map.ofList

        return
            seasons
            |> List.map (fun r ->
                let p = r.Split '|'
                let method', mal = matchOf.TryFind p[0] |> Option.defaultValue ("", "")

                p[0],
                { Number = (if p[1] = "" then None else Some(int p[1]))
                  Audio = p[2]
                  Dub = (method' = "dub")
                  Mal = mal })
            |> Map.ofList
    }

/// Seasons in Crunchyroll's order (number nulls last, then id; the seasonless last)
let private expectedSeasons (facts: Map<string, SeasonFacts>) (eps: Ep list) =
    eps
    |> List.groupBy _.Season
    |> List.sortBy (fun (id, _) ->
        id = "", (if id = "" || not (facts.ContainsKey id) then None else facts[id].Number) |> Option.defaultValue Int32.MaxValue, id)

/// Episodes in order: number nulls last, then title, then id
let private expectedEpisodes (eps: Ep list) =
    eps
    |> List.sortWith (fun a b ->
        let n (e: Ep) = e.Number |> Option.defaultValue Int32.MaxValue

        match compare (n a) (n b) with
        | 0 ->
            match compare a.Untitled b.Untitled with
            | 0 ->
                match String.CompareOrdinal(a.Title, b.Title) with
                | 0 -> String.CompareOrdinal(a.Id, b.Id)
                | c -> c
            | c -> c
        | c -> c)

/// Every season and episode of a series' page against the raw rows
let private assertSeasons (all: Ep list) (facts: Map<string, SeasonFacts>) (seriesId: string) (html: string) =
    let eps = all |> List.filter (fun e -> e.Series = seriesId)
    Assert.NotEmpty eps
    Assert.Equal(seriesId, peopleAttr "series-page" html |> Option.get)

    let expected = expectedSeasons facts eps
    let page = outline html

    Assert.Equal<string list>(
        expected |> List.map (fun (id, _) -> if id = "" then "other" else id),
        page |> List.map (fun s -> attr "season" s.Attrs)
    )

    for (id, seasonEps), shown in List.zip expected page do
        Assert.Equal(string seasonEps.Length, attr "known" shown.Attrs)

        if id = "" || not (facts.ContainsKey id) then
            Assert.False(shown.Attrs.ContainsKey "audio")
            Assert.False(shown.Attrs.ContainsKey "dub")
            Assert.Equal(None, shown.Mal)
            Assert.False shown.MalMissing
        else
            let f = facts[id]
            Assert.Equal(f.Audio, attr "audio" shown.Attrs)
            Assert.Equal(f.Dub, shown.Attrs.ContainsKey "dub")

            if f.Mal = "" then
                Assert.True(shown.MalMissing, $"season {id} is unmatched")
                Assert.Equal(None, shown.Mal)
            else
                Assert.False shown.MalMissing
                Assert.Equal(Some f.Mal, shown.Mal)

        // episodes in order, each with its comment count and a link to its own page
        let expectedEps = expectedEpisodes seasonEps
        Assert.Equal<string list>(expectedEps |> List.map _.Id, shown.Episodes |> List.map (attr "episode"))
        Assert.Equal<string list>(expectedEps |> List.map (fun e -> string e.Comments), shown.Episodes |> List.map (attr "comments"))


    // every episode links to its own page, in page order
    Assert.Equal<string list>(
        expected |> List.collect (fun (_, seasonEps) -> expectedEpisodes seasonEps) |> List.map (fun e -> $"/anime/{seriesId}/episode/{e.Id}"),
        episodeHrefs html
    )

    // MyAnimeList details show once per entry: the first season with an entry has them, later ones the compact link
    let withMal = page |> List.filter (fun s -> s.Mal.IsSome)

    for malId, group in withMal |> List.groupBy _.Mal do
        Assert.False((List.head group).MalCompact, $"entry {malId} is shown in full first")

        for later in List.tail group do
            Assert.True(later.MalCompact, $"entry {malId} appears again only as a link")

    // the numbers on the page add up
    Assert.Equal(eps.Length, page |> List.sumBy (fun s -> s.Episodes.Length))

// ---------------------------------------------------------------------------
// The 2026 sample, every series (SC-001)
// ---------------------------------------------------------------------------

let private withSample () = sampleApp ()

[<Fact>]
let ``every series of the 2026 sample shows its header, seasons, episodes and MyAnimeList entries, in en and es`` () =
    task {
        let! app, _ = withSample ()
        use app = app
        let! all = knownEpisodes app.Db
        let! facts = seasonFacts app.Db

        let! titles =
            Db.dump
                app.Db
                """SELECT e.series_id, coalesce(s.title, min(e.series_title), e.series_id), coalesce(s.slug, '')
                   FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id
                   WHERE e.duration_ms IS NOT NULL
                   GROUP BY e.series_id, s.title, s.slug ORDER BY 1"""

        Assert.True(titles.Length > 5, "the sample has several series")
        app.Mal.Clear()

        for row in titles do
            let p = row.Split '|'
            let seriesId, expectedTitle, slug = p[0], p[1], p[2]

            for language in [ "en"; "es" ] do
                let! html = seriesPage app language seriesId
                assertSeasons all facts seriesId html
                Assert.Equal(expectedTitle, title html)

                // Crunchyroll's link only with a slug
                if slug = "" then
                    Assert.DoesNotContain("data-cr-series-link", html)
                else
                    Assert.Equal(Some $"https://www.crunchyroll.com/series/{seriesId}/{slug}", hrefOfHook "cr-series-link" html)

                let t = if language = "es" then Texts.es else Texts.en
                Assert.Contains($"<html lang=\"{t.Lang}\"", html)
                Assert.Contains(t.AnimeSeasonsTitle, html)
                Assert.Contains(t.SeriesKnownNote, html)

        // the page never calls MyAnimeList
        Assert.Empty app.Mal.Requests
    }

[<Fact>]
let ``the sample's multi-season series with a dub, an unmatched season and a shared entry read as expected`` () =
    task {
        let! app, _ = withSample ()
        use app = app

        let! jjk = Db.scalar<string> app.Db "SELECT series_id FROM cr_seasons WHERE id = 'G6MGC3778'" []
        let! dubbed = Db.scalar<string> app.Db "SELECT series_id FROM cr_seasons WHERE id = 'GRK5CNJ14'" []

        let! unmatched =
            Db.scalar<string>
                app.Db
                """SELECT e.series_id FROM cr_episodes e
                   LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                   WHERE e.duration_ms IS NOT NULL AND e.season_id IS NOT NULL AND m.mal_id IS NULL
                   GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1"""
                []

        let! jjkPage = seriesPage app "en" jjk
        Assert.True((outline jjkPage).Length > 1, "JUJUTSU KAISEN has several seasons")
        Assert.Contains("JUJUTSU KAISEN", title jjkPage)

        let! dubPage = seriesPage app "en" dubbed
        let seasons = outline dubPage
        let ids = seasons |> List.map (fun s -> attr "season" s.Attrs)
        Assert.Contains("GRK5CNJ14", ids)
        Assert.Contains("GYDQCGK5Q", ids)
        let! dubs = Db.dump app.Db "SELECT season_id FROM cr_season_matches WHERE method = 'dub'"
        let shownDubs = seasons |> List.filter (fun s -> s.Attrs.ContainsKey "dub") |> List.map (fun s -> attr "season" s.Attrs)
        Assert.NotEmpty shownDubs
        Assert.Equal<string list>(ids |> List.filter (fun id -> List.contains id dubs), shownDubs)
        // a dub shares its original's entry: that entry is shown in full once
        let entries = seasons |> List.choose _.Mal
        Assert.True(entries.Length > (List.distinct entries).Length, "the dub repeats its original's entry")

        let! unmatchedPage = seriesPage app "en" unmatched
        Assert.Contains(true, outline unmatchedPage |> List.map _.MalMissing)
        Assert.Contains(Texts.en.NotOnMyAnimeList, unmatchedPage)
    }

// ---------------------------------------------------------------------------
// Hand-seeded catalogs
// ---------------------------------------------------------------------------

let private userGuid (n: int) = Guid.Parse("00000000-0000-0000-0000-0000000000" + n.ToString "00")

let private insertUsers (db: DbConfig) (count: int) =
    task {
        for i in 1..count do
            do!
                Db.execute
                    db
                    $"INSERT INTO users (id, username, display_name) VALUES ('{userGuid i}', 'person{i}', 'Person Name {i}')"
    }

let private addWatch (db: DbConfig) (user: int) (episode: string) (at: DateTimeOffset) =
    Db.execute
        db
        $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
            VALUES ('{userGuid user}', '{episode}', '{at.UtcDateTime:O}', 1400, true, gen_random_uuid())"""

/// "MULTI001": a season with its dub (one MyAnimeList entry), a second season with its own entry, a third
/// unmatched. Season 2's entry is watched by two people, season 1's by one, so season 2's image is the poster.
let private multiDb () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 4

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES ('MULTI001', 'Multi Show', 'multi-show', now());
                   INSERT INTO cr_seasons (id, series_id, season_number, title, audio_locale, source_at) VALUES
                     ('MSEASON1', 'MULTI001', 1, 'Multi Show', 'ja-JP', now()),
                     ('MSEASOND', 'MULTI001', 1, 'Multi Show (English Dub)', 'en-US', now()),
                     ('MSEASON2', 'MULTI001', 2, NULL, 'ja-JP', now()),
                     ('MSEASON3', 'MULTI001', 3, 'Third', 'ja-JP', now());
                   INSERT INTO cr_episodes (id, series_id, season_id, series_title, title, episode_number, duration_ms, thumbnail_url) VALUES
                     ('MEP1S1', 'MULTI001', 'MSEASON1', 'Multi Show', 'First', 1, 1440000, 'https://img/m1.jpg'),
                     ('MEP2S1', 'MULTI001', 'MSEASON1', 'Multi Show', 'Second', 2, 1440000, 'https://img/m2.jpg'),
                     ('MEP1SD', 'MULTI001', 'MSEASOND', 'Multi Show', 'First (dub)', 1, 1440000, NULL),
                     ('MEP1S2', 'MULTI001', 'MSEASON2', 'Multi Show', 'Again', 1, 1380000, NULL),
                     ('MEP1S3', 'MULTI001', 'MSEASON3', 'Multi Show', 'Third one', 1, 1380000, NULL);
                   INSERT INTO mal_anime (mal_id, url, title, raw, data_hash, fetched_at, checked_at, image_url) VALUES
                     (900001, 'https://myanimelist.net/anime/900001', 'Multi Show', '{}', 'x', now(), now(), 'https://img/p1.jpg'),
                     (900002, 'https://myanimelist.net/anime/900002', 'Multi Show 2', '{}', 'x', now(), now(), 'https://img/p2.jpg');
                   INSERT INTO cr_season_matches (season_id, status, mal_id, method, shared_from) VALUES
                     ('MSEASON1', 'matched', 900001, 'search', NULL),
                     ('MSEASOND', 'matched', 900001, 'dub', 'MSEASON1'),
                     ('MSEASON2', 'matched', 900002, 'search', NULL)"""

        let at = now.AddDays -2.0
        do! addWatch db 1 "MEP1S1" at
        do! addWatch db 1 "MEP1S2" at
        do! addWatch db 2 "MEP1S2" at
        return db
    }

[<Fact>]
let ``MyAnimeList shows once per entry, the unmatched season says so and the poster is the entry most people watched`` () =
    task {
        let! db = multiDb ()
        use! app = appAt db [] now
        let! all = knownEpisodes db
        let! facts = seasonFacts db
        let! html = seriesPage app "en" "MULTI001"
        assertSeasons all facts "MULTI001" html

        let seasons = outline html
        Assert.Equal<string list>([ "MSEASON1"; "MSEASOND"; "MSEASON2"; "MSEASON3" ], seasons |> List.map (fun s -> attr "season" s.Attrs))
        // original in full, its dub as a link, season 2 in full, season 3 unmatched
        Assert.Equal<(string option * bool) list>(
            [ Some "900001", false; Some "900001", true; Some "900002", false; None, false ],
            seasons |> List.map (fun s -> s.Mal, s.MalCompact)
        )

        Assert.Equal<bool list>([ false; false; false; true ], seasons |> List.map _.MalMissing)
        Assert.Equal<bool list>([ false; true; false; false ], seasons |> List.map (fun s -> s.Attrs.ContainsKey "dub"))
        Assert.Equal<string list>([ "ja-JP"; "en-US"; "ja-JP"; "ja-JP" ], seasons |> List.map (attr "audio" << _.Attrs))
        // a season without a title is "Season N"
        Assert.Contains(Texts.en.AnimeSeasonNumber 2, html)
        Assert.Contains(Texts.en.NotOnMyAnimeList, html)
        Assert.Equal(1, Regex.Matches(html, "https://img/p2.jpg").Count)
        Assert.DoesNotContain("https://img/p1.jpg", html)
        Assert.Contains(Texts.en.AnimeWatchOnCrunchyroll, html)
        Assert.Equal(Some "https://www.crunchyroll.com/series/MULTI001/multi-show", hrefOfHook "cr-series-link" html)

        // a tie on people goes to the smaller mal_id; without any MyAnimeList image the newest thumbnail
        // entry 900001: users 1, 3, 4. Entry 900002: users 1, 2, 3
        do! addWatch db 3 "MEP2S1" (now.AddDays -1.0)
        do! addWatch db 4 "MEP1S1" (now.AddDays -1.5)
        do! addWatch db 3 "MEP1S2" (now.AddDays -1.0)
        let! tie = seriesPage app "en" "MULTI001"
        Assert.Contains("https://img/p1.jpg", tie)
        Assert.DoesNotContain("https://img/p2.jpg", tie)

        do! Db.execute db "UPDATE mal_anime SET image_url = NULL"
        let! thumb = seriesPage app "en" "MULTI001"
        // the most recently watched episode with a thumbnail: MEP2S1, watched yesterday by user 3
        Assert.Contains("https://img/m2.jpg", thumb)
        Assert.DoesNotContain("https://img/p", thumb)

        do! Db.execute db "UPDATE cr_episodes SET thumbnail_url = NULL"
        let! none = seriesPage app "en" "MULTI001"
        Assert.DoesNotContain("https://img/", none)
    }

[<Fact>]
let ``each episode counts its public and unlisted comments that are not deleted, hidden ones included`` () =
    task {
        let! db = multiDb ()
        use! app = appAt db [] now

        do!
            Db.execute
                db
                """INSERT INTO comments (id, cr_episode_id, author_user_id, remote_actor, content, audience, deleted_at, hidden_at) VALUES
                     ('c-public', 'MEP1S1', '00000000-0000-0000-0000-000000000001', NULL, 'a', 'public', NULL, NULL),
                     ('c-unlisted', 'MEP1S1', '00000000-0000-0000-0000-000000000002', NULL, 'b', 'unlisted', NULL, NULL),
                     ('c-hidden', 'MEP1S1', '00000000-0000-0000-0000-000000000003', NULL, 'c', 'public', NULL, now()),
                     ('c-deleted', 'MEP1S1', '00000000-0000-0000-0000-000000000004', NULL, 'd', 'public', now(), NULL),
                     ('c-followers', 'MEP1S1', NULL, 'someone@example.social', 'e', 'followers', NULL, NULL),
                     ('c-direct', 'MEP1S1', NULL, 'someone@example.social', 'f', 'direct', NULL, NULL),
                     ('c-other', 'MEP2S1', '00000000-0000-0000-0000-000000000001', NULL, 'g', 'public', NULL, NULL),
                     ('c-dub', 'MEP1SD', NULL, 'someone@example.social', 'h', 'public', NULL, NULL)"""

        let! all = knownEpisodes db
        let! facts = seasonFacts db
        let! html = seriesPage app "en" "MULTI001"
        assertSeasons all facts "MULTI001" html

        let counts =
            outline html
            |> List.collect _.Episodes
            |> List.map (fun e -> attr "episode" e, attr "comments" e)
            |> Map.ofList

        // per episode: the dub's own comment is not added to its original's
        Assert.Equal<(string * string) list>(
            [ "MEP1S1", "3"; "MEP1S2", "0"; "MEP1S3", "0"; "MEP1SD", "1"; "MEP2S1", "1" ],
            counts |> Map.toList
        )

        // the visible label says how many, in both languages, and an empty episode says nothing
        Assert.Contains(Texts.en.SeriesCommentsLabel 3, html)
        Assert.Contains(Texts.en.SeriesCommentsLabel 1, html)
        Assert.DoesNotContain(Texts.en.SeriesCommentsLabel 0, html)
        let! es = seriesPage app "es" "MULTI001"
        Assert.Contains("3 comentarios", es)
        Assert.Contains("1 comentario", es)
    }

[<Fact>]
let ``people counts show only at or above the minimum, all time and in the last 7 days, and name nobody`` () =
    task {
        let! db = newDatabase ()
        do! insertUsers db 4

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES ('PEOPLE01', 'Counted', NULL, now());
                   INSERT INTO cr_episodes (id, series_id, series_title, episode_number, duration_ms) VALUES
                     ('PPL1', 'PEOPLE01', 'Counted', 1, 1440000), ('PPL2', 'PEOPLE01', 'Counted', 2, 1440000);
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('PPLU', 'PEOPLE01', 'Counted', NULL)"""

        // user 1 twice and inside the week, 2 inside, 3 and 4 outside; an unresolved episode counts for nobody
        do! addWatch db 1 "PPL1" (now.AddDays -1.0)
        do! addWatch db 1 "PPL2" (now.AddDays -2.0)
        do! addWatch db 2 "PPL1" (now.AddDays -3.0)
        do! addWatch db 3 "PPL1" (now.AddDays -10.0)
        do! addWatch db 4 "PPL2" (now.AddDays -30.0)
        do! addWatch db 4 "PPLU" (now.AddDays -1.0)

        let! all = Db.scalar<int64> db "SELECT count(DISTINCT w.user_id) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL" []

        let! week =
            Db.scalar<int64>
                db
                "SELECT count(DISTINCT w.user_id) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL AND w.watched_at >= @from"
                [ "from", box (now.AddDays(-7.0).UtcDateTime) ]

        Assert.Equal(4L, all)
        Assert.Equal(2L, week)

        for minimum, expectedAll, expectedWeek in [ 2, Some "4", Some "2"; 3, Some "4", None; 4, Some "4", None; 5, None, None ] do
            use! app = appAt db [ "Frunchy:Charts:MinPeople", string minimum ] now
            let! html = seriesPage app "en" "PEOPLE01"
            Assert.Equal(expectedAll, peopleAttr "people-count" html)
            Assert.Equal(expectedWeek, peopleAttr "people-week" html)

            if expectedAll.IsSome then
                Assert.Contains("Watched by 4 people on Frunchy", html)

            if expectedWeek.IsSome then
                Assert.Contains("Watched by 2 people this week", html)

            // no person is named
            Assert.DoesNotContain("person1", html)
            Assert.DoesNotContain("Person Name", html)
            Assert.DoesNotContain("/u/", html)

        // Spanish says it in Spanish
        use! app = appAt db [ "Frunchy:Charts:MinPeople", "2" ] now
        let! es = seriesPage app "es" "PEOPLE01"
        Assert.Contains("Visto por 4 personas en Frunchy", es)
        Assert.Contains("Visto por 2 personas esta semana", es)
    }

[<Fact>]
let ``malformed, unknown and episode-less series are the plain not-found page`` () =
    task {
        let! db = newDatabase ()

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES ('UNRES001', 'Unresolved', NULL, now());
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('UNRE1', 'UNRES001', 'Unresolved', NULL)"""

        use! app = appAt db [] now

        for language in [ "en"; "es" ] do
            let t = if language = "es" then Texts.es else Texts.en

            for id in [ "SHORT"; "lowercase1"; "ABC-DEF-GHI"; "ABCDEFGHIJKLMNOPQRSTUV"; "UNKNOWN01"; "UNRES001"; "ABC%27DEF1" ] do
                let! status, html = get app language $"/anime/{id}"
                Assert.Equal(HttpStatusCode.NotFound, status)
                Assert.Contains(t.NotFoundTitle, html)
                Assert.DoesNotContain("data-series-page", html)
    }

[<Fact>]
let ``a movie without a series row, seasonless and numberless episodes read as expected`` () =
    task {
        let! db = newDatabase ()

        // no cr_series row: the title comes from the episodes (the least, so it is deterministic), and no Crunchyroll link
        do!
            Db.execute
                db
                """INSERT INTO cr_episodes (id, series_id, season_id, series_title, title, episode_number, episode_label, duration_ms) VALUES
                     ('ODDM0001', 'ODD00001', NULL, 'Odd Show', 'The Movie', NULL, 'Movie', 6000000),
                     ('ODDM0002', 'ODD00001', NULL, 'Odd Show', 'Part Two', 2, NULL, 1500000),
                     ('ODDM0003', 'ODD00001', NULL, 'Odd Show', NULL, NULL, NULL, 900000),
                     ('ODDM0004', 'ODD00001', NULL, 'Odd Show', 'Part One', 1, NULL, 1500000),
                     ('ODDM0005', 'ODD00001', NULL, 'Odd Show', 'Unknown length', 3, NULL, NULL)"""

        use! app = appAt db [] now
        let! all = knownEpisodes db
        let! facts = seasonFacts db
        let! html = seriesPage app "en" "ODD00001"
        assertSeasons all facts "ODD00001" html

        Assert.Equal("Odd Show", title html)
        Assert.DoesNotContain("data-cr-series-link", html)

        match outline html with
        | [ season ] ->
            Assert.Equal("other", attr "season" season.Attrs)
            Assert.Equal("4", attr "known" season.Attrs)
            // numbered ones first, the numberless ones last: the titled one, then the untitled one
            Assert.Equal<string list>([ "ODDM0004"; "ODDM0002"; "ODDM0001"; "ODDM0003" ], season.Episodes |> List.map (attr "episode"))
        | other -> failwith $"expected only the other-episodes block, found {other.Length} seasons"

        Assert.Contains(Texts.en.AnimeOtherEpisodes, html)
        Assert.Contains("Movie", html)
        Assert.Contains(Texts.en.SpecialLabel, html)
        Assert.Contains(Texts.en.EpisodeLabel 1, html)
        Assert.DoesNotContain("Unknown length", html)
        Assert.DoesNotContain("data-mal-missing", html)
    }

/// A series with `count` known episodes in each of the seasons named (`seasonId`, count), numbered from 1
let private bigDb (seasons: (string * int) list) =
    task {
        let! db = newDatabase ()

        do!
            Db.execute
                db
                "INSERT INTO cr_series (id, title, slug, source_at) VALUES ('BIG00001', 'Big Show', 'big-show', now())"

        for index, (season, count) in List.indexed seasons do
            do!
                Db.execute
                    db
                    $"""INSERT INTO cr_seasons (id, series_id, season_number, title, audio_locale, source_at)
                        VALUES ('{season}', 'BIG00001', {index + 1}, 'Part {index + 1}', 'ja-JP', now());
                        INSERT INTO cr_episodes (id, series_id, season_id, series_title, title, episode_number, duration_ms)
                        SELECT '{season}E' || n, 'BIG00001', '{season}', 'Big Show', 'Episode title ' || n, n, 1440000
                        FROM generate_series(1, {count}) AS n"""

        return db
    }

[<Fact>]
let ``a season with more than 30 known episodes starts collapsed, one with 30 or fewer is open`` () =
    task {
        let! db = bigDb [ "BSEASONA", 31; "BSEASONB", 30; "BSEASONC", 1 ]
        use! app = appAt db [] now
        let! all = knownEpisodes db
        let! facts = seasonFacts db

        for language in [ "en"; "es" ] do
            let t = if language = "es" then Texts.es else Texts.en
            let! html = seriesPage app language "BIG00001"
            assertSeasons all facts "BIG00001" html

            let seasons = outline html |> List.map (fun s -> attr "season" s.Attrs, s.Collapsed, s.Episodes.Length)
            Assert.Equal<(string * bool * int) list>([ "BSEASONA", true, 31; "BSEASONB", false, 30; "BSEASONC", false, 1 ], seasons)

            // a native <details>, closed, whose summary gives the count; no script, no open attribute
            let details = Regex.Matches(html, "<details\\b[^>]*>")
            Assert.Equal(1, details.Count)
            Assert.DoesNotContain(" open", details[0].Value)
            Assert.Matches("<summary\\b[^>]*>\\s*" + Regex.Escape(t.SeriesKnownEpisodes 31) + "\\s*</summary>", html)

            // the open seasons say their count in text, and the summary is the collapsed one's only count
            Assert.Contains(t.SeriesKnownEpisodes 30, html)
            Assert.Contains(t.SeriesKnownEpisodes 1, html)
            Assert.Equal(1, Regex.Matches(html, Regex.Escape(t.SeriesKnownEpisodes 31)).Count)

            // the episodes of the collapsed season are inside the details
            let detailsHtml = Regex.Match(html, "<details\\b.*?</details>", RegexOptions.Singleline).Value
            Assert.Equal(31, Regex.Matches(detailsHtml, "data-episode=\"").Count)
    }

[<Fact>]
let ``a series of 1,100 episodes is ready in under a second`` () =
    task {
        let! db = bigDb [ "LSEASON1", 1000; "LSEASON2", 100 ]

        // a comment on every episode, so the counting is part of the work
        do!
            Db.execute
                db
                """INSERT INTO comments (id, cr_episode_id, remote_actor, content, audience)
                   SELECT 'c' || e.id, e.id, 'someone@example.social', 'x', 'public' FROM cr_episodes e"""

        use! app = appAt db [] now
        let! all = knownEpisodes db
        let! facts = seasonFacts db
        Assert.Equal(1100, all.Length)

        // the first request warms the host up; the second is what a reader waits for
        let! first = seriesPage app "en" "BIG00001"
        let clock = Stopwatch.StartNew()
        let! html = seriesPage app "en" "BIG00001"
        let elapsed = clock.Elapsed

        assertSeasons all facts "BIG00001" html
        Assert.True(elapsed < TimeSpan.FromSeconds 1.0, $"1,100 episodes took {elapsed.TotalMilliseconds:N0} ms")
        Assert.Equal(first, html)
        Console.Error.WriteLine $"series page, 1,100 episodes: {elapsed.TotalMilliseconds:N0} ms"
    }
