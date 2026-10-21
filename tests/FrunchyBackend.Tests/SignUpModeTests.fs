/// Feature 020: the sign-up mode. The root (and only the root) opens or closes sign-up on /review/settings, the mode
/// is read per request and fails closed to invite-only, sign-up follows it (deciding at submit time, in one
/// transaction either way), and /signin says it's by invitation (a note and a dialog, invite mode only).
module FrunchyBackend.Tests.SignUpModeTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

type private Env =
    { App: TestApp
      Clock: TestClock
      /// The root operator (username "boss"), browsing in the given language
      Boss: Browser
      /// An operator from the table, not the root
      Opr: Browser
      /// Signed in, not an operator
      Pleb: Browser
      Visitor: Browser }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

let private newEnvIn (language: string) (root: string option) : Task<Env> =
    task {
        let clock = TestClock()
        let! db = newDatabase ()

        let config =
            match root with
            | Some name -> [ "Frunchy:RootOperator", name ]
            | None -> []

        let! app = createAppWith db config (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        let boss = app.NewBrowser language
        let! invite = createInvite app
        let! _ = submitSignUp boss "sub-boss" invite "boss" "Boss"
        let! opr = signedUp app "sub-opr" "opr"
        let! pleb = signedUp app "sub-pleb" "pleb"
        let! added = OperatorActions.add app.Db root "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        // the invites the accounts above signed up with aren't the subject of these tests
        do! Db.execute app.Db "DELETE FROM invites"
        return { App = app; Clock = clock; Boss = boss; Opr = opr; Pleb = pleb; Visitor = app.NewBrowser() }
    }

let private newEnv () = newEnvIn "en" (Some "boss")

let private settingsPage = "/review/settings"
let private signUpAction = "/review/settings/signup"

let private body (response: HttpResponseMessage) =
    task {
        let! raw = response.Content.ReadAsStringAsync()
        return WebUtility.HtmlDecode raw
    }

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

/// Set the mode straight in the database (a value the page would never write too)
let private setRaw (app: TestApp) (value: string) =
    Db.execute
        app.Db
        $"INSERT INTO instance_settings (key, value) VALUES ('signup_mode', '{value}') ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value"

let private setMode (app: TestApp) (mode: SignUpMode.SignUpMode) = setRaw app (SignUpMode.toDb mode)

let private clearSetting (app: TestApp) = Db.execute app.Db "DELETE FROM instance_settings"

/// Everything the settings change can touch
let private snapshot (app: TestApp) =
    task {
        let! settings = Db.dump app.Db "SELECT s::text FROM instance_settings s ORDER BY key"
        let! events = Db.dump app.Db "SELECT e::text FROM security_events e ORDER BY id"
        return settings, events
    }

let private modeEvents (app: TestApp) =
    Db.dump
        app.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event = 'signup_mode_changed' ORDER BY e.id"

let private storedMode (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT value FROM instance_settings WHERE key = 'signup_mode'"
        return List.tryHead rows
    }

let private saveAs (browser: Browser) (mode: string) = browser.PostForm(settingsPage, signUpAction, [ "mode", mode ])

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! html = body response
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", html)
        Assert.DoesNotContain("data-settings", html)
        Assert.Null response.Headers.Location
    }

