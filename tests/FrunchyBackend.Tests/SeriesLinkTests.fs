module FrunchyBackend.Tests.SeriesLinkTests

open System
open System.Net
open System.Text.RegularExpressions
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 013, User Story 2: everything leads to the series page (contracts/series-page.md, research R5).

let private now = DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)

let private userGuid (n: int) = Guid.Parse("00000000-0000-0000-0000-0000000000" + n.ToString "00")

let private addWatch (db: DbConfig) (user: Guid) (episode: string) (at: DateTimeOffset) =
    Db.execute
        db
        $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
            VALUES ('{user}', '{episode}', '{at.UtcDateTime:O}', 1400, true, gen_random_uuid())"""

/// The `<a>` tags of an HTML fragment that carry the hook, as (href, text)
let private linksOf (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<a\\b[^>]*\\bdata-{Regex.Escape hook}(?![-\\w])[^>]*>(.*?)</a>", RegexOptions.Singleline) ->
          Regex.Match(m.Value, "href=\"([^\"]*)\"").Groups[1].Value, Regex.Replace(m.Groups[1].Value, "<[^>]*>", "").Trim() ]

/// The chart's rows by series id: the HTML from one `data-chart-row` to the next
let private rows (html: string) =
    let starts = [ for m in Regex.Matches(html, "data-chart-row=\"([^\"]*)\"") -> m.Groups[1].Value, m.Index ]

    [ for (id, start), next in List.zip starts (List.tail starts @ [ "", html.Length ] |> List.map snd) -> id, html.Substring(start, next - start) ]

let private series =
    [ "LNK00001", "First Show"; "LNK00002", "Second & Show"; "LNK00003", "Third Show" ]

/// Three series, each watched by the two seeded users, so all show on the all-time chart at a minimum of 2
let private chartDb () =
    task {
        let! db = newDatabase ()

        for i in 1..2 do
            do!
                Db.execute
                    db
                    $"INSERT INTO users (id, username, display_name) VALUES ('{userGuid i}', 'seeded{i}', 'Seeded {i}')"

        for id, title in series do
            let title = title.Replace("'", "''")

            do!
                Db.execute
                    db
                    $"""INSERT INTO cr_series (id, title, slug, source_at) VALUES ('{id}', '{title}', 'slug-{id}', now());
                        INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES ('{id}E1', '{id}', '{title}', 1440000)"""

            for i in 1..2 do
                do! addWatch db (userGuid i) $"{id}E1" (now.AddDays -3.0)

        return db
    }

[<Fact>]
let ``a chart row's title links to the series page for everyone, and Your history shows only for a viewer who watched it`` () =
    task {
        let! db = chartDb ()

        let clock = FreshAuthTests.TestClock(Now = now)

        use! app =
            createAppWith db [ "Frunchy:Charts:MinPeople", "2" ] (fun b -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

        let! viewer = signedUp app "sub-viewer" "viewer"
        use viewer = viewer
        let! viewerId = Db.scalar<Guid> db "SELECT id FROM users WHERE username = @u" [ "u", box "viewer" ]

        // the viewer watched the first series only
        do! addWatch db viewerId "LNK00001E1" (now.AddDays -1.0)

        // signed out, in both languages: a title link on every row, no personal link, no person named
        for language in [ "en"; "es" ] do
            let! response = getWithLanguage app.Client language "/charts/all"
            let! anonymous = readHtml response
            let anonymousRows = rows anonymous
            Assert.Equal<string list>(series |> List.map fst |> List.sort, anonymousRows |> List.map fst |> List.sort)

            for id, block in anonymousRows do
                let expectedTitle = series |> List.find (fun (s, _) -> s = id) |> snd
                Assert.Equal<(string * string) list>([ $"/anime/{id}", expectedTitle ], linksOf "series-page-link" block)
                Assert.DoesNotContain("data-chart-mine", block)

            Assert.DoesNotContain("/u/", anonymous)

        // signed in: the same title links, and "Your history" on the watched series only
        let! page = viewer.Get "/charts/all"
        let html = WebUtility.HtmlDecode page.Html
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Equal<string list>(series |> List.map fst |> List.sort, rows html |> List.map fst |> List.sort)

        for id, block in rows html do
            let expectedTitle = series |> List.find (fun (s, _) -> s = id) |> snd
            Assert.Equal<(string * string) list>([ $"/anime/{id}", expectedTitle ], linksOf "series-page-link" block)

            if id = "LNK00001" then
                Assert.Equal<(string * string) list>([ $"/u/viewer/anime/{id}", Texts.en.ChartsYourHistory ], linksOf "chart-mine" block)
            else
                Assert.DoesNotContain("data-chart-mine", block)

        // the title link leads to a page that says what the row says
        let! target = getWithLanguage app.Client "en" "/anime/LNK00002"
        Assert.Equal(HttpStatusCode.OK, target.StatusCode)
        let! targetHtml = readHtml target
        Assert.Contains("data-series-page=\"LNK00002\"", targetHtml)
    }

[<Fact>]
let ``the episode page's series title links to the series page, which opens`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND e.title IS NOT NULL AND coalesce(e.series_title, '') !~ '[&<>\"]'"

        for language in [ "en"; "es" ] do
            let! status, html = episodePage app language ep.Series ep.Episode
            Assert.Equal(HttpStatusCode.OK, status)

            let! seriesTitle =
                Db.scalar<string> app.Db "SELECT coalesce(min(series_title), '') FROM cr_episodes WHERE id = @id" [ "id", box ep.Episode ]

            match linksOf "series-page-link" html with
            | [ href, text ] ->
                Assert.Equal($"/anime/{ep.Series}", href)
                Assert.Equal(seriesTitle, text)
            | other -> failwith $"expected one series page link, found {other.Length}"

        // following it lands on that series' page
        let! response = getWithLanguage app.Client "en" $"/anime/{ep.Series}"
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! page = readHtml response
        Assert.Contains($"data-series-page=\"{ep.Series}\"", page)
        // and the episode is listed there, linking back to its page
        Assert.Contains($"href=\"/anime/{ep.Series}/episode/{ep.Episode}\"", page)
    }

[<Fact>]
let ``a person's anime page links to the series page`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app

        let! series = Db.scalar<string> app.Db "SELECT series_id FROM cr_seasons WHERE id = 'G6MGC3778'" []

        for language in [ "en"; "es" ] do
            let t = if language = "es" then Texts.es else Texts.en
            let! response = getWithLanguage app.Client language $"/u/animegolden/anime/{series}"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! html = readHtml response

            match linksOf "series-page-link" html with
            | [ href, text ] ->
                Assert.Equal($"/anime/{series}", href)
                Assert.Equal(t.SeriesPageLink, text)
            | other -> failwith $"expected one series page link, found {other.Length}"

        let! page = getWithLanguage app.Client "en" $"/anime/{series}"
        Assert.Equal(HttpStatusCode.OK, page.StatusCode)
        let! html = readHtml page
        Assert.Contains($"data-series-page=\"{series}\"", html)
        // the public page names nobody: it never links back to a person
        Assert.DoesNotContain("animegolden", html)
    }
