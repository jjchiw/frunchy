/// Operators manage invites on the web (feature 019): only operators get in (404 for everyone else on every route
/// and verb), the code is shown once and never stored, logged or recorded, and the CLI's actions are shared.
module FrunchyBackend.Tests.InviteReviewTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Everything the application logs, as lines
type private CapturedLogs() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = List.ofSeq lines

    interface ILoggerProvider with
        member _.CreateLogger(_: string) =
            { new ILogger with
                member _.BeginScope<'S>(_: 'S) = Unchecked.defaultof<IDisposable>
                member _.IsEnabled(_: LogLevel) = true

                member _.Log<'S>(_: LogLevel, _: EventId, state: 'S, ex: exn, formatter: Func<'S, exn, string>) =
                    lines.Enqueue(formatter.Invoke(state, ex)) }

        member _.Dispose() = ()

type private Env =
    { App: TestApp
      Logs: CapturedLogs
      /// The root operator (username "boss"), browsing in the given language
      Boss: Browser
      /// An operator from the table, not the root
      Opr: Browser
      /// Signed in, not an operator
      Pleb: Browser
      Visitor: Browser }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

let private newEnvIn (language: string) : Task<Env> =
    task {
        let! db = newDatabase ()
        let logs = new CapturedLogs()

        let! app =
            createAppWith db [ "Frunchy:RootOperator", "boss" ] (fun builder -> builder.Logging.AddProvider logs |> ignore)

        let boss = app.NewBrowser language
        let! invite = createInvite app
        let! _ = submitSignUp boss "sub-boss" invite "boss" "Boss"
        let! opr = signedUp app "sub-opr" "opr"
        let! pleb = signedUp app "sub-pleb" "pleb"
        let! added = OperatorActions.add app.Db (Some "boss") "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        // the invites the accounts above signed up with aren't the subject of these tests
        do! Db.execute app.Db "DELETE FROM invites"
        return { App = app; Logs = logs; Boss = boss; Opr = opr; Pleb = pleb; Visitor = app.NewBrowser() }
    }

let private newEnv () = newEnvIn "en"

let private page = "/review/invites"

let private createWith (browser: Browser) (days: string) =
    browser.PostForm(page, "/review/invites/create", [ "days", days ])

let private revokeWith (browser: Browser) (id: string) =
    browser.PostForm(page, "/review/invites/revoke", [ "id", id ])

let private body (response: HttpResponseMessage) =
    task {
        let! raw = response.Content.ReadAsStringAsync()
        return WebUtility.HtmlDecode raw
    }

let private location (response: HttpResponseMessage) = string response.Headers.Location

let private codeIn (html: string) =
    let m = Regex.Match(html, "data-invite-code=\"\"[^>]*>([^<]+)<")
    if not m.Success then failwith "no code shown"
    m.Groups[1].Value.Trim()

let private invites (app: TestApp) = Db.count app.Db "invites"

let private inviteEvents (app: TestApp) =
    Db.dump
        app.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'invite_%' ORDER BY e.id"

let private snapshot (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT i::text FROM invites i ORDER BY id"
        let! events = Db.dump app.Db "SELECT e::text FROM security_events e ORDER BY id"
        return rows, events
    }

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! html = body response
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", html)
        Assert.DoesNotContain("data-invites", html)
        Assert.Null response.Headers.Location
    }