/// Every route and verb answers 404 for this browser, nothing is rate-limited, and nothing changes
let private assertClosedFor (env: Env) (who: string) (browser: Browser) =
    task {
        let! before = snapshot env.App

        let! shown = browser.GetRaw settingsPage
        do! assertNotFound $"{who} GET" shown
        let! withMessage = browser.GetRaw $"{settingsPage}?msg=saved"
        do! assertNotFound $"{who} GET with a notice" withMessage

        let! withToken = browser.PostForm("/account", signUpAction, [ "mode", "open" ])
        do! assertNotFound $"{who} POST with a token" withToken
        let! withoutToken = browser.PostFormWithoutToken(signUpAction, [ "mode", "open" ])
        do! assertNotFound $"{who} POST without a token" withoutToken

        // a 429 would reveal the route
        for i in 1..15 do
            let! response = browser.PostFormWithoutToken(signUpAction, [ "mode", "open" ])
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

// ---------------------------------------------------------------------------
// The root's page
// ---------------------------------------------------------------------------

[<Fact>]
let ``visitors, users and operators who aren't root get 404 on every route and verb; the root gets the page`` () =
    task {
        use! env = newEnv ()
        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "user" env.Pleb
        do! assertClosedFor env "operator who isn't root" env.Opr

        let! page = env.Boss.Get settingsPage
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-settings", page.Html)
        Assert.Contains("data-signup-mode=\"invite\"", page.Html)
        Assert.Contains("data-signup-option=\"invite\"", page.Html)
        Assert.Contains("data-signup-option=\"open\"", page.Html)
        // invite-only is the one selected by default
        Assert.Matches("data-signup-option=\"invite\"\\s+checked", page.Html)
        Assert.DoesNotMatch("data-signup-option=\"open\"\\s+checked", page.Html)
    }

[<Fact>]
let ``with no root configured nobody gets in, so the mode stays invite-only`` () =
    task {
        use! env = newEnvIn "en" None
        do! assertClosedFor env "boss" env.Boss
        do! assertClosedFor env "operator" env.Opr
        do! assertClosedFor env "user" env.Pleb
        do! assertClosedFor env "visitor" env.Visitor

        let! mode = SignUpMode.current env.App.Db
        Assert.Equal(SignUpMode.Invite, mode)
    }

[<Fact>]
let ``the root opens and closes sign-up: the setting, the event on their account, the notice and the page`` () =
    task {
        use! env = newEnv ()

        let! opened = saveAs env.Boss "open"
        Assert.Equal(HttpStatusCode.Found, opened.StatusCode)
        Assert.Equal("/review/settings?msg=saved", locationOf opened)

        let! stored = storedMode env.App
        Assert.Equal(Some "open", stored)
        let! by = Db.scalar<string> env.App.Db "SELECT u.username FROM instance_settings s JOIN users u ON u.id = s.updated_by" []
        Assert.Equal("boss", by)
        let! events = modeEvents env.App
        Assert.Equal<string list>([ "boss signup_mode_changed user" ], events)

        let! page = env.Boss.Get(locationOf opened)
        Assert.Contains("data-settings-notice=\"saved\"", page.Html)
        Assert.Contains("data-signup-mode=\"open\"", page.Html)
        Assert.Matches("data-signup-option=\"open\"\\s+checked", page.Html)
        Assert.DoesNotMatch("data-signup-option=\"invite\"\\s+checked", page.Html)

        // the event shows in the root's own security activity
        let! account = env.Boss.Get "/account"
        Assert.Contains("Sign-up mode changed", account.Html)

        let! closed = saveAs env.Boss "invite"
        Assert.Equal("/review/settings?msg=saved", locationOf closed)
        let! stored = storedMode env.App
        Assert.Equal(Some "invite", stored)
        let! events = modeEvents env.App
        Assert.Equal<string list>([ "boss signup_mode_changed user"; "boss signup_mode_changed user" ], events)
        // one row per key, whatever the number of changes
        let! rows = Db.count env.App.Db "instance_settings"
        Assert.Equal(1L, rows)
    }

[<Fact>]
let ``a bad choice or a missing antiforgery token changes nothing`` () =
    task {
        use! env = newEnv ()
        let! before = snapshot env.App

        // at most 10 POSTs: the root's limit is 10 a minute
        for value in [ ""; "everyone"; "OPEN"; "Open"; "invite,open"; "x'; DROP TABLE instance_settings;--" ] do
            let! response = saveAs env.Boss value
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"'{value}' got {int response.StatusCode}")
            Assert.Equal("/review/settings?msg=bad-input", locationOf response)

        let! noField = env.Boss.PostForm(settingsPage, signUpAction, [])
        Assert.Equal("/review/settings?msg=bad-input", locationOf noField)

        let! page = env.Boss.Get "/review/settings?msg=bad-input"
        Assert.Contains("data-settings-notice=\"bad-input\"", page.Html)
        Assert.DoesNotContain("data-settings-notice=\"saved\"", page.Html)

        let! after = snapshot env.App
        Assert.Equal(before, after)

        // a forged request (no token) is refused by the forgery check, not changed
        let! forged = env.Boss.PostFormWithoutToken(signUpAction, [ "mode", "open" ])
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode)
        let! afterForged = snapshot env.App
        Assert.Equal(before, afterForged)
    }

