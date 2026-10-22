/// Feature 022: every page under /u/{username} shares one masthead and one tab bar (Dashboard, Anime DNA, Year in
/// review). Tests read `data-*` hooks and ARIA attributes, never classes.
module FrunchyBackend.Tests.ProfileTabsTests

open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

type private Tab =
    { Href: string
      Name: string
      Current: bool
      Label: string
      /// The three hooks the old buttons carried, now on the tabs
      Hooks: string list }

/// The tab bar's links in page order, or None when the page has no `data-profile-tabs`
let private tabs (html: string) : (string * Tab list) option =
    let nav = Regex.Match(html, "<nav\\b([^>]*\\bdata-profile-tabs\\b[^>]*)>(.*?)</nav>", RegexOptions.Singleline)

    if not nav.Success then
        None
    else
        let label = Regex.Match(nav.Groups[1].Value, "aria-label=\"([^\"]*)\"").Groups[1].Value

        let links =
            [ for m in Regex.Matches(nav.Groups[2].Value, "<a\\b([^>]*)>(.*?)</a>", RegexOptions.Singleline) ->
                  let tag = m.Groups[1].Value

                  { Href = Regex.Match(tag, "href=\"([^\"]*)\"").Groups[1].Value
                    Name = Regex.Match(tag, "data-tab=\"([^\"]*)\"").Groups[1].Value
                    Current = tag.Contains "aria-current=\"page\""
                    Label = m.Groups[2].Value.Trim()
                    Hooks = [ for h in [ "data-dashboard-link"; "data-dna-link"; "data-year-link" ] do if tag.Contains h then h ] } ]

        Some(label, links)

let private h1Count (html: string) = Regex.Matches(html, "<h1\\b").Count

/// The masthead's own markup: from its opening tag to the tab bar
let private masthead (html: string) =
    let start = html.IndexOf "data-profile-masthead"
    let finish = html.IndexOf "data-profile-tabs"
    Assert.True(start >= 0 && finish > start, "masthead before the tab bar")
    html.Substring(start, finish - start)

let private nameTag (html: string) =
    Regex.Match(html, "<[a-z0-9]+\\b[^>]*\\bdata-profile-name\\b[^>]*>").Value

/// The checks every profile page shares: the bar, its three links in order and the right one current
let private assertTabs (page: string) (user: string) (selected: string option) (labels: string list) (ariaLabel: string) (html: string) =
    match tabs html with
    | None -> failwith $"{page}: no tab bar"
    | Some(label, links) ->
        Assert.Equal(ariaLabel, label)
        Assert.Equal<string list>([ "dashboard"; "dna"; "year" ], links |> List.map _.Name)
        Assert.Equal<string list>([ $"/u/{user}"; $"/u/{user}/dna"; $"/u/{user}/year" ], links |> List.map _.Href)
        Assert.Equal<string list>(labels, links |> List.map _.Label)

        // the hooks of the old buttons, one each, on the tab of the same view (the selected one included)
        Assert.Equal<string list list>(
            [ [ "data-dashboard-link" ]; [ "data-dna-link" ]; [ "data-year-link" ] ],
            links |> List.map _.Hooks
        )

        let current = links |> List.filter _.Current |> List.map _.Name
        Assert.Equal<string list>((match selected with Some s -> [ s ] | None -> []), current)

        // nothing but aria-current says "selected": no other page-level marker on the links
        Assert.Equal(1, Regex.Matches(html, "data-profile-tabs").Count)

let private english = [ "Dashboard"; "Anime DNA"; "Year in review" ]
let private spanish = [ "Panel"; "ADN anime"; "Resumen del año" ]

/// A world with a public "animenerd": the owner, another member and a signed-out visitor
let private publicWorld () = world [ defaultKey, "public" ]

[<Fact>]
let ``every profile page has the tab bar with the right tab current and one h1`` () =
    task {
        use! w = publicWorld ()

        let pages =
            [ "dashboard", "/u/animenerd", Some "dashboard"
              "dna", "/u/animenerd/dna", Some "dna"
              "year", "/u/animenerd/year", Some "year"
              "year 2022", "/u/animenerd/year/2022", Some "year"
              "anime", $"/u/animenerd/anime/{w.SeriesId}", None ]

        for browser, who in [ w.Stranger, "visitor"; w.Owner, "owner"; w.Other, "member" ] do
            for name, path, selected in pages do
                let! page = browser.Get path
                Assert.Equal(HttpStatusCode.OK, page.Status)
                assertTabs $"{who} {name}" "animenerd" selected english "Profile pages" page.Html
                Assert.True((h1Count page.Html = 1), $"{who} {name}: {h1Count page.Html} h1")
                Assert.Contains("data-profile-masthead", page.Html)
    }

[<Fact>]
let ``the masthead name is the h1 on the dashboard and a link to it everywhere else`` () =
    task {
        use! w = publicWorld ()

        let! dashboard = w.Stranger.Get "/u/animenerd"
        let tag = nameTag dashboard.Html
        Assert.StartsWith("<h1", tag)
        Assert.DoesNotContain("href", tag)
        Assert.Contains("ANIMENERD", masthead dashboard.Html)
        Assert.Contains("@animenerd", masthead dashboard.Html)

        for path in [ "/u/animenerd/dna"; "/u/animenerd/year"; "/u/animenerd/year/2022"; $"/u/animenerd/anime/{w.SeriesId}" ] do
            let! page = w.Stranger.Get path
            let tag = nameTag page.Html
            Assert.StartsWith("<a", tag)
            Assert.Contains("href=\"/u/animenerd\"", tag)
            Assert.Contains("ANIMENERD", masthead page.Html)
            Assert.Contains("@animenerd", masthead page.Html)
    }

