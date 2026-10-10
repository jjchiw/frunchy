module FrunchyBackend.Tests.AnimeLinkTests

open System
open System.Net
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 006, User Story 2: series titles on the dashboard, the DNA page and the year page lead to the anime
// page (contracts/anime-page.md, "Links added to other pages"). The 2026 sample with the feature 003 fixtures.

let private now = DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)

let private withSample () =
    task {
        let! db = newDatabase ()
        let clock = TestClock(Now = now)
        let! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        routeSampleCatalog app.Mal
        let! _ = enrichAll app
        return app
    }

let private page (app: TestApp) (path: string) =
    task {
        let! response = getWithLanguage app.Client "en" path
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        return! readHtml response
    }

type private SeriesLink = { Id: string; Href: string; Text: string }

/// Every series link of a page, in document order
let private seriesLinks (html: string) =
    [ for m in Regex.Matches(html, "<a\\b[^>]*\\bdata-series-link=\"([^\"]*)\"[^>]*>([^<]*)</a>") ->
          { Id = m.Groups[1].Value
            Href = Regex.Match(m.Value, "href=\"([^\"]*)\"").Groups[1].Value
            Text = m.Groups[2].Value } ]

let private attrsOf (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}=\"[^\"]*\"[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ] |> Map.ofList ]

/// The title the DNA and year pages show for a series
let private titleOf (db: DbConfig) (seriesId: string) =
    Db.scalar<string>
        db
        $"SELECT coalesce(max(s.title), min(e.series_title), e.series_id) FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id WHERE e.series_id = '{seriesId}' GROUP BY e.series_id"
        []

/// Each link goes to the user's anime page for its series and the page answers
let private assertLeads (app: TestApp) (links: SeriesLink list) =
    task {
        for l in links do
            Assert.Equal($"/u/animenerd/anime/{l.Id}", l.Href)
            let! response = getWithLanguage app.Client "en" l.Href
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
    }

[<Fact>]
let ``the dashboard's newest episode and every recent card link to their anime page`` () =
    task {
        let! app = withSample ()
        use app = app
        let! html = page app "/u/animenerd"

        // the cards are the six newest watches, in that order (as the dashboard's own rule)
        let! recent = Db.dump app.Db "SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY w.watched_at DESC, e.id LIMIT 6"
        Assert.Equal(6, recent.Length)

        let links = seriesLinks html
        // the header's newest episode first, then one per card
        Assert.Equal<string list>(recent.Head :: recent, links |> List.map _.Id)

        let cards = html.Split("data-recent-item", StringSplitOptions.None) |> Array.skip 1
        Assert.Equal(6, cards.Length)

        for card, id in Array.zip cards (List.toArray recent) do
            Assert.Equal<string list>([ id ], seriesLinks card |> List.map _.Id)

        do! assertLeads app links
    }

[<Fact>]
let ``the DNA page's insignia anime and each genre's top anime link to their anime page`` () =
    task {
        let! app = withSample ()
        use app = app
        let! html = page app "/u/animenerd/dna"

        let insignia = attrsOf "dna-insignia" html |> List.exactlyOne |> Map.find "series"
        let tops = attrsOf "top-series" html |> List.map (Map.find "top-series")
        Assert.NotEmpty tops

        // exactly those: no link on "Others", none on a genre without a top
        let links = seriesLinks html
        Assert.Equal<string list>(insignia :: tops, links |> List.map _.Id)

        for l in links do
            let! expected = titleOf app.Db l.Id
            Assert.Equal(expected, WebUtility.HtmlDecode l.Text)

        for row in attrsOf "genre" html |> List.filter (fun r -> r["genre"] = "others") do
            Assert.False(row.ContainsKey "top-series")

        do! assertLeads app links
    }

[<Fact>]
let ``the year page's top series and each genre's top anime link to their anime page`` () =
    task {
        let! app = withSample ()
        use app = app
        let! html = page app "/u/animenerd/year/2022"

        let top = attrsOf "year-top-series" html |> List.exactlyOne |> Map.find "series"
        let tops = attrsOf "top-series" html |> List.map (Map.find "top-series")
        Assert.NotEmpty tops

        let links = seriesLinks html
        Assert.Equal<string list>(top :: tops, links |> List.map _.Id)

        for l in links do
            let! expected = titleOf app.Db l.Id
            Assert.Equal(expected, WebUtility.HtmlDecode l.Text)

        do! assertLeads app links
    }