[<Fact>]
let ``an unknown notice key shows nothing`` () =
    task {
        use! env = newEnv ()
        let! page = env.Boss.Get "/review/settings?msg=whatever"
        Assert.DoesNotContain("data-settings-notice", page.Html)
    }

[<Fact>]
let ``a stale session re-confirms first and changes nothing; non-roots still see 404`` () =
    task {
        use! env = newEnv ()
        // the page itself needs no recent sign-in; the token comes from it
        let! _ = env.Boss.Get settingsPage
        env.Clock.Now <- env.Clock.Now.AddMinutes 11.0
        let! before = snapshot env.App

        let! stale = saveAs env.Boss "open"
        Assert.Equal(HttpStatusCode.Found, stale.StatusCode)
        Assert.Equal("/signin/reconfirm?returnUrl=%2Freview%2Fsettings", locationOf stale)
        let! after = snapshot env.App
        Assert.Equal(before, after)

        // a stale operator who isn't root is not sent anywhere: it's still a 404
        let! outsider = env.Opr.PostForm("/account", signUpAction, [ "mode", "open" ])
        do! assertNotFound "stale non-root" outsider

        // re-confirming brings the root back to the page, where the change now works
        let! back = env.Boss.Get("/signin/reconfirm?returnUrl=%2Freview%2Fsettings&sub=sub-boss")
        Assert.Equal("/review/settings", back.Path)
        let! saved = saveAs env.Boss "open"
        Assert.Equal("/review/settings?msg=saved", locationOf saved)
    }

[<Fact>]
let ``changes are limited to 10 a minute for the root only`` () =
    task {
        use! env = newEnv ()

        for i in 1..10 do
            let! response = saveAs env.Boss "invite"
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"POST {i} got {int response.StatusCode}")

        let! limited = saveAs env.Boss "invite"
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
    }

[<Fact>]
let ``the account page links to the site settings for the root only`` () =
    task {
        use! env = newEnv ()
        let! boss = env.Boss.Get "/account"
        Assert.Contains("data-settings-link", boss.Html)
        Assert.Contains("href=\"/review/settings\"", boss.Html)
        Assert.Contains("data-operators-link", boss.Html)

        // a table operator has the operator links but not this one
        let! opr = env.Opr.Get "/account"
        Assert.Contains("data-invites-link", opr.Html)
        Assert.DoesNotContain("data-settings-link", opr.Html)

        let! pleb = env.Pleb.Get "/account"
        Assert.DoesNotContain("data-settings-link", pleb.Html)
    }

[<Fact>]
let ``the setting and its event commit together or not at all`` () =
    task {
        use! env = newEnv ()
        let! bossId = Db.scalar<Guid> env.App.Db "SELECT id FROM users WHERE username = @u" [ "u", box "boss" ]

        // rolled back: neither the setting nor the event is left behind
        do!
            task {
                use connection = getConnection env.App.Db
                do! connection.OpenAsync()
                use tx = connection.BeginTransaction()
                do! SignUpMode.setIn connection tx bossId SignUpMode.Open
                do! tx.RollbackAsync()
            }

        let! stored = storedMode env.App
        Assert.Equal(None, stored)
        let! events = modeEvents env.App
        Assert.Empty events

        // the event can't be recorded for an unknown account, so the setting isn't written either
        let! threw =
            task {
                try
                    do! SignUpMode.set env.App.Db (Guid.NewGuid()) SignUpMode.Open
                    return false
                with _ ->
                    return true
            }

        Assert.True threw
        let! stored = storedMode env.App
        Assert.Equal(None, stored)
        let! mode = SignUpMode.current env.App.Db
        Assert.Equal(SignUpMode.Invite, mode)

        // and committed together
        do! SignUpMode.set env.App.Db bossId SignUpMode.Open
        let! stored = storedMode env.App
        Assert.Equal(Some "open", stored)
        let! events = modeEvents env.App
        Assert.Equal<string list>([ "boss signup_mode_changed user" ], events)
    }

[<Fact>]
let ``deleting the user who last changed the setting keeps the setting`` () =
    task {
        use! env = newEnv ()
        let! response = saveAs env.Boss "open"
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        do! Db.execute env.App.Db "DELETE FROM users WHERE username = 'boss'"
        let! stored = storedMode env.App
        Assert.Equal(Some "open", stored)
        let! by = Db.scalar<obj> env.App.Db "SELECT updated_by FROM instance_settings" []
        Assert.True(isNull by || by = box DBNull.Value)
    }