[<Fact>]
let ``the DNA and year headings are the view's own and keep the person's name in their hooks`` () =
    task {
        use! w = publicWorld ()

        let! dna = w.Stranger.Get "/u/animenerd/dna"
        Assert.Matches("<h1\\b[^>]*data-dna-user=\"ANIMENERD\"[^>]*>\\s*Anime DNA\\s*</h1>", dna.Html)

        let! year = w.Stranger.Get "/u/animenerd/year/2022"
        Assert.Matches("<h1\\b[^>]*data-year-user=\"ANIMENERD\"[^>]*>\\s*2022 in review\\s*</h1>", year.Html)
        Assert.Contains("data-year-picker", year.Html)
        Assert.Contains("data-rewatch-note", year.Html)

        let! anime = w.Stranger.Get $"/u/animenerd/anime/{w.SeriesId}"
        Assert.Matches("<h1\\b[^>]*data-anime-title", anime.Html)
        // the old "back to the dashboard" button is gone: the Dashboard tab leads back
        Assert.Equal(1, Regex.Matches(anime.Html, "data-dashboard-link").Count)
    }

[<Fact>]
let ``the copy link and the visibility notes sit in the masthead`` () =
    task {
        use! w = publicWorld ()

        for path in [ "/u/animenerd"; "/u/animenerd/dna"; "/u/animenerd/year/2022" ] do
            let! page = w.Stranger.Get path
            let top = masthead page.Html
            Assert.Contains("data-copy-link", top)
            Assert.Contains($"data-address=\"http://localhost{path}\"", top)

        // the owner's note about a Members profile is in the masthead too, before the copy link
        use! m = world [ defaultKey, "members" ]
        let! own = m.Owner.Get "/u/animenerd/dna"
        let top = masthead own.Html
        Assert.Contains("data-profile-members", top)
        Assert.True((top.IndexOf "data-profile-members" < top.IndexOf "data-copy-link"))

        // a private profile's owner sees the private note instead of the copy link
        use! p = world [ defaultKey, "private" ]
        let! own = p.Owner.Get "/u/animenerd"
        let top = masthead own.Html
        Assert.Contains("data-profile-private", top)
        Assert.DoesNotContain("data-copy-link", top)
    }

[<Fact>]
let ``the labels are in Spanish for a Spanish visitor`` () =
    task {
        use! w = publicWorld ()
        use es = w.App.NewBrowser "es"

        for path, selected in
            [ "/u/animenerd", Some "dashboard"
              "/u/animenerd/dna", Some "dna"
              "/u/animenerd/year/2022", Some "year"
              $"/u/animenerd/anime/{w.SeriesId}", None ] do
            let! page = es.Get path
            Assert.Equal(HttpStatusCode.OK, page.Status)
            assertTabs path "animenerd" selected spanish "Páginas del perfil" page.Html
    }

/// Two members with enough history for a compatibility level, "viewera" looking at "ownerb"
let private comparePair () =
    task {
        let! app = newApp ()
        let! a = signedUp app "sub-viewera" "viewera"
        let! b = signedUp app "sub-ownerb" "ownerb"
        let! keyA = connectExtension a
        let! keyB = connectExtension b
        let! _ = submitAccepted app keyA (envelopeOf (syntheticPages RealAccountId 2 10))
        let! _ = submitAccepted app keyB (envelopeOf (syntheticPages "11111111-2222-3333-4444-555555555555" 2 10))
        let! _ = processAll app.Db
        return app, a
    }

[<Fact>]
let ``the compare page has the masthead without an address and the tab bar with nothing selected`` () =
    task {
        let! app, viewer = comparePair ()
        use _ = app

        let! page = viewer.Get "/u/ownerb/compare"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        assertTabs "compare" "ownerb" None english "Profile pages" page.Html
        Assert.Equal(1, h1Count page.Html)
        Assert.Contains("data-compare-summary", page.Html)

        let top = masthead page.Html
        Assert.Contains("OWNERB", top)
        Assert.Contains("href=\"/u/ownerb\"", nameTag page.Html)
        Assert.DoesNotContain("data-copy-link", top)
        Assert.DoesNotContain("data-profile-private", top)
        Assert.DoesNotContain("data-profile-members", top)
    }

[<Fact>]
let ``a page for a profile that is not enough to compare still has the masthead and the tab bar`` () =
    task {
        use! w = publicWorld ()

        // the member has no history, so the page says there is not enough: the bar is still there
        let! page = w.Other.Get "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        assertTabs "compare, not enough" "animenerd" None english "Profile pages" page.Html
        Assert.Contains("data-compat-not-enough", page.Html)
        // The empty card's title is the page's one h1 (the masthead name is a link here)
        Assert.Equal(1, h1Count page.Html)
    }
