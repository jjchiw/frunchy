/// Feature 017 (T016, US4): everything that points at a profile follows the visibility rule: comment authors,
/// compare and its card, the MyAnimeList review, and the fediverse handle and actor link. Tests read `data-*` hooks.
module FrunchyBackend.Tests.ProfileLinksTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.MalReviewAccessTests
open FrunchyBackend.Tests.ProfileHelpers

// ---------------------------------------------------------------------------
// Comment authors
// ---------------------------------------------------------------------------

/// The opening tag of the first element of a page that carries `data-comment-author`
let private authorTag (html: string) =
    Regex.Match(html, "<[a-zA-Z0-9]+\\b[^>]*\\bdata-comment-author\\b[^>]*>").Value

/// Every author tag of a page, in page order
let private authorTags (html: string) =
    [ for m in Regex.Matches(html, "<[a-zA-Z0-9]+\\b[^>]*\\bdata-comment-author\\b[^>]*>") -> m.Value ]

let private hasHref (tag: string) = Regex.IsMatch(tag, "\\bhref=\"")

/// A world with a private "animenerd" who has commented on a numbered episode, and the episode's page
let private withComment (config: (string * string) list) =
    task {
        let! w = world config
        let! ep = pickEpisode w.App.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL"
        let! response = postComment w.Owner ep.Series ep.Episode "hello from a private profile" false
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        return w, episodeUrl ep.Series ep.Episode
    }

[<Fact>]
let ``a private author is plain text for a stranger, and a link for the owner and a link holder`` () =
    task {
        let! w, page = withComment [ defaultKey, "private" ]
        use _ = w

        // a stranger (signed out) and another signed-in user: the name is there, with no link
        for browser, who in [ w.Stranger, "signed out"; w.Other, "other user"; w.Root, "root operator" ] do
            let! p = browser.Get page
            Assert.Equal(HttpStatusCode.OK, p.Status)
            let tags = authorTags p.Html
            Assert.Equal(1, tags.Length)
            Assert.False(hasHref tags.Head, $"{who} got a link: {tags.Head}")
            Assert.Contains("ANIMENERD", p.Html)
            Assert.DoesNotContain("/u/animenerd", p.Html)

        // the owner follows their own profile
        let! own = w.Owner.Get page
        Assert.Contains("href=\"/u/animenerd\"", authorTag own.Html)

        // a browser that followed the share link does too, signed in or not
        let! path, _ = createShareLink w.Owner
        use friend = w.App.NewBrowser()
        use! _ = friend.GetRaw path
        let! holder = friend.Get page
        Assert.Contains("href=\"/u/animenerd\"", authorTag holder.Html)

        use! _ = w.Other.GetRaw path
        let! otherHolder = w.Other.Get page
        Assert.Contains("href=\"/u/animenerd\"", authorTag otherHolder.Html)

        // until the link is replaced
        let! _ = createShareLink w.Owner
        let! stale = friend.Get page
        Assert.False(hasHref (authorTag stale.Html))
    }

[<Fact>]
let ``a public author is linked for everyone`` () =
    task {
        let! w, page = withComment [ defaultKey, "public" ]
        use _ = w
        let! p = w.Stranger.Get page
        Assert.Contains("href=\"/u/animenerd\"", authorTag p.Html)

        // and plain again once the author goes private
        let! _ = setVisibility w.Owner "private"
        let! hidden = w.Stranger.Get page
        Assert.False(hasHref (authorTag hidden.Html))
    }

// ---------------------------------------------------------------------------
// Compare and its card
// ---------------------------------------------------------------------------

[<Fact>]
let ``compare and the card follow the profile: hidden is a 404, shared works`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        let! hidden = statusOf w.Other "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.NotFound, hidden)

        let! path, _ = createShareLink w.Owner
        use! _ = w.Other.GetRaw path

        let! compare = w.Other.Get "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.OK, compare.Status)

        // the card is on the dashboard of a profile the viewer can see, for a signed-in non-owner only
        let! dashboard = w.Other.Get "/u/animenerd"
        Assert.Contains("data-compat-card", dashboard.Html)
        let! own = w.Owner.Get "/u/animenerd"
        Assert.DoesNotContain("data-compat-card", own.Html)

        // the link ended: the card and the page go with it
        let! _ = createShareLink w.Owner
        let! gone = statusOf w.Other "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.NotFound, gone)
    }

[<Fact>]
let ``a private viewer sees the card and compare on a public profile`` () =
    task {
        use! w = world [ defaultKey, "public" ]
        let! _ = setVisibility w.Other "private"

        // the viewer's own profile is private, and still the comparison shows to them alone
        let! status = statusOf w.Stranger "/u/other"
        Assert.Equal(HttpStatusCode.NotFound, status)

        let! dashboard = w.Other.Get "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, dashboard.Status)
        Assert.Contains("data-compat-card", dashboard.Html)
        let! compare = w.Other.Get "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.OK, compare.Status)
    }

// ---------------------------------------------------------------------------
// The MyAnimeList review
// ---------------------------------------------------------------------------

[<Fact>]
let ``the MyAnimeList review links seasons to series pages, never into a history`` () =
    task {
        use! env = newReviewEnvWith [ defaultKey, "private"; "Frunchy:RootOperator", "boss" ] [] true

        for path in [ "/review/mal?status=ambiguous"; "/review/mal"; "/review/mal/manual" ] do
            let! page = env.Boss.Get path
            Assert.Equal(HttpStatusCode.OK, page.Status)
            // animenerd is the only person with history; nothing points at them
            Assert.DoesNotContain("/u/animenerd", page.Html)

        let! page = env.Boss.Get "/review/mal?status=ambiguous"
        Assert.Contains($"href=\"/anime/{slimeSeries}\"", page.Html)
        Assert.Contains("data-series-page-link", page.Html)
        Assert.DoesNotContain("data-series-link", page.Html)
    }

// ---------------------------------------------------------------------------
// Federation on
// ---------------------------------------------------------------------------

let private linkHeader (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Link" with
    | true, values -> Some(String.Join(",", values))
    | _ -> None

[<Fact>]
let ``with federation on a stranger gets the 404 and no Link header, and the owner and a link holder keep the handle`` () =
    task {
        use! w = world (FakeMegafono.federationSettings @ [ defaultKey, "private" ])

        // strangers: the plain unknown-user 404, nothing advertised
        for browser in [ w.Stranger; w.Other; w.Root ] do
            use! response = browser.GetRaw "/u/animenerd"
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
            Assert.Equal(None, linkHeader response)
            let! html = response.Content.ReadAsStringAsync()
            Assert.DoesNotContain("data-fediverse-handle", html)
            Assert.DoesNotContain("activity+json", html)

        // the owner's page still advertises the actor and shows the handle
        use! own = w.Owner.GetRaw "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, own.StatusCode)
        Assert.True((linkHeader own).IsSome)
        Assert.Contains("activity+json", (linkHeader own).Value)
        let! ownHtml = own.Content.ReadAsStringAsync()
        Assert.Contains("data-fediverse-handle=\"@animenerd@frunchy.test\"", ownHtml)

        // so does a page opened through the share link
        let! path, _ = createShareLink w.Owner
        use friend = w.App.NewBrowser()
        use! _ = friend.GetRaw path
        use! shared = friend.GetRaw "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode)
        Assert.True((linkHeader shared).IsSome)
    }