// ---------------------------------------------------------------------------
// Fail closed
// ---------------------------------------------------------------------------

[<Fact>]
let ``a missing row and an unknown value both mean invite-only`` () =
    task {
        use! env = newEnv ()
        let! missing = SignUpMode.current env.App.Db
        Assert.Equal(SignUpMode.Invite, missing)

        for value in [ "banana"; "OPEN"; "Open"; " open"; "open "; ""; "true"; "1"; "opened" ] do
            do! setRaw env.App value
            let! mode = SignUpMode.current env.App.Db
            Assert.True((mode = SignUpMode.Invite), $"'{value}' read as {mode}")

            // the pages agree: the dialog and the note show, and sign-up asks for a code
            let! signIn = env.Visitor.Get "/signin"
            Assert.Contains("data-invite-only-dialog", signIn.Html)

            let! page = env.Boss.Get settingsPage
            Assert.Contains("data-signup-mode=\"invite\"", page.Html)

        do! clearSetting env.App
        let! signIn = env.Visitor.Get "/signin"
        Assert.Contains("data-invite-only-note", signIn.Html)
        use strangerBrowser = env.App.NewBrowser()
        let! stranger = strangerBrowser.Get "/signin/test?sub=stranger"
        Assert.Contains("name=\"invite\"", stranger.Html)
    }

[<Fact>]
let ``SignUpMode.parse opens only on the exact word open`` () =
    Assert.Equal(SignUpMode.Open, SignUpMode.parse "open")

    for value in [ "invite"; "Open"; "OPEN"; ""; " open"; "banana"; null ] do
        Assert.Equal(SignUpMode.Invite, SignUpMode.parse value)

    Assert.Equal(Some SignUpMode.Invite, SignUpMode.tryParseChoice "invite")
    Assert.Equal(Some SignUpMode.Open, SignUpMode.tryParseChoice "open")
    Assert.Equal(None, SignUpMode.tryParseChoice "Open")
    Assert.Equal(None, SignUpMode.tryParseChoice "")

[<Fact>]
let ``the mode is read per request, so another copy of the site sees a change at once`` () =
    task {
        use! env = newEnv ()
        use! other = createApp env.App.Db
        use browser = other.NewBrowser()

        let! before = browser.Get "/signin"
        Assert.Contains("data-invite-only-dialog", before.Html)

        let! saved = saveAs env.Boss "open"
        Assert.Equal(HttpStatusCode.Found, saved.StatusCode)

        let! after = browser.Get "/signin"
        Assert.DoesNotContain("data-invite-only-dialog", after.Html)
    }

// ---------------------------------------------------------------------------
// Sign-up follows the mode
// ---------------------------------------------------------------------------

let private invitesDump (app: TestApp) = Db.dump app.Db "SELECT i::text FROM invites i ORDER BY id"

[<Fact>]
let ``in open mode a new identity creates an account with no code and no invite is touched`` () =
    task {
        use! env = newEnv ()
        do! setMode env.App SignUpMode.Open
        // an active invite exists: it must stay exactly as it is
        let! _ = createInvite env.App
        let! invitesBefore = invitesDump env.App
        Assert.Equal(1, invitesBefore.Length)

        use newcomer = env.App.NewBrowser()
        let! response = submitSignUp newcomer "sub-newcomer" "" "newcomer" "Newcomer"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/account", locationOf response)

        let! users = Db.dump env.App.Db "SELECT username FROM users WHERE username = 'newcomer'"
        Assert.Equal<string list>([ "newcomer" ], users)
        let! identity = Db.dump env.App.Db "SELECT provider || ':' || provider_user_id FROM user_identities WHERE provider_user_id = 'sub-newcomer'"
        Assert.Equal<string list>([ "test:sub-newcomer" ], identity)
        let! created = Db.dump env.App.Db "SELECT e.event FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = 'newcomer'"
        Assert.Equal<string list>([ "account_created" ], created)

        let! invitesAfter = invitesDump env.App
        Assert.Equal<string list>(invitesBefore, invitesAfter)
        let! used = Db.scalar<int64> env.App.Db "SELECT count(*) FROM invites WHERE used_at IS NOT NULL OR used_by_user_id IS NOT NULL" []
        Assert.Equal(0L, used)

        // and they are signed in
        let! account = newcomer.Get "/account"
        Assert.Equal(HttpStatusCode.OK, account.Status)
    }

