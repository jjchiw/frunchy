/// Feature 021: the Members level and the new default. Every route under /u/{username} opens for anyone signed in,
/// sends a signed-out visitor to sign in and back, and a Private profile still answers exactly like an unknown
/// username. Tests read `data-*` hooks and responses only.
module FrunchyBackend.Tests.MembersProfileTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Operations
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.AuthHelpers

let private allPaths (username: string) (seriesId: string) =
    openPaths username seriesId @ [ $"/u/{username}/compare" ]

let private locationOf (response: Net.Http.HttpResponseMessage) = string response.Headers.Location

let private signInFor (path: string) = $"/signin?returnUrl={Uri.EscapeDataString path}"

/// The response body without the per-request antiforgery token
let private body (browser: Browser) (path: string) : Task<HttpStatusCode * string> =
    task {
        use! response = browser.GetRaw path
        let! text = response.Content.ReadAsStringAsync()
        // ... and the footer language link's returnUrl, which is the address asked for
        let stripped = Regex.Replace(Regex.Replace(text, "(__RequestVerificationToken\"[^>]*value=\")[^\"]*(\")", "$1$2"), "(/language/(en|es)\\?returnUrl=)[^\"]*", "$1")
        return response.StatusCode, stripped
    }

// ---------------------------------------------------------------------------
// US1: members by default
// ---------------------------------------------------------------------------

[<Fact>]
let ``by default a signed-in user, an operator included, opens every page of a profile`` () =
    task {
        // a blank setting is what an instance without it reads
        use! w = world [ defaultKey, "" ]

        for browser, who in [ w.Other, "other user"; w.Root, "root operator" ] do
            for path in openPaths "animenerd" w.SeriesId do
                let! page = browser.Get path
                Assert.True((page.Status = HttpStatusCode.OK), $"{who} {path}: {page.Status}")
                Assert.True((page.Path = path || path = "/u/animenerd/year"), $"{who} {path} ended on {page.Path}")

            let! compare = browser.Get "/u/animenerd/compare"
            Assert.Equal(HttpStatusCode.OK, compare.Status)
    }

[<Fact>]
let ``a signed-out visitor to a members profile is sent to sign in, with the requested path`` () =
    task {
        use! w = world [ defaultKey, "" ]

        for path in allPaths "animenerd" w.SeriesId do
            use! response = w.Stranger.GetRaw path
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            Assert.Equal(signInFor path, locationOf response)
    }

[<Fact>]
let ``the returnUrl keeps the query string, and signing in lands on the page`` () =
    task {
        use! w = world [ defaultKey, "members" ]
        let path = "/u/animenerd/year/2022?from=email&x=1"

        use! response = w.Stranger.GetRaw path
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/signin?returnUrl=%2Fu%2Fanimenerd%2Fyear%2F2022%3Ffrom%3Demail%26x%3D1", locationOf response)

        // the fake provider's round trip honours a local returnUrl: an existing member lands on the page
        let! landing = w.Stranger.Get $"/signin/test?sub=sub-other&returnUrl={Uri.EscapeDataString path}"
        Assert.Equal(HttpStatusCode.OK, landing.Status)
        Assert.Equal(path, landing.Path)
        Assert.Contains("data-copy-link", landing.Html)
    }

[<Fact>]
let ``a signed-in user is never redirected from a members profile`` () =
    task {
        use! w = world [ defaultKey, "members" ]

        for path in allPaths "animenerd" w.SeriesId do
            use! response = w.Other.GetRaw path
            // /year goes on to the newest year; nothing ever goes to sign-in
            Assert.True((response.StatusCode = HttpStatusCode.OK || path.EndsWith "/year"), $"{path}: {response.StatusCode}")
            Assert.DoesNotContain("/signin", string response.Headers.Location)
    }

