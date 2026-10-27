/// Feature 017 (T014, US2): the owner chooses public or private on /account, and the choice outranks the instance
/// default. Tests read the `data-*` hooks only.
module FrunchyBackend.Tests.ProfileVisibilityTests

open System.Net
open Xunit
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private eventCount (w: World) (name: string) =
    Db.scalar<int64>
        w.App.Db
        "SELECT count(*) FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = 'animegolden' AND e.event = @event"
        [ "event", box name ]

let private rowCount (w: World) =
    Db.scalar<int64> w.App.Db "SELECT count(*) FROM profile_settings" []

[<Fact>]
let ``the account page shows the effective state and where it comes from`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! page = w.Owner.Get "/account"
        Assert.Equal("private", hookValue "visibility" page.Html)
        Assert.Equal("default", hookValue "visibility-source" page.Html)
        Assert.Equal("none", hookValue "share-link-state" page.Html)
        Assert.Contains("id=\"visibility\"", page.Html)

        // choosing makes it the owner's own
        let! response = setVisibility w.Owner "public"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/account?msg=visibility-saved#visibility", string response.Headers.Location)

        let! after = w.Owner.Get "/account?msg=visibility-saved"
        Assert.Equal("public", hookValue "visibility" after.Html)
        Assert.Equal("choice", hookValue "visibility-source" after.Html)
        Assert.Contains("data-account-notice=\"visibility-saved\"", after.Html)

        // a public default reads public, from the default
        use! v = world [ defaultKey, "public" ]
        let! page = v.Owner.Get "/account"
        Assert.Equal("public", hookValue "visibility" page.Html)
        Assert.Equal("default", hookValue "visibility-source" page.Html)
    }

[<Fact>]
let ``choosing public opens the profile to strangers and choosing private hides it again`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! before = statusOf w.Stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, before)

        let! _ = setVisibility w.Owner "public"

        for path in openPaths "animegolden" w.SeriesId do
            let! status = statusOf w.Stranger path
            // the year redirect answers 302 to a year page when it can see it
            Assert.True((status = HttpStatusCode.OK || status = HttpStatusCode.Redirect), $"public {path}: {status}")

        let! _ = setVisibility w.Owner "private"

        for path in openPaths "animegolden" w.SeriesId do
            let! status = statusOf w.Stranger path
            Assert.True((status = HttpStatusCode.NotFound), $"private again {path}: {status}")

        // the owner still sees it, with the private note
        let! own = w.Owner.Get "/u/animegolden"
        Assert.Contains("data-profile-private", own.Html)
    }

[<Fact>]
let ``the choice outlives a change of the instance default`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! _ = setVisibility w.Owner "private"

        // the same database, now served by an instance whose default is public
        use! other = createAppWith w.App.Db [ defaultKey, "public" ] ignore
        use stranger = other.NewBrowser()
        let! hidden = statusOf stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, hidden)

        // while a user who never chose follows the new default
        do! Db.execute w.App.Db "DELETE FROM profile_settings"
        let! followed = statusOf stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.OK, followed)
    }

[<Fact>]
let ``an invalid value, a missing token and a signed-out post change nothing`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        for bad in [ "friends"; ""; "PUBLIC "; "public,private" ] do
            let! response = setVisibility w.Owner bad
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            Assert.Equal("/account?msg=visibility-invalid#visibility", string response.Headers.Location)

        let! page = w.Owner.Get "/account?msg=visibility-invalid"
        Assert.Contains("data-account-notice=\"visibility-invalid\"", page.Html)

        // no antiforgery token: refused
        let! forged = w.Owner.PostFormWithoutToken("/account/visibility", [ "visibility", "public" ])
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode)

        // signed out: sent to sign in
        let! signedOut = w.Stranger.PostFormWithoutToken("/account/visibility", [ "visibility", "public" ])
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode)
        Assert.StartsWith("/signin", string signedOut.Headers.Location)

        let! rows = rowCount w
        Assert.Equal(0L, rows)
        let! changed = eventCount w "profile_made_public"
        Assert.Equal(0L, changed)
        let! status = statusOf w.Stranger "/u/animegolden"
        Assert.Equal(HttpStatusCode.NotFound, status)
    }

[<Fact>]
let ``each change records a security event and the account page lists it`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! _ = setVisibility w.Owner "public"
        let! _ = setVisibility w.Owner "private"
        let! madePublic = eventCount w "profile_made_public"
        let! madePrivate = eventCount w "profile_made_private"
        let! old = eventCount w "profile_visibility_changed"
        Assert.Equal(1L, madePublic)
        Assert.Equal(1L, madePrivate)
        Assert.Equal(0L, old)

        let! page = w.Owner.Get "/account"
        Assert.Contains("data-security-event=\"profile_made_public\"", page.Html)
        Assert.Contains("data-security-event=\"profile_made_private\"", page.Html)
    }

[<Fact>]
let ``the account page names the level each change set, in English and Spanish`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        let expected =
            [ "public", "profile_made_public", "Profile made public", "Perfil hecho público"
              "members", "profile_made_members", "Profile set to members", "Perfil solo para miembros"
              "private", "profile_made_private", "Profile made private", "Perfil hecho privado" ]

        for level, _, _, _ in expected do
            let! _ = setVisibility w.Owner level
            ()

        // a row from before this change still reads
        let! _ =
            Db.scalar<int64>
                w.App.Db
                "WITH ins AS (INSERT INTO security_events (user_id, event, actor) SELECT id, 'profile_visibility_changed', 'user' FROM users WHERE username = 'animegolden' RETURNING 1) SELECT count(*) FROM ins"
                []

        let! english = w.Owner.Get "/account"
        Assert.Contains("Profile visibility changed", english.Html)

        for _, event, en, _ in expected do
            Assert.Contains($"data-security-event=\"{event}\"", english.Html)
            Assert.Contains(en, english.Html)

        let! _ = w.Owner.Get "/language/es?returnUrl=%2Faccount"
        let! spanish = w.Owner.Get "/account"
        Assert.Contains("Visibilidad del perfil cambiada", spanish.Html)

        for _, _, _, es in expected do
            Assert.Contains(es, spanish.Html)
    }