[<Fact>]
let ``in open mode a code that is typed anyway is ignored and not consumed`` () =
    task {
        use! env = newEnv ()
        do! setMode env.App SignUpMode.Open
        let! code = createInvite env.App
        let! invitesBefore = invitesDump env.App

        use first = env.App.NewBrowser()
        let! a = submitSignUp first "sub-a" code "person-a" "A"
        Assert.Equal(HttpStatusCode.Redirect, a.StatusCode)
        use second = env.App.NewBrowser()
        let! b = submitSignUp second "sub-b" "inv_NOTAREALCODE" "person-b" "B"
        Assert.Equal(HttpStatusCode.Redirect, b.StatusCode)

        let! invitesAfter = invitesDump env.App
        Assert.Equal<string list>(invitesBefore, invitesAfter)
    }

[<Fact>]
let ``open mode keeps the username, display name and identity rules, and refusals create nothing`` () =
    task {
        use! env = newEnv ()
        do! setMode env.App SignUpMode.Open
        let! users = Db.count env.App.Db "users"
        let! identities = Db.count env.App.Db "user_identities"
        let! events = Db.count env.App.Db "security_events"

        for username, displayName, expected in
            [ "abc-", "X", "That username isn't allowed"
              "a b", "X", "That username isn't allowed"
              "admin", "X", "That username isn't allowed"
              "pleb", "X", "That username is already taken."
              "fine", "", "The display name must be 1-60 characters."
              "fine", String('x', 61), "The display name must be 1-60 characters." ] do
            use browser = env.App.NewBrowser()
            let! response = submitSignUp browser $"sub-{Guid.NewGuid():N}" "" username displayName
            let! html = body response
            Assert.True(response.StatusCode = HttpStatusCode.BadRequest, $"{username}: {int response.StatusCode}")
            Assert.Contains(expected, html)
            // open mode says nothing about a code, even in a refusal
            Assert.DoesNotContain("name=\"invite\"", html)

        let! usersAfter = Db.count env.App.Db "users"
        let! identitiesAfter = Db.count env.App.Db "user_identities"
        let! eventsAfter = Db.count env.App.Db "security_events"
        Assert.Equal(users, usersAfter)
        Assert.Equal(identities, identitiesAfter)
        Assert.Equal(events, eventsAfter)
    }

[<Fact>]
let ``invite mode still refuses a new identity without a valid code`` () =
    task {
        use! env = newEnv ()
        do! setMode env.App SignUpMode.Invite
        let! users = Db.count env.App.Db "users"

        for code in [ ""; "inv_NOTAREALCODE" ] do
            use browser = env.App.NewBrowser()
            let! response = submitSignUp browser $"sub-{Guid.NewGuid():N}" code "newbie" "Newbie"
            let! html = body response
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
            Assert.Contains("This invite code isn't valid.", html)
            Assert.Contains("name=\"invite\"", html)

        let! after = Db.count env.App.Db "users"
        Assert.Equal(users, after)

        // a valid code still works, and is consumed
        let! code = createInvite env.App
        use browser = env.App.NewBrowser()
        let! ok = submitSignUp browser "sub-ok" code "newbie" "Newbie"
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode)
        let! used = Db.scalar<int64> env.App.Db "SELECT count(*) FROM invites WHERE used_at IS NOT NULL" []
        Assert.Equal(1L, used)
    }

[<Fact>]
let ``the mode at submit time decides, whatever the page showed`` () =
    task {
        use! env = newEnv ()

        // rendered in open mode (no code field), closed before the submit: a code is needed
        do! setMode env.App SignUpMode.Open
        use closing = env.App.NewBrowser()
        let! page = signInWith closing "sub-closing"
        Assert.Equal("/signup", page.Path)
        Assert.DoesNotContain("name=\"invite\"", page.Html)
        do! setMode env.App SignUpMode.Invite
        let! refused = closing.PostForm("/signup", "/signup", [ "username", "closing"; "displayName", "Closing" ])
        let! html = body refused
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode)
        // the refusal is drawn in the mode now in force: it asks for the code
        Assert.Contains("name=\"invite\"", html)
        Assert.Contains("This invite code isn't valid.", html)
        let! none = Db.dump env.App.Db "SELECT username FROM users WHERE username = 'closing'"
        Assert.Empty none

        // rendered in invite mode (code field), opened before the submit: no code is needed
        use opening = env.App.NewBrowser()
        let! page = signInWith opening "sub-opening"
        Assert.Contains("name=\"invite\"", page.Html)
        do! setMode env.App SignUpMode.Open
        let! accepted = opening.PostForm("/signup", "/signup", [ "username", "opening"; "displayName", "Opening" ])
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode)
        let! made = Db.dump env.App.Db "SELECT username FROM users WHERE username = 'opening'"
        Assert.Equal<string list>([ "opening" ], made)
    }