[<Fact>]
let ``a private profile still answers like an unknown username, signed out included`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        // the same body as a username that doesn't exist, and never a redirect
        let routes =
            [ "/u/{0}"; "/u/{0}/dna"; "/u/{0}/year"; "/u/{0}/year/2022"; "/u/{0}/anime/" + w.SeriesId ]

        for browser in [ w.Stranger; w.Other; w.Root ] do
            for route in routes do
                let! hiddenStatus, hidden = body browser (route.Replace("{0}", "animenerd"))
                let! unknownStatus, unknown = body browser (route.Replace("{0}", "nobodyhere"))
                Assert.Equal(HttpStatusCode.NotFound, hiddenStatus)
                Assert.Equal(unknownStatus, hiddenStatus)
                Assert.Equal(unknown, hidden)

        // a signed-in viewer's compare is the same 404 as for an unknown name
        let! hiddenStatus, hidden = body w.Other "/u/animenerd/compare"
        let! unknownStatus, unknown = body w.Other "/u/nobodyhere/compare"
        Assert.Equal(unknownStatus, hiddenStatus)
        Assert.Equal(unknown, hidden)
    }

[<Fact>]
let ``a share link opens a members profile for a signed-out browser`` () =
    task {
        use! w = world [ defaultKey, "members" ]
        let! path, _ = createShareLink w.Owner

        use friend = w.App.NewBrowser()
        use! before = friend.GetRaw "/u/animenerd"
        Assert.Equal(HttpStatusCode.Redirect, before.StatusCode)

        use! opened = friend.GetRaw path
        Assert.Equal(HttpStatusCode.SeeOther, opened.StatusCode)

        for p in openPaths "animenerd" w.SeriesId do
            let! page = friend.Get p
            Assert.True((page.Status = HttpStatusCode.OK), $"{p}: {page.Status}")
    }

[<Fact>]
let ``a choice outlives a change of the default`` () =
    task {
        // the default is members, but animenerd chose private; another instance default doesn't change that
        use! w = world [ defaultKey, "members" ]
        let! saved = setVisibility w.Owner "private"
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode)

        let! hidden = statusOf w.Other "/u/animenerd"
        Assert.Equal(HttpStatusCode.NotFound, hidden)

        // the same database under a public default: the choice still holds
        use! again = createAppWith w.App.Db [ defaultKey, "public" ] ignore
        use other = again.NewBrowser()
        let! _ = signInWith other "sub-other"
        let! stillHidden = statusOf other "/u/animenerd"
        Assert.Equal(HttpStatusCode.NotFound, stillHidden)
    }

[<Fact>]
let ``the setting accepts members, private and public, and a stranger value stops startup`` () =
    task {
        for value in [ "members"; "MEMBERS"; " private "; "public" ] do
            let! db = newDatabase ()
            use! _ = createAppWith db [ defaultKey, value ] ignore
            ()

        Assert.Equal(Profiles.Members, Profiles.parseDefault "")
        Assert.Equal(Profiles.Members, Profiles.parseDefault null)
        Assert.Equal(Profiles.Members, Profiles.parseDefault "  ")

        let! db = newDatabase ()

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                task {
                    use! _ = createAppWith db [ defaultKey, "friends" ] ignore
                    ()
                }
                :> Task)

        Assert.Contains(defaultKey, ex.Message)
        Assert.Contains("members", ex.Message)
    }

// ---------------------------------------------------------------------------
// US2: choosing among three
// ---------------------------------------------------------------------------

[<Fact>]
let ``the account page offers public, members and private, in that order`` () =
    task {
        use! w = world [ defaultKey, "" ]
        let! page = w.Owner.Get "/account"

        let values =
            [ for m in Regex.Matches(page.Html, "name=\"visibility\"[^>]*value=\"([a-z]+)\"|value=\"([a-z]+)\"[^>]*name=\"visibility\"") ->
                  if m.Groups[1].Success then m.Groups[1].Value else m.Groups[2].Value ]

        Assert.Equal<string list>([ "public"; "members"; "private" ], values)
        Assert.Equal("members", hookValue "visibility" page.Html)
        Assert.Equal("default", hookValue "visibility-source" page.Html)
    }

[<Fact>]
let ``choosing members is stored, recorded, and shown`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! saved = setVisibility w.Owner "members"
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode)
        Assert.Equal("/account?msg=visibility-saved#visibility", locationOf saved)

        let! stored =
            Db.scalar<string> w.App.Db "SELECT visibility FROM profile_settings p JOIN users u ON u.id = p.user_id WHERE u.username = 'animenerd'" []

        Assert.Equal("members", stored)

        let! events =
            Db.scalar<int64>
                w.App.Db
                "SELECT count(*) FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = 'animenerd' AND e.event = 'profile_made_members'"
                []

        Assert.Equal(1L, events)
        let! visibility = accountHook w.Owner "visibility"
        Assert.Equal("members", visibility)
        let! source = accountHook w.Owner "visibility-source"
        Assert.Equal("choice", source)

        // and it now opens for another member under a private default
        let! status = statusOf w.Other "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, status)
    }

