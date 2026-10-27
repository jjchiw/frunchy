/// Feature 017 (T015, US3): a share link opens a private profile in the browser that follows it, until the owner
/// replaces or turns the link off. Tests read the `data-*` hooks and the responses only.
module FrunchyBackend.Tests.ShareLinkTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private ok (status: HttpStatusCode) = status = HttpStatusCode.OK || status = HttpStatusCode.Redirect

/// Open a share link in a browser without following the redirect
let private open' (browser: Browser) (path: string) : Task<Net.Http.HttpResponseMessage> = browser.GetRaw path

let private eventCount (w: World) (name: string) =
    Db.scalar<int64>
        w.App.Db
        "SELECT count(*) FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = 'animegolden' AND e.event = @event"
        [ "event", box name ]

let private tokenOf (path: string) = path.Substring "/s/".Length

[<Fact>]
let ``a link opens every page of the profile in the browser that follows it`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, link = createShareLink w.Owner
        Assert.StartsWith("http://localhost/s/frs_", link)

        // a fresh browser sees nothing until it follows the link
        use friend = w.App.NewBrowser()
        let! before = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, before)

        use! response = open' friend path
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/u/animegolden", string response.Headers.Location)
        Assert.Contains("frunchy.shares", friend.CookieNames)

        // the cookie is HttpOnly, SameSite=Lax, with an expiry, and not Secure over http
        let cookie =
            response.Headers.GetValues "Set-Cookie" |> Seq.find (fun c -> c.StartsWith "frunchy.shares=") |> fun c -> c.ToLowerInvariant()

        Assert.Contains("httponly", cookie)
        Assert.Contains("samesite=lax", cookie)
        Assert.Contains("expires=", cookie)
        Assert.DoesNotContain("secure", cookie)
        // the grant never carries the token
        Assert.DoesNotContain(tokenOf path, cookie)

        let! landed = friend.Get path
        Assert.Equal(HttpStatusCode.OK, landed.Status)
        Assert.Equal("/u/animegolden", landed.Path)
        // a link holder gets no private note and no copy-link of an address that opens nothing for others
        Assert.DoesNotContain("data-profile-private", landed.Html)
        Assert.DoesNotContain("data-copy-link", landed.Html)

        for p in openPaths "animegolden" w.SeriesId do
            let! page = friend.Get p
            Assert.True((page.Status = HttpStatusCode.OK), $"{p}: {page.Status}")

        // and anyone else is still locked out
        let! stranger = statusOf w.Stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, stranger)
    }

[<Fact>]
let ``the link is shown once and only its hash is stored`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner
        let token = tokenOf path

        // the account page says there is a link, never what it is
        let! page = w.Owner.Get "/account"
        Assert.Equal("active", hookValue "share-link-state" page.Html)
        Assert.DoesNotContain(token, page.Html)
        Assert.False(hasHook "share-link" page.Html)

        let! hash = Db.scalar<string> w.App.Db "SELECT share_token_hash FROM profile_settings" []
        Assert.Equal(ShareToken.hash token, hash)
        let! stored = Db.scalar<int64> w.App.Db "SELECT count(*) FROM profile_settings WHERE profile_settings::text LIKE '%' || @token || '%'" [ "token", box token ]
        Assert.Equal(0L, stored)

        // the page that shows it is never cached
        let! response = w.Owner.PostForm("/account", "/account/share-link", [])
        Assert.Contains("no-store", string response.Headers.CacheControl)
    }

[<Fact>]
let ``replacing the link ends the old one for every browser, and the new one works`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! oldPath, _ = createShareLink w.Owner

        use friend = w.App.NewBrowser()
        use! _ = open' friend oldPath
        let! allowed = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, allowed)

        let! newPath, _ = createShareLink w.Owner
        Assert.NotEqual<string>(oldPath, newPath)

        // the browser's grant names a link that no longer exists
        for p in openPaths "animegolden" w.SeriesId do
            let! status = statusOf friend p
            Assert.True((status = HttpStatusCode.NotFound), $"after replace {p}: {status}")

        // the old address is dead for a new visitor, and sets nothing
        use late = w.App.NewBrowser()
        use! dead = open' late oldPath
        Assert.Equal(HttpStatusCode.NotFound, dead.StatusCode)
        Assert.False(setsCookie dead)

        // the new one opens it, in the browser that had the old grant too
        use! fresh = open' friend newPath
        Assert.Equal(HttpStatusCode.SeeOther, fresh.StatusCode)
        let! again = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, again)

        let! created = eventCount w "share_link_created"
        let! replaced = eventCount w "share_link_replaced"
        Assert.Equal(1L, created)
        Assert.Equal(1L, replaced)
    }

[<Fact>]
let ``turning the link off ends access and offers a new one`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner

        use friend = w.App.NewBrowser()
        use! _ = open' friend path
        let! allowed = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, allowed)

        let! response = revokeShareLink w.Owner
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/account?msg=share-link-revoked#visibility", string response.Headers.Location)

        let! status = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, status)
        use! dead = open' w.Stranger path
        Assert.Equal(HttpStatusCode.NotFound, dead.StatusCode)

        let! page = w.Owner.Get "/account?msg=share-link-revoked"
        Assert.Equal("none", hookValue "share-link-state" page.Html)
        Assert.Contains("data-account-notice=\"share-link-revoked\"", page.Html)
        Assert.Contains("action=\"/account/share-link\"", page.Html)
        Assert.DoesNotContain("action=\"/account/share-link/revoke\"", page.Html)

        let! revoked = eventCount w "share_link_revoked"
        Assert.Equal(1L, revoked)

        // turning off a link that isn't there records nothing more
        let! _ = revokeShareLink w.Owner
        let! revokedAgain = eventCount w "share_link_revoked"
        Assert.Equal(1L, revokedAgain)

        // and the link columns are empty
        let! left = Db.scalar<int64> w.App.Db "SELECT count(*) FROM profile_settings WHERE share_token_hash IS NOT NULL OR share_link_id IS NOT NULL OR share_link_created_at IS NOT NULL" []
        Assert.Equal(0L, left)
    }