[<Fact>]
let ``the sign-up page has the code field in invite mode and neither field nor words about a code in open mode`` () =
    task {
        use! env = newEnv ()

        use inviteBrowser = env.App.NewBrowser()
        let! invitePage = signInWith inviteBrowser "sub-i"
        Assert.Contains("name=\"invite\"", invitePage.Html)
        Assert.Contains("Enter your invite code", invitePage.Html)

        do! setMode env.App SignUpMode.Open
        use openBrowser = env.App.NewBrowser()
        let! openPage = signInWith openBrowser "sub-o"
        Assert.Equal("/signup", openPage.Path)
        Assert.DoesNotContain("name=\"invite\"", openPage.Html)
        Assert.DoesNotContain("Invite code", openPage.Html)
        Assert.DoesNotMatch("(?i)invit", openPage.Html)
        Assert.Contains("Choose how you'll appear.", openPage.Html)
        Assert.Contains("name=\"username\"", openPage.Html)
        Assert.Contains("name=\"displayName\"", openPage.Html)
    }

[<Fact>]
let ``existing accounts sign in the same way in both modes`` () =
    task {
        use! env = newEnv ()

        for mode in [ SignUpMode.Invite; SignUpMode.Open ] do
            do! setMode env.App mode
            use browser = env.App.NewBrowser()
            let! landing = browser.Get "/signin/test?sub=sub-pleb"
            Assert.Equal("/account", landing.Path)
            Assert.Equal(HttpStatusCode.OK, landing.Status)
    }

// ---------------------------------------------------------------------------
// Sign-in says so
// ---------------------------------------------------------------------------

/// The attributes and content of the `<a>` for a provider
let private providerLinks (html: string) =
    [ for m in Regex.Matches(html, "<a\\s[^>]*data-provider=\"[^\"]+\"[^>]*>") -> m.Value ]

[<Fact>]
let ``in invite mode sign-in has the note, the dialog and a dialog hook on every provider link`` () =
    task {
        use! env = newEnv ()
        let! page = env.Visitor.Get "/signin"
        Assert.Equal(HttpStatusCode.OK, page.Status)

        // the plain-text note, outside the dialog and so visible without JavaScript
        Assert.Contains("data-invite-only-note", page.Html)
        let note = Regex.Match(page.Html, "data-invite-only-note=\"\"[\\s\\S]*?</div>").Value
        Assert.Contains("Frunchy is invite-only", note)
        Assert.Contains("invite code", note)

        // the dialog: native, labelled, with the exact copy, a continue link per provider, cancel, "don't show again"
        Assert.Matches("<dialog[^>]*data-invite-only-dialog", page.Html)
        let dialog = Regex.Match(page.Html, "<dialog[\\s\\S]*?</dialog>").Value
        Assert.Contains("aria-labelledby=\"invite-dialog-title\"", dialog)
        Assert.Contains("aria-describedby=\"invite-dialog-body\"", dialog)
        Assert.Contains("x-ref=\"inviteDialog\"", dialog)
        Assert.Contains(">Frunchy is invite-only<", dialog)

        Assert.Contains(
            "Already have an account? Continue and sign in. New here? You'll need an invite code from someone on Frunchy: after you sign in, we'll ask for it.",
            dialog
        )

        Assert.Contains("Don't show this again on this device", dialog)
        Assert.Contains("data-invite-remember", dialog)
        Assert.Contains("data-invite-cancel", dialog)
        Assert.Contains(">Cancel<", dialog)
        Assert.Contains("data-invite-continue=\"test\"", dialog)
        Assert.Contains("Continue with Test", dialog)
        Assert.Matches("href=\"/signin/test\\?returnUrl=[^\"]*\"[^>]*data-invite-continue=\"test\"", dialog)

        // every provider link opens it, and is still a plain link
        let links = providerLinks page.Html |> List.filter (fun l -> not (l.Contains "data-invite-continue"))
        Assert.NotEmpty links

        for link in links do
            Assert.Contains("data-invite-dialog", link)
            let name = Regex.Match(link, "data-provider=\"([^\"]+)\"").Groups[1].Value
            Assert.Contains($"href=\"/signin/{name}?returnUrl=", link)

        // one continue link in the dialog per provider
        Assert.Equal(links.Length, Regex.Matches(dialog, "data-invite-continue=").Count)

        // browser storage is touched only inside try/catch, under one key
        let script = Regex.Match(page.Html, "x-data=\"([^\"]*frunchy\\.inviteNoticeSeen[^\"]*)\"").Groups[1].Value
        Assert.NotEmpty script
        Assert.Equal(Regex.Matches(script, "localStorage").Count, Regex.Matches(script, "try \\{[^}]*localStorage").Count)
    }