/// Every route and verb answers 404 for this browser, nothing is rate-limited, and nothing changes
let private assertClosedFor (env: Env) (who: string) (browser: Browser) =
    task {
        let! before = snapshot env.App
        let! shown = browser.GetRaw page
        do! assertNotFound $"{who} GET" shown

        let! withMessage = browser.GetRaw $"{page}?msg=revoked"
        do! assertNotFound $"{who} GET with a notice" withMessage

        for action, fields in
            [ "/review/invites/create", [ "days", "7" ]
              "/review/invites/revoke", [ "id", string (Guid.NewGuid()) ] ] do
            let! withToken = browser.PostForm("/account", action, fields)
            do! assertNotFound $"{who} POST {action} with a token" withToken

            let! withoutToken = browser.PostFormWithoutToken(action, fields)
            do! assertNotFound $"{who} POST {action} without a token" withoutToken

        // a 429 would reveal the routes: the limiter is 30 a minute, so go well past it
        for i in 1..40 do
            let! response = browser.PostFormWithoutToken("/review/invites/create", [ "days", "7" ])
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``visitors and users get 404 on every route and verb; the root and table operators get the page`` () =
    task {
        use! env = newEnv ()
        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "user" env.Pleb

        for who, browser in [ "root", env.Boss; "table operator", env.Opr ] do
            let! shown = browser.Get page
            Assert.True(shown.Status = HttpStatusCode.OK, $"{who} got {int shown.Status}")
            Assert.Contains("data-invites", shown.Html)
            Assert.Contains("data-action=\"create\"", shown.Html)
            Assert.Contains("data-invites-empty", shown.Html)
    }

[<Fact>]
let ``a table operator who loses the flag gets the 404 on the next request`` () =
    task {
        use! env = newEnv ()
        let! open' = env.Opr.GetRaw page
        Assert.Equal(HttpStatusCode.OK, open'.StatusCode)
        let! _ = OperatorActions.remove env.App.Db (Some "boss") "opr" OperatorActions.Server
        let! closed = env.Opr.GetRaw page
        do! assertNotFound "former operator" closed
    }

[<Fact>]
let ``create shows the code once with no-store; only its hash is stored and it is nowhere else`` () =
    task {
        use! env = newEnv ()
        let! response = createWith env.Boss "7"
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        Assert.Null response.Headers.Location
        Assert.True(response.Headers.CacheControl.NoStore, "the code page must be no-store")

        let! html = body response
        let code = codeIn html
        Assert.Matches("^inv_[0-9A-HJKMNP-TV-Z]{12}$", code)
        Assert.Contains("data-invite-shown", html)
        Assert.Contains("<time", html)
        // the way to use it names this site's sign-in address
        Assert.Contains("localhost/signin", html)

        // only the hash is stored
        let! stored = Db.scalar<string> env.App.Db "SELECT code_hash FROM invites" []
        Assert.Equal(InviteCode.hash code, stored)
        Assert.DoesNotContain(code.Substring 4, stored)
        let! count = invites env.App
        Assert.Equal(1L, count)

        // the event is on the operator's account, in the same change, and says nothing about the code
        let! events = inviteEvents env.App
        Assert.Equal<string list>([ "boss invite_created user" ], events)
        let! everything = Db.dump env.App.Db "SELECT e::text FROM security_events e"
        let! invitesDump = Db.dump env.App.Db "SELECT i::text FROM invites i"

        for line in everything @ invitesDump do
            Assert.DoesNotContain(code.Substring 4, line)

        // nothing logged names it
        for line in env.Logs.Lines do
            Assert.DoesNotContain(code.Substring 4, line)

        // coming back to the page, or the account page, never shows it again
        let! list = env.Boss.Get page
        Assert.DoesNotContain(code.Substring 4, list.Html)
        Assert.Contains("data-invite-state=\"active\"", list.Html)
        Assert.Contains("data-action=\"revoke\"", list.Html)
        let! account = env.Boss.Get "/account"
        Assert.DoesNotContain(code.Substring 4, account.Html)
    }

[<Fact>]
let ``a new identity signs up with the code once and the row then shows who used it`` () =
    task {
        use! env = newEnv ()
        let! created = createWith env.Opr "3"
        let! html = body created
        let code = codeIn html

        use newcomer = env.App.NewBrowser()
        let! signedUp = submitSignUp newcomer "sub-newcomer" code "newcomer" "Newcomer"
        Assert.Equal(HttpStatusCode.Found, signedUp.StatusCode)

        let! list = env.Opr.Get page
        Assert.Contains("data-invite-state=\"used\"", list.Html)
        Assert.Contains("data-invite-used-by=\"newcomer\"", list.Html)
        // a used invite can't be revoked
        Assert.DoesNotContain("data-action=\"revoke\"", list.Html)

        // the code works once
        use second = env.App.NewBrowser()
        let! again = submitSignUp second "sub-second" code "second" "Second"
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode)

        // the user deleted afterwards: the row stays "used", without a name
        do! Db.execute env.App.Db "DELETE FROM users WHERE username = 'newcomer'"
        let! after = env.Opr.Get page
        Assert.Contains("data-invite-state=\"used\"", after.Html)
        Assert.Contains("data-invite-used-by=\"\"", after.Html)
    }

[<Fact>]
let ``days outside 1 to 365 or not a number are refused and create nothing`` () =
    task {
        use! env = newEnv ()
        let! before = snapshot env.App

        for days in [ "0"; "366"; "abc"; ""; "-1"; "7.5"; "99999999999" ] do
            let! response = createWith env.Boss days
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"'{days}' got {int response.StatusCode}")
            Assert.Equal("/review/invites?msg=bad-days", location response)

            let! followed = env.Boss.Follow response
            Assert.Contains("data-invites-notice=\"bad-days\"", followed.Html)
            Assert.DoesNotContain("data-invite-code", followed.Html)

        let! after = snapshot env.App
        Assert.Equal(before, after)

        // the limits themselves are fine
        for days in [ "1"; "365" ] do
            let! ok = createWith env.Boss days
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode)

        let! expiries = Db.dump env.App.Db "SELECT round(extract(epoch FROM (expires_at - created_at)) / 86400)::int FROM invites ORDER BY 1"
        Assert.Equal<string list>([ "1"; "365" ], expiries)
    }

