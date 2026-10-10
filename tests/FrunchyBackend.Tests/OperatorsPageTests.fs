/// The root operator's page for managing operators (feature 008, US1 and US2): only the root gets in,
/// everyone else gets the plain 404 before anything is read, and every change records its events.
module FrunchyBackend.Tests.OperatorsPageTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

type Env =
    { App: TestApp
      Clock: TestClock
      /// The root operator when one is configured (username "boss")
      Boss: Browser
      /// An operator from the table, not the root (username "opr")
      Opr: Browser
      /// Signed in, not an operator
      Pleb: Browser
      /// Signed in, nobody yet: the one to make an operator
      Newbie: Browser
      Visitor: Browser }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

/// An app with a test clock, the given root, and the accounts boss (browsing in `language`), opr (in the
/// operators table), pleb and newbie
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
        let! newbie = signedUp app "sub-newbie" "newbie"
        let! added = OperatorActions.add app.Db root "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        return { App = app; Clock = clock; Boss = boss; Opr = opr; Pleb = pleb; Newbie = newbie; Visitor = app.NewBrowser() }
    }

let private newEnv (root: string option) = newEnvIn "en" root

/// Everything the operator actions can change
let private snapshot (app: TestApp) =
    task {
        let! operators = Db.dump app.Db "SELECT o::text FROM operators o ORDER BY user_id"
        let! events = Db.dump app.Db "SELECT e::text FROM security_events e ORDER BY id"
        return operators, events
    }

let private operatorEvents (app: TestApp) =
    Db.dump
        app.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'operator_%' ORDER BY e.id"

/// POST with a token taken from `formPage` (the operators page for the root, the account page for anyone else)
let private post (formPage: string) (browser: Browser) (action: string) (username: string) =
    browser.PostForm(formPage, action, [ "username", username ])

let private asRoot (env: Env) (action: string) (username: string) = post "/review/operators" env.Boss action username

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! body = response.Content.ReadAsStringAsync()
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", WebUtility.HtmlDecode body)
        Assert.DoesNotContain("data-operators", body)
        Assert.Null response.Headers.Location
    }

/// Every route and verb answers 404 for this browser, nothing is rate-limited, and nothing changes
let private assertClosedFor (env: Env) (who: string) (browser: Browser) =
    task {
        let! before = snapshot env.App

        let! page = browser.GetRaw "/review/operators"
        do! assertNotFound $"{who} GET" page

        for action in [ "/review/operators/add"; "/review/operators/remove" ] do
            let! withToken = post "/account" browser action "newbie"
            do! assertNotFound $"{who} POST {action} with a token" withToken

            let! withoutToken = browser.PostFormWithoutToken(action, [ "username", "newbie" ])
            do! assertNotFound $"{who} POST {action} without a token" withoutToken

        // a 429 would reveal the routes
        for i in 1..15 do
            let! response = browser.PostFormWithoutToken("/review/operators/add", [ "username", "newbie" ])
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``access matrix: visitors, users and operators who aren't root get 404 on every route and verb`` () =
    task {
        use! env = newEnv (Some "boss")
        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "user" env.Pleb
        do! assertClosedFor env "operator who isn't root" env.Opr

        // the root gets the page
        let! page = env.Boss.Get "/review/operators"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-operators", page.Html)
    }

[<Fact>]
let ``with no root configured nobody gets in, not even operators or the would-be root`` () =
    task {
        use! env = newEnv None
        do! assertClosedFor env "boss" env.Boss
        do! assertClosedFor env "operator" env.Opr
        do! assertClosedFor env "user" env.Pleb
        do! assertClosedFor env "visitor" env.Visitor
    }

[<Fact>]
let ``the page lists the root apart and each operator with who added them and when`` () =
    task {
        use! env = newEnv (Some "boss")
        let! added = asRoot env "/review/operators/add" "newbie"
        Assert.Equal("/review/operators?msg=added", locationOf added)

        let! page = env.Boss.Get "/review/operators"
        Assert.Contains("data-operators-root=\"boss\"", page.Html)
        Assert.DoesNotContain("data-operator=\"boss\"", page.Html)
        // added from the terminal: nobody on record; added from the page: the root
        Assert.Contains("data-operator=\"opr\"", page.Html)
        Assert.Matches("data-operator=\"opr\"\\s+data-added-by=\"\"", page.Html)
        Assert.Matches("data-operator=\"newbie\"\\s+data-added-by=\"boss\"", page.Html)
        let stamps = [ for m in Regex.Matches(page.Html, "data-added-at=\"([^\"]+)\"") -> m.Groups[1].Value ]
        Assert.Equal(2, stamps.Length)

        for stamp in stamps do
            Assert.True(fst (DateTimeOffset.TryParse stamp), $"not a timestamp: {stamp}")

        Assert.Contains("data-action=\"add\"", page.Html)
        Assert.Contains("data-action=\"remove\"", page.Html)
        Assert.DoesNotContain("data-operators-empty", page.Html)

        // the notice from the redirect is shown
        let! noticed = env.Boss.Get "/review/operators?msg=added"
        Assert.Contains("data-operators-notice=\"added\"", noticed.Html)
        let! unknownKey = env.Boss.Get "/review/operators?msg=whatever"
        Assert.DoesNotContain("data-operators-notice", unknownKey.Html)

        let! _ = OperatorActions.remove env.App.Db (Some "boss") "opr" OperatorActions.Server
        let! _ = OperatorActions.remove env.App.Db (Some "boss") "newbie" OperatorActions.Server
        let! empty = env.Boss.Get "/review/operators"
        Assert.Contains("data-operators-empty", empty.Html)
    }

[<Fact>]
let ``adding and removing take effect on the next request`` () =
    task {
        use! env = newEnv (Some "boss")
        let! before = env.Newbie.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode)

        // the username goes through the same rule as sign-up: case and spaces don't matter
        let! added = asRoot env "/review/operators/add" "  Newbie "
        Assert.Equal(HttpStatusCode.Found, added.StatusCode)
        Assert.Equal("/review/operators?msg=added", locationOf added)
        let! allowed = env.Newbie.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode)
        // an operator who isn't root still can't manage operators
        let! stillClosed = env.Newbie.GetRaw "/review/operators"
        Assert.Equal(HttpStatusCode.NotFound, stillClosed.StatusCode)

        let! removed = asRoot env "/review/operators/remove" "newbie"
        Assert.Equal("/review/operators?msg=removed", locationOf removed)
        let! closed = env.Newbie.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode)
    }