[<Fact>]
let ``a grant for one user opens nothing of another`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner

        use friend = w.App.NewBrowser()
        use! _ = open' friend path

        let! mine = statusOf friend "/u/animegolden"
        let! theirs = statusOf friend "/u/other"
        Assert.Equal(HttpStatusCode.OK, mine)
        Assert.Equal(HttpStatusCode.NotFound, theirs)

        // a second link adds a second grant, and each opens only its own owner
        let! otherPath, _ = createShareLink w.Other
        use! _ = open' friend otherPath
        let! both = statusOf friend "/u/other"
        let! first = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, both)
        Assert.Equal(HttpStatusCode.OK, first)
    }

[<Fact>]
let ``malformed, unknown and replaced tokens get the plain 404 and no cookie`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! replaced, _ = createShareLink w.Owner
        let! _ = createShareLink w.Owner

        let unknown = "/s/" + ShareToken.generate ()
        let paths =
            [ "/s/abc"
              "/s/frs_short"
              "/s/frs_" + String('A', 44)
              "/s/frs_" + String('!', 43)
              "/s/" + (ShareToken.generate ()).Substring 4
              unknown
              replaced ]

        for p in paths do
            use! response = open' w.Stranger p
            Assert.True((response.StatusCode = HttpStatusCode.NotFound), $"{p}: {response.StatusCode}")
            Assert.False(setsCookie response, $"{p} set a cookie")

        Assert.Empty(w.Stranger.CookieNames)
        // it is the same plain 404 page every other unknown address gets
        let! page = w.Stranger.Get unknown
        let! plain = w.Stranger.Get "/nothing-here"
        Assert.Equal(withoutLanguageReturn plain.Html, withoutLanguageReturn page.Html)
    }

[<Fact>]
let ``a tampered or foreign grant cookie is ignored`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner

        use friend = w.App.NewBrowser()
        use! _ = open' friend path
        let! allowed = statusOf friend "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, allowed)

        for garbage in [ "garbage"; "CfDJ8AAAAAAAAAAAAAAAAAAAAAA"; "e30" ] do
            use tampered = w.App.NewBrowser()
            tampered.Cookies.SetCookies(Uri "http://localhost/", $"frunchy.shares={garbage}; path=/")
            let! status = statusOf tampered "/u/animegolden"
            Assert.True((status = HttpStatusCode.NotFound), $"{garbage}: {status}")

        // a cookie from the one grant with one character changed
        let original = friend.Cookies.GetAllCookies() |> Seq.find (fun c -> c.Name = "frunchy.shares")
        let flipped = (if original.Value.EndsWith "A" then "B" else "A")
        use forged = w.App.NewBrowser()
        forged.Cookies.SetCookies(Uri "http://localhost/", $"frunchy.shares={original.Value.Substring(0, original.Value.Length - 1)}{flipped}; path=/")
        let! status = statusOf forged "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, status)
    }

[<Fact>]
let ``the owner opening their own link is just redirected`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner

        use! response = open' w.Owner path
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/u/animegolden", string response.Headers.Location)
        // no grant needed
        Assert.False(setsCookie response)
    }

[<Fact>]
let ``a link and a public profile both work`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner
        let! _ = setVisibility w.Owner "public"

        let! opened = statusOf w.Stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, opened)

        use friend = w.App.NewBrowser()
        use! response = open' friend path
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)

        // the link matters again when the profile goes private
        let! _ = setVisibility w.Owner "private"
        let! holder = statusOf friend "/u/animegolden"
        let! outsider = statusOf w.Stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, holder)
        Assert.Equal(HttpStatusCode.NotFound, outsider)
    }

[<Fact>]
let ``a signed-in user who follows the link sees the whole profile too`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! path, _ = createShareLink w.Owner

        let! before = statusOf w.Other "/u/animegolden/compare"
        Assert.Equal(HttpStatusCode.NotFound, before)

        use! _ = open' w.Other path

        for p in openPaths "animegolden" w.SeriesId @ [ "/u/animegolden/compare" ] do
            let! page = w.Other.Get p
            Assert.True((page.Status = HttpStatusCode.OK), $"{p}: {page.Status}")
    }

[<Fact>]
let ``share requests are rate limited per IP`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let statuses = ResizeArray<int>()

        for _ in 1..31 do
            use! response = open' w.Stranger ("/s/" + ShareToken.generate ())
            statuses.Add(int response.StatusCode)

        Assert.Equal<int list>(List.replicate 30 404 @ [ 429 ], List.ofSeq statuses)
    }

[<Fact>]
let ``creating and turning off a link needs a sign-in and a token`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        for action in [ "/account/share-link"; "/account/share-link/revoke" ] do
            let! forged = w.Owner.PostFormWithoutToken(action, [])
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode)
            let! signedOut = w.Stranger.PostFormWithoutToken(action, [])
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode)
            Assert.StartsWith("/signin", string signedOut.Headers.Location)

        let! rows = Db.scalar<int64> w.App.Db "SELECT count(*) FROM profile_settings" []
        Assert.Equal(0L, rows)
        let! events = Db.scalar<int64> w.App.Db "SELECT count(*) FROM security_events WHERE event LIKE 'share_link_%'" []
        Assert.Equal(0L, events)
    }