[<Fact>]
let ``a post without the antiforgery token creates and revokes nothing`` () =
    task {
        use! env = newEnv ()
        let! created = createWith env.Boss "7"
        Assert.Equal(HttpStatusCode.OK, created.StatusCode)
        let! before = snapshot env.App

        let! forgedCreate = env.Boss.PostFormWithoutToken("/review/invites/create", [ "days", "7" ])
        Assert.Equal(HttpStatusCode.BadRequest, forgedCreate.StatusCode)

        let! id = Db.scalar<Guid> env.App.Db "SELECT id FROM invites" []
        let! forgedRevoke = env.Boss.PostFormWithoutToken("/review/invites/revoke", [ "id", string id ])
        Assert.Equal(HttpStatusCode.BadRequest, forgedRevoke.StatusCode)

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``revoking an active invite records the event and its code is then refused at sign-up`` () =
    task {
        use! env = newEnv ()
        let! created = createWith env.Boss "7"
        let! createdHtml = body created
        let code = codeIn createdHtml
        let! id = Db.scalar<Guid> env.App.Db "SELECT id FROM invites" []

        let! response = revokeWith env.Boss (string id)
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal("/review/invites?msg=revoked", location response)
        let! followed = env.Boss.Follow response
        Assert.Contains("data-invites-notice=\"revoked\"", followed.Html)
        Assert.Contains("data-invite-state=\"revoked\"", followed.Html)
        Assert.DoesNotContain("data-action=\"revoke\"", followed.Html)

        let! events = inviteEvents env.App
        Assert.Equal<string list>([ "boss invite_created user"; "boss invite_revoked user" ], events)

        use newcomer = env.App.NewBrowser()
        let! refused = submitSignUp newcomer "sub-late" code "late" "Late"
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode)
        let! refusedHtml = body refused
        Assert.Contains("This invite code isn't valid.", refusedHtml)
        let! lateUsers = Db.scalar<int64> env.App.Db "SELECT count(*) FROM users WHERE username = 'late'" []
        Assert.Equal(0L, lateUsers)

        // the second revoke changes nothing and records nothing
        let! again = revokeWith env.Boss (string id)
        Assert.Equal("/review/invites?msg=not-active", location again)
        let! notice = env.Boss.Follow again
        Assert.Contains("data-invites-notice=\"not-active\"", notice.Html)
        let! eventsAfter = inviteEvents env.App
        Assert.Equal<string list>(events, eventsAfter)
    }