[<Fact>]
let ``refusals change nothing and say why`` () =
    task {
        // at most 10 POSTs: the root's limit is 10 a minute
        use! env = newEnv (Some "boss")
        let! before = snapshot env.App

        for action, username, key in
            [ "add", "boss", "is-root"
              "add", "BOSS", "is-root"
              "remove", "boss", "is-root"
              "add", "nobody", "unknown-user"
              "add", "opr", "already-operator"
              "remove", "newbie", "not-operator"
              "add", "", "bad-input"
              "add", "new bie", "bad-input"
              "add", "admin", "bad-input"
              "remove", "x'; DROP TABLE operators;--", "bad-input" ] do

            let! response = asRoot env $"/review/operators/{action}" username
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"{action} '{username}' got {int response.StatusCode}")
            Assert.Equal($"/review/operators?msg={key}", locationOf response)

            let! page = env.Boss.Get(locationOf response)
            Assert.Contains($"data-operators-notice=\"{key}\"", page.Html)

        let! after = snapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``the forgery check still guards the root's own POSTs`` () =
    task {
        use! env = newEnv (Some "boss")
        let! forged = env.Boss.PostFormWithoutToken("/review/operators/add", [ "username", "newbie" ])
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode)
        let! operators = Db.scalar<int64> env.App.Db "SELECT count(*) FROM operators" []
        Assert.Equal(1L, operators)
    }

[<Fact>]
let ``a stale session re-confirms first and changes nothing; non-roots still see 404`` () =
    task {
        use! env = newEnv (Some "boss")
        // the page itself needs no recent sign-in; the token comes from it
        let! _ = env.Boss.Get "/review/operators"
        env.Clock.Now <- env.Clock.Now.AddMinutes 11.0
        let! before = snapshot env.App

        for action in [ "add"; "remove" ] do
            let! stale = asRoot env $"/review/operators/{action}" "newbie"
            Assert.Equal(HttpStatusCode.Found, stale.StatusCode)
            Assert.Equal("/signin/reconfirm?returnUrl=%2Freview%2Foperators", locationOf stale)

        let! after = snapshot env.App
        Assert.Equal(before, after)

        // a stale operator who isn't root is not sent anywhere: it's still a 404
        let! outsider = post "/account" env.Opr "/review/operators/add" "newbie"
        do! assertNotFound "stale non-root" outsider

        // re-confirming brings the root back to the page, where the change now works
        let! back = env.Boss.Get("/signin/reconfirm?returnUrl=%2Freview%2Foperators&sub=sub-boss")
        Assert.Equal("/review/operators", back.Path)
        let! added = asRoot env "/review/operators/add" "newbie"
        Assert.Equal("/review/operators?msg=added", locationOf added)
    }

[<Fact>]
let ``changes are limited to 10 a minute for the root only`` () =
    task {
        use! env = newEnv (Some "boss")

        for i in 1..10 do
            let! response = asRoot env "/review/operators/add" "nobody"
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"POST {i} got {int response.StatusCode}")

        let! limited = asRoot env "/review/operators/add" "nobody"
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
        // reading is not limited
        let! page = env.Boss.GetRaw "/review/operators"
        Assert.Equal(HttpStatusCode.OK, page.StatusCode)
    }