[<Fact>]
let ``a value other than the three changes nothing`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        for bad in [ "everyone"; ""; "Members "; "friends" ] do
            let! response = setVisibility w.Owner bad

            // "Members " is trimmed and then still not one of the three lowercase words
            Assert.Equal("/account?msg=visibility-invalid#visibility", locationOf response)

        let! rows = Db.count w.App.Db "profile_settings"
        Assert.Equal(0L, rows)
    }

// ---------------------------------------------------------------------------
// US3: pages say who can see them
// ---------------------------------------------------------------------------

[<Fact>]
let ``the owner of a members profile sees the members note and the copy link, others only the copy link`` () =
    task {
        use! w = world [ defaultKey, "members" ]

        for path in allPaths "animenerd" w.SeriesId do
            let! page = w.Owner.Get path
            Assert.True((page.Status = HttpStatusCode.OK), $"{path}: {page.Status}")
            Assert.Contains("data-profile-members", page.Html)
            Assert.Contains("/account#visibility", page.Html)
            Assert.DoesNotContain("data-profile-private", page.Html)

        let! own = w.Owner.Get "/u/animenerd"
        Assert.Contains("data-copy-link", own.Html)

        for path in openPaths "animenerd" w.SeriesId do
            let! page = w.Other.Get path
            Assert.DoesNotContain("data-profile-members", page.Html)
            Assert.Contains("data-copy-link", page.Html)
            // the public-profile line is for Public only
            Assert.DoesNotContain("anyone with the link can see these numbers", page.Html)
    }

[<Fact>]
let ``the home note follows the instance default in both languages`` () =
    task {
        let check (value: string) (en: string) (es: string) =
            task {
                let! db = newDatabase ()
                use! app = createAppWith db [ defaultKey, value ] ignore
                let! response = getWithLanguage app.Client "en" "/"
                let! html = readHtml response
                Assert.Contains(en, html)
                let! response = getWithLanguage app.Client "es" "/"
                let! html = readHtml response
                Assert.Contains(es, html)
            }

        do! check "" "Profiles are for members" "Los perfiles son para miembros"
        do! check "members" "Profiles are for members" "Los perfiles son para miembros"
        do! check "private" "Profiles are private" "Los perfiles son privados"
        do! check "public" "Profiles are public unless" "Los perfiles son públicos"
    }

[<Fact>]
let ``user-list shows members, starred when it comes from the default`` () =
    task {
        use! w = world [ defaultKey, "" ]

        do!
            Db.execute
                w.App.Db
                "INSERT INTO profile_settings (user_id, visibility) SELECT id, 'members' FROM users WHERE username = 'other'"

        let settings = Profiles.ProfileSettings Profiles.Members
        let! rows = ListUsers.listUsers w.App.Db settings
        let byName name = rows |> List.find (fun r -> r.Username = name)

        Assert.Equal(Profiles.Members, (byName "animenerd").Visibility)
        Assert.True((byName "animenerd").FromDefault)
        Assert.Equal(Profiles.Members, (byName "other").Visibility)
        Assert.False((byName "other").FromDefault)
        Assert.Equal("members", Profiles.Visibility.toDb Profiles.Members)
    }

[<Fact>]
let ``a members author is linked for a signed-in reader and plain text for a signed-out one`` () =
    task {
        use! w = world [ defaultKey, "members" ]
        let! ep = pickEpisode w.App.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL"
        let! response = postComment w.Owner ep.Series ep.Episode "hello from a members profile" false
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        let page = episodeUrl ep.Series ep.Episode

        let authorTag (html: string) =
            Regex.Match(html, "<[a-zA-Z0-9]+\\b[^>]*\\bdata-comment-author\\b[^>]*>").Value

        let! signedIn = w.Other.Get page
        Assert.Contains("href=\"/u/animenerd\"", authorTag signedIn.Html)

        let! signedOut = w.Stranger.Get page
        Assert.Equal(HttpStatusCode.OK, signedOut.Status)
        Assert.DoesNotContain("href=", authorTag signedOut.Html)
        Assert.DoesNotContain("/u/animenerd", signedOut.Html)
    }

// ---------------------------------------------------------------------------
// The pure rule
// ---------------------------------------------------------------------------

[<Fact>]
let ``canView lets any signed-in viewer see a members profile and nobody else`` () =
    let owner = Guid.NewGuid()
    let linkId = Guid.NewGuid()

    let access visibility =
        { Profiles.UserId = owner
          Profiles.Choice = Some visibility
          Profiles.Visibility = visibility
          Profiles.LinkId = Some linkId }

    let members = access Profiles.Members
    Assert.True(Profiles.canView (Some(Guid.NewGuid())) [] members)
    Assert.True(Profiles.canView (Some owner) [] members)
    Assert.False(Profiles.canView None [] members)
    // a grant for the current link opens it signed out; an old link's grant does not
    Assert.True(Profiles.canView None [ { UserId = owner; LinkId = linkId; At = 0L } ] members)
    Assert.False(Profiles.canView None [ { UserId = owner; LinkId = Guid.NewGuid(); At = 0L } ] members)

    // private is never opened by being signed in
    let hidden = access Profiles.Private
    Assert.False(Profiles.canView (Some(Guid.NewGuid())) [] hidden)
    Assert.True(Profiles.canView (Some owner) [] hidden)
    Assert.True(Profiles.canView None [] (access Profiles.Public))