[<Fact>]
let ``used, expired and unknown invites are not-active and a bad id is bad-input`` () =
    task {
        use! env = newEnv ()
        let! used = InviteActions.create env.App.Db None 7
        let! expired = InviteActions.create env.App.Db None 7
        do! Db.execute env.App.Db $"UPDATE invites SET used_at = now() WHERE id = '{used.Id}'"
        do! Db.execute env.App.Db $"UPDATE invites SET expires_at = now() - interval '1 minute' WHERE id = '{expired.Id}'"
        let! before = snapshot env.App

        for id in [ string used.Id; string expired.Id; string (Guid.NewGuid()) ] do
            let! response = revokeWith env.Boss id
            Assert.Equal("/review/invites?msg=not-active", location response)

        for id in [ ""; "nope"; "1; DROP TABLE invites" ] do
            let! response = revokeWith env.Boss id
            Assert.Equal("/review/invites?msg=bad-input", location response)
            let! followed = env.Boss.Follow response
            Assert.Contains("data-invites-notice=\"bad-input\"", followed.Html)

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``the page shows every state with its times and lists at most the 100 newest`` () =
    task {
        use! env = newEnv ()
        let! active = InviteActions.create env.App.Db None 7
        let! revoked = InviteActions.create env.App.Db None 7
        let! expired = InviteActions.create env.App.Db None 7
        let! _ = InviteActions.revoke env.App.Db None revoked.Id
        do! Db.execute env.App.Db $"UPDATE invites SET expires_at = now() - interval '1 minute' WHERE id = '{expired.Id}'"

        let! shown = env.Boss.Get page
        let row (id: Guid) = Regex.Match(shown.Html, $"data-invite-row=\"{id}\"\\s+data-invite-state=\"(\\w+)\"")
        Assert.Equal("active", (row active.Id).Groups[1].Value)
        Assert.Equal("revoked", (row revoked.Id).Groups[1].Value)
        Assert.Equal("expired", (row expired.Id).Groups[1].Value)
        // created and expiry times, both as <time> elements
        Assert.Equal(6, Regex.Matches(shown.Html, "<time ").Count)

        do!
            Db.execute
                env.App.Db
                "INSERT INTO invites (id, code_hash, expires_at, created_at) SELECT gen_random_uuid(), 'h' || g, now() + interval '1 day', now() - make_interval(mins => g) FROM generate_series(1, 120) g"

        let! many = env.Boss.Get page
        Assert.Equal(100, Regex.Matches(many.Html, "data-invite-row=").Count)
    }

[<Fact>]
let ``the account page links to the invites for operators only`` () =
    task {
        use! env = newEnv ()

        for who, browser in [ "root", env.Boss; "table operator", env.Opr ] do
            let! account = browser.Get "/account"
            Assert.True(account.Html.Contains "data-invites-link", $"{who} should see the link")

        let! pleb = env.Pleb.Get "/account"
        Assert.DoesNotContain("data-invites-link", pleb.Html)
    }

[<Fact>]
let ``the page, its notices and the code page are in Spanish`` () =
    task {
        use! env = newEnvIn "es"
        let! shown = env.Boss.Get page
        Assert.Contains("Invitaciones", shown.Html)
        Assert.Contains("Crear invitación", shown.Html)

        let! created = createWith env.Boss "7"
        let! html = body created
        Assert.Contains("Tu código de invitación", html)
        Assert.Contains("Copiar código", html)
        Assert.DoesNotContain("Your invite code", html)

        let! list = env.Boss.Get page
        Assert.Contains("Activa", list.Html)

        let! notice = env.Boss.Get $"{page}?msg=bad-days"
        Assert.Contains("data-invites-notice=\"bad-days\"", notice.Html)
        Assert.Contains("entre 1 y 365", notice.Html)

        let! account = env.Boss.Get "/account"
        Assert.Contains("Gestionar invitaciones", account.Html)
    }