[<Fact>]
let ``in open mode sign-in has no dialog, no note and no dialog hooks`` () =
    task {
        use! env = newEnv ()
        do! setMode env.App SignUpMode.Open
        let! page = env.Visitor.Get "/signin"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.DoesNotContain("data-invite-only-dialog", page.Html)
        Assert.DoesNotContain("data-invite-only-note", page.Html)
        Assert.DoesNotContain("data-invite-dialog", page.Html)
        Assert.DoesNotContain("<dialog", page.Html)
        Assert.DoesNotContain("frunchy.inviteNoticeSeen", page.Html)
        Assert.DoesNotMatch("(?i)invit", page.Html)
        // the providers are still there, as plain links
        Assert.Contains("data-provider=\"test\"", page.Html)
        Assert.Contains("href=\"/signin/test?returnUrl=", page.Html)
    }

[<Fact>]
let ``the provider round trip starts the same in both modes`` () =
    task {
        use! env = newEnv ()

        for mode in [ SignUpMode.Invite; SignUpMode.Open ] do
            do! setMode env.App mode
            let! response = env.Visitor.GetRaw "/signin/test?sub=sub-pleb"
            Assert.Equal(HttpStatusCode.Found, response.StatusCode)
    }

// ---------------------------------------------------------------------------
// Spanish
// ---------------------------------------------------------------------------

[<Fact>]
let ``the new pages and copy render in Spanish`` () =
    task {
        use! env = newEnvIn "es" (Some "boss")
        use visitor = env.App.NewBrowser "es"

        let! signIn = visitor.Get "/signin"
        Assert.Contains("lang=\"es\"", signIn.Html)
        Assert.Contains("Frunchy es solo con invitación", signIn.Html)
        Assert.Contains("Continuar con Test", signIn.Html)
        Assert.Contains("No volver a mostrar esto en este dispositivo", signIn.Html)
        Assert.Contains(">Cancelar<", signIn.Html)
        Assert.DoesNotContain("Frunchy is invite-only", signIn.Html)

        let! settings = env.Boss.Get settingsPage
        Assert.Contains("Ajustes del sitio", settings.Html)
        Assert.Contains("Solo con invitación", settings.Html)
        Assert.Contains("El registro es solo con invitación.", settings.Html)
        Assert.DoesNotContain("Site settings", settings.Html)

        let! saved = saveAs env.Boss "open"
        let! after = env.Boss.Get(locationOf saved)
        Assert.Contains("Guardado.", after.Html)
        Assert.Contains("El registro está abierto", after.Html)

        let! account = env.Boss.Get "/account"
        Assert.Contains("Modo de registro cambiado", account.Html)
        Assert.Contains("Ajustes del sitio", account.Html)

        // open mode in Spanish: no dialog, and the sign-up page says nothing about a code
        let! signInOpen = visitor.Get "/signin"
        Assert.DoesNotContain("data-invite-only-dialog", signInOpen.Html)
        use newcomer = env.App.NewBrowser "es"
        let! signUp = signInWith newcomer "sub-es"
        Assert.Contains("Elige cómo aparecerás.", signUp.Html)
        Assert.DoesNotContain("name=\"invite\"", signUp.Html)
    }