[<Fact>]
let ``adding records an event on both accounts, with the right actor`` () =
    task {
        use! env = newEnv (Some "boss")
        let! _ = asRoot env "/review/operators/add" "newbie"
        let! events = operatorEvents env.App
        // opr was added from the terminal in the setup: only its own account has an event
        Assert.Equal<string list>([ "opr operator_added operator"; "newbie operator_added operator"; "boss operator_added user" ], events)

        let! _ = asRoot env "/review/operators/remove" "newbie"
        let! events = operatorEvents env.App
        Assert.Equal<string list>(
            [ "opr operator_added operator"
              "newbie operator_added operator"
              "boss operator_added user"
              "newbie operator_removed operator"
              "boss operator_removed user" ],
            events
        )

        // both owners see them on their account page
        let! root = env.Boss.Get "/account"
        Assert.Contains("data-security-event=\"operator_added\"", root.Html)
        Assert.Contains("Operator removed", root.Html)
        let! newbie = env.Newbie.Get "/account"
        Assert.Contains("Operator added · by the operator", newbie.Html)
        Assert.Contains("Operator removed · by the operator", newbie.Html)
    }

[<Fact>]
let ``deleting an operator's account removes the grant; deleting the root's keeps it with nobody on record`` () =
    task {
        use! env = newEnv (Some "boss")
        let! _ = asRoot env "/review/operators/add" "newbie"
        let! count = Db.scalar<int64> env.App.Db "SELECT count(*) FROM operators" []
        Assert.Equal(2L, count)

        let! opr = AccountActions.tryUserIdByUsername env.App.Db "opr"
        let! deleted = AccountActions.deleteUser env.App.Db opr.Value
        Assert.True deleted.IsSome
        let! rows = Db.dump env.App.Db "SELECT u.username FROM operators o JOIN users u ON u.id = o.user_id"
        Assert.Equal<string list>([ "newbie" ], rows)

        let! boss = AccountActions.tryUserIdByUsername env.App.Db "boss"
        let! _ = AccountActions.deleteUser env.App.Db boss.Value
        let! kept = Db.dump env.App.Db "SELECT (added_by IS NULL)::text FROM operators"
        Assert.Equal<string list>([ "true" ], kept)
    }

[<Fact>]
let ``the page and its notices are in English and Spanish`` () =
    task {
        use! env = newEnv (Some "boss")
        let! en = env.Boss.Get "/review/operators?msg=is-root"
        Assert.Contains("lang=\"en\"", en.Html)
        Assert.Contains("Root operator", en.Html)
        Assert.Contains("Make operator", en.Html)
        Assert.Contains("the root operator, set in the server configuration", en.Html)

        use! esEnv = newEnvIn "es" (Some "boss")
        let! es = esEnv.Boss.Get "/review/operators?msg=already-operator"
        Assert.Contains("lang=\"es\"", es.Html)
        Assert.Contains("Operador raíz", es.Html)
        Assert.Contains("Hacer operador", es.Html)
        Assert.Contains("Ese usuario ya es operador", es.Html)
        Assert.DoesNotContain("Make operator", es.Html)

        // the event labels too
        let! _ = asRoot esEnv "/review/operators/add" "newbie"
        let! account = esEnv.Boss.Get "/account"
        Assert.Contains("Operador añadido", account.Html)
    }

[<Fact>]
let ``the account page tells operators what they are, and links the operators page for the root only`` () =
    task {
        use! env = newEnv (Some "boss")

        let! root = env.Boss.Get "/account"
        Assert.Contains("data-operator-status", root.Html)
        Assert.Contains("You're an operator", root.Html.Replace("&#39;", "'"))
        Assert.Contains("data-review-link", root.Html)
        Assert.Contains("data-operators-link", root.Html)
        Assert.Contains("href=\"/review/operators\"", root.Html)

        let! opr = env.Opr.Get "/account"
        Assert.Contains("data-operator-status", opr.Html)
        Assert.Contains("data-review-link", opr.Html)
        Assert.DoesNotContain("data-operators-link", opr.Html)
        Assert.DoesNotContain("/review/operators", opr.Html)

        let! pleb = env.Pleb.Get "/account"
        Assert.DoesNotContain("data-operator-status", pleb.Html)
        Assert.DoesNotContain("data-operators-link", pleb.Html)
        Assert.DoesNotContain("/review/", pleb.Html)

        // the line follows the table at once: a new operator sees it, and loses it when removed
        let! _ = asRoot env "/review/operators/add" "newbie"
        let! added = env.Newbie.Get "/account"
        Assert.Contains("data-operator-status", added.Html)
        let! _ = asRoot env "/review/operators/remove" "newbie"
        let! removed = env.Newbie.Get "/account"
        Assert.DoesNotContain("data-operator-status", removed.Html)

        // in Spanish
        use! es = newEnvIn "es" (Some "boss")
        let! page = es.Boss.Get "/account"
        Assert.Contains("Eres operador", page.Html)
        Assert.Contains("Gestionar operadores", page.Html)

        // no root configured: the table's operator still sees the line, nobody sees the link
        use! none = newEnv None
        let! oprPage = none.Opr.Get "/account"
        Assert.Contains("data-operator-status", oprPage.Html)
        let! bossPage = none.Boss.Get "/account"
        Assert.DoesNotContain("data-operator-status", bossPage.Html)
        Assert.DoesNotContain("data-operators-link", bossPage.Html)
    }
