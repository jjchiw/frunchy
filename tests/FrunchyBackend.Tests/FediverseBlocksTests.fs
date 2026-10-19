/// Feature 015 (US6, T012): a user's blocks on the account page and the instance blocks on `/review/fediverse`, both
/// through Megáfono's block calls (the fake keeps them in memory). Megáfono's errors show plain messages and change
/// nothing; a security event is recorded only for a change; outsiders get the plain 404 on every route and verb.
module FrunchyBackend.Tests.FediverseBlocksTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.MegafonoClient
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.FakeMegafono
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Views

type Env =
    { App: TestApp
      /// Signed in, not an operator
      Alice: Browser
      /// The root operator (username "boss")
      Boss: Browser
      Visitor: Browser }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

let private newEnvWith (federated: bool) : Task<Env> =
    task {
        let! db = newDatabase ()

        let! app =
            if federated then
                createFederatedAppWith db [ "Frunchy:RootOperator", "boss" ] ignore
            else
                createAppWith db [ "Frunchy:RootOperator", "boss" ] ignore

        let! alice = signedUp app "sub-alice" "alice"
        let boss = app.NewBrowser()
        let! invite = createInvite app
        let! _ = submitSignUp boss "sub-boss" invite "boss" "Boss"
        return { App = app; Alice = alice; Boss = boss; Visitor = app.NewBrowser() }
    }

let private newEnv () = newEnvWith true

let private userId (app: TestApp) (username: string) =
    Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box username ]

let private blocksOf (app: TestApp) (username: string) =
    task {
        let! id = userId app username
        return app.Megafono.BlocksOf(string id)
    }

let private events (app: TestApp) =
    Db.dump
        app.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'fediverse_block_%' ORDER BY e.id"

let private addAs (browser: Browser) (target: string) =
    browser.PostForm("/account", "/account/blocks/add", [ "target", target ])

let private removeAs (browser: Browser) (kind: string) (target: string) =
    browser.PostForm("/account", "/account/blocks/remove", [ "kind", kind; "target", target ])

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

/// The `data-block` values on a page, in order
let private listed (html: string) =
    [ for m in Regex.Matches(html, "data-block=\"([^\"]+)\"") -> m.Groups[1].Value ]

// ---------------------------------------------------------------------------
// What a person types
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData("@bob@mastodon.social", "actor", "@bob@mastodon.social")>]
[<InlineData("bob@mastodon.social", "actor", "@bob@mastodon.social")>]
[<InlineData("  @Bob@Mastodon.Social  ", "actor", "@Bob@mastodon.social")>]
[<InlineData("https://spam.example/users/bob", "actor", "https://spam.example/users/bob")>]
[<InlineData("https://spam.example/@bob", "actor", "https://spam.example/@bob")>]
[<InlineData("spam.example", "domain", "spam.example")>]
[<InlineData("Spam.Example", "domain", "spam.example")>]
[<InlineData("spam.example.", "domain", "spam.example")>]
[<InlineData("megafono", "domain", "megafono")>]
let ``the block field is read by its shape`` (typed: string, kind: string, expected: string) =
    match parseBlockTarget typed with
    | Some(Actor value) ->
        Assert.Equal("actor", kind)
        Assert.Equal(expected, value)
    | Some(Domain value) ->
        Assert.Equal("domain", kind)
        Assert.Equal(expected, value)
    | None -> failwith $"'{typed}' was refused"

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("http://spam.example/users/bob")>]
[<InlineData("https://spam.example")>]
[<InlineData("ftp://spam.example/x")>]
[<InlineData("@bob")>]
[<InlineData("@@")>]
[<InlineData("@bob@")>]
[<InlineData("bob@@host.example")>]
[<InlineData("a@b@c.example")>]
[<InlineData("two words")>]
[<InlineData("spam/example")>]
[<InlineData("-bad.example")>]
[<InlineData("bad_host.example")>]
let ``the block field refuses what is neither an account nor a server`` (typed: string) =
    Assert.True(parseBlockTarget typed |> Option.isNone, $"'{typed}' was accepted")

// ---------------------------------------------------------------------------
// A user's blocks
// ---------------------------------------------------------------------------

[<Fact>]
let ``a user adds a block by handle, by address and by domain, sees them listed, and lifts them`` () =
    task {
        use! env = newEnv ()
        let! id = userId env.App "alice"

        // by handle, with and without the leading @ (the same block: the second is a repeat)
        let! byHandle = addAs env.Alice "@bob@mastodon.social"
        Assert.Equal(HttpStatusCode.Found, byHandle.StatusCode)
        Assert.Equal("/account?msg=block-added#blocks", locationOf byHandle)
        let! repeat = addAs env.Alice "bob@mastodon.social"
        Assert.Equal("/account?msg=block-already#blocks", locationOf repeat)

        let! byUrl = addAs env.Alice "https://spam.example/users/carol"
        Assert.Equal("/account?msg=block-added#blocks", locationOf byUrl)
        let! byDomain = addAs env.Alice "Spam.Example"
        Assert.Equal("/account?msg=block-added#blocks", locationOf byDomain)

        // what Megáfono was asked
        let posts = env.App.Megafono.RequestsTo $"POST /internal/actors/{id}/blocks"
        Assert.Equal(4, posts.Length)
        Assert.Equal("@bob@mastodon.social", posts[0].Json.GetProperty("actor").GetString())
        Assert.Equal("https://spam.example/users/carol", posts[2].Json.GetProperty("actor").GetString())
        Assert.Equal("spam.example", posts[3].Json.GetProperty("domain").GetString())
        Assert.All(posts, fun p -> Assert.Equal(Some $"Bearer {Token}", p.Authorization))

        // listed, newest first, with the account/server kind
        let! page = env.Alice.Get "/account"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-account=\"blocks\"", page.Html)
        Assert.Contains("data-blocks", page.Html)
        Assert.Equal<string list>([ "spam.example"; "https://spam.example/users/carol"; "@bob@mastodon.social" ], listed page.Html)
        Assert.Contains("data-block-add", page.Html)
        Assert.Equal(3, Regex.Matches(page.Html, "data-block-remove").Count)

        // lifted one by one, as the list's own forms post them
        let! lifted = removeAs env.Alice "domain" "spam.example"
        Assert.Equal("/account?msg=block-removed#blocks", locationOf lifted)
        let! liftedUrl = removeAs env.Alice "actor" "https://spam.example/users/carol"
        Assert.Equal("/account?msg=block-removed#blocks", locationOf liftedUrl)
        let! liftedHandle = removeAs env.Alice "actor" "@bob@mastodon.social"
        Assert.Equal("/account?msg=block-removed#blocks", locationOf liftedHandle)

        let! after = env.Alice.Get "/account"
        Assert.Empty(listed after.Html)
        Assert.Contains("data-blocks-empty", after.Html)
        Assert.Empty(env.App.Megafono.BlocksOf(string id))
    }

[<Fact>]
let ``the notice shows inside the section, with its plain message`` () =
    task {
        use! env = newEnv ()
        let! response = addAs env.Alice "@bob@mastodon.social"
        let! page = env.Alice.Follow response
        Assert.Contains("data-block-notice=\"block-added\"", page.Html)
        Assert.Contains(Texts.en.BlockNotice "block-added", page.Html)
        // the notice is not the account page's top-of-page notice
        Assert.DoesNotContain("data-account-notice", page.Html)
    }

[<Theory>]
[<InlineData(400, "block-invalid")>]
[<InlineData(404, "block-not-found")>]
[<InlineData(502, "block-unreachable")>]
[<InlineData(503, "block-error")>]
[<InlineData(500, "block-error")>]
let ``a refusal from Megafono shows its plain message and changes nothing`` (status: int, key: string) =
    task {
        use! env = newEnv ()
        env.App.Megafono.Route("POST /internal/actors/*/blocks", [ Status status ])

        let! response = addAs env.Alice "@bob@mastodon.social"
        Assert.Equal($"/account?msg={key}#blocks", locationOf response)

        let! page = env.Alice.Follow response
        Assert.Contains($"data-block-error=\"{key}\"", page.Html)
        Assert.Contains(Texts.en.BlockNotice key, page.Html)
        Assert.DoesNotContain("data-block-notice", page.Html)

        let! blocks = blocksOf env.App "alice"
        Assert.Empty blocks
        let! recorded = events env.App
        Assert.Empty recorded
    }

[<Fact>]
let ``409 blocked, and Megafono not answering at all, show their messages and change nothing`` () =
    task {
        use! env = newEnv ()
        env.App.Megafono.Route("POST /internal/actors/*/blocks", [ Body(409, """{"error":"blocked","message":"the instance blocks spam.example"}""") ])
        let! blocked = addAs env.Alice "@bob@spam.example"
        Assert.Equal("/account?msg=block-blocked#blocks", locationOf blocked)
        let! page = env.Alice.Follow blocked
        Assert.Contains(Texts.en.BlockNotice "block-blocked", page.Html)

        // a 409 that is not "blocked" is just an error
        env.App.Megafono.Route("POST /internal/actors/*/blocks", [ Status 409 ])
        let! conflict = addAs env.Alice "@bob@spam.example"
        Assert.Equal("/account?msg=block-error#blocks", locationOf conflict)

        env.App.Megafono.Route("POST /internal/actors/*/blocks", [ Throw(HttpRequestException "down") ])
        let! down = addAs env.Alice "@bob@spam.example"
        Assert.Equal("/account?msg=block-unreachable#blocks", locationOf down)

        let! blocks = blocksOf env.App "alice"
        Assert.Empty blocks
        let! recorded = events env.App
        Assert.Empty recorded
    }

[<Fact>]
let ``what can't be a block never reaches Megafono`` () =
    task {
        use! env = newEnv ()

        for typed in [ ""; "two words"; "http://spam.example/users/bob"; "@bob" ] do
            let! response = addAs env.Alice typed
            Assert.Equal("/account?msg=block-invalid#blocks", locationOf response)

        let! bad = removeAs env.Alice "actor" "not-a-url"
        Assert.Equal("/account?msg=block-invalid#blocks", locationOf bad)
        let! worse = removeAs env.Alice "nonsense" "spam.example"
        Assert.Equal("/account?msg=block-invalid#blocks", locationOf worse)

        Assert.Empty(env.App.Megafono.RequestsTo "POST /internal/actors/*/blocks")
        Assert.Empty(env.App.Megafono.RequestsTo "DELETE /internal/actors/*/blocks")
    }

[<Fact>]
let ``lifting a block that isn't there says so and records nothing`` () =
    task {
        use! env = newEnv ()
        let! response = removeAs env.Alice "domain" "spam.example"
        Assert.Equal("/account?msg=block-not-blocked#blocks", locationOf response)
        let! page = env.Alice.Follow response
        Assert.Contains(Texts.en.BlockNotice "block-not-blocked", page.Html)
        let! recorded = events env.App
        Assert.Empty recorded
    }

[<Fact>]
let ``one security event per change, none for a repeat or a refusal`` () =
    task {
        use! env = newEnv ()
        let! _ = addAs env.Alice "@bob@mastodon.social"
        let! _ = addAs env.Alice "@bob@mastodon.social"
        let! _ = addAs env.Alice "spam.example"
        let! _ = removeAs env.Alice "domain" "spam.example"
        let! _ = removeAs env.Alice "domain" "spam.example"
        let! recorded = events env.App

        Assert.Equal<string list>(
            [ "alice fediverse_block_added user"; "alice fediverse_block_added user"; "alice fediverse_block_removed user" ],
            recorded
        )

        // they show in the account's own activity, with their label
        let! page = env.Alice.Get "/account"
        Assert.Contains(Texts.en.SecurityEventLabel "fediverse_block_added", page.Html)
        Assert.Contains("data-security-event=\"fediverse_block_removed\"", page.Html)
    }

[<Fact>]
let ``when Megafono can't list the blocks the page still loads, with a plain line`` () =
    task {
        use! env = newEnv ()
        env.App.Megafono.Route("GET /internal/actors/*/blocks", [ Status 503 ])
        let! page = env.Alice.Get "/account"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-block-load-failed", page.Html)
        Assert.Contains(Texts.en.BlocksLoadFailed, page.Html)
        // the rest of the page is there, and so is the form
        Assert.Contains("data-account=\"profile\"", page.Html)
        Assert.Contains("data-block-add", page.Html)

        env.App.Megafono.Route("GET /internal/actors/*/blocks", [ Throw(HttpRequestException "down") ])
        let! down = env.Alice.Get "/account"
        Assert.Equal(HttpStatusCode.OK, down.Status)
        Assert.Contains("data-block-load-failed", down.Html)
    }

[<Fact>]
let ``a Megafono that hangs doesn't hold the account page for long`` () =
    task {
        use! env = newEnv ()
        // far longer than the page's 3 s deadline for reading the blocks
        env.App.Megafono.Route("GET /internal/actors/*/blocks", [ Delay(TimeSpan.FromSeconds 20.0, Status 200) ])
        let watch = Diagnostics.Stopwatch.StartNew()
        let! page = env.Alice.Get "/account"
        watch.Stop()
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-block-load-failed", page.Html)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 8.0, $"the account page took {watch.Elapsed}")
    }

[<Fact>]
let ``the section and its routes exist only with federation on`` () =
    task {
        use! env = newEnvWith false
        let! page = env.Alice.Get "/account"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.DoesNotContain("data-account=\"blocks\"", page.Html)
        Assert.DoesNotContain("data-fediverse-link", page.Html)

        let! add = env.Alice.PostForm("/account", "/account/blocks/add", [ "target", "spam.example" ])
        Assert.Equal(HttpStatusCode.NotFound, add.StatusCode)
        let! remove = env.Alice.PostForm("/account", "/account/blocks/remove", [ "kind", "domain"; "target", "spam.example" ])
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode)

        // not even the root operator finds the operator page
        let! review = env.Boss.GetRaw "/review/fediverse"
        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode)
        let! reviewPost = env.Boss.PostForm("/account", "/review/fediverse/blocks/add", [ "target", "spam.example" ])
        Assert.Equal(HttpStatusCode.NotFound, reviewPost.StatusCode)
    }

[<Fact>]
let ``the block routes need a sign-in and the antiforgery token`` () =
    task {
        use! env = newEnv ()
        let! visitor = env.Visitor.PostFormWithoutToken("/account/blocks/add", [ "target", "spam.example" ])
        Assert.NotEqual(HttpStatusCode.OK, visitor.StatusCode)
        let! noToken = env.Alice.PostFormWithoutToken("/account/blocks/add", [ "target", "spam.example" ])
        Assert.NotEqual(HttpStatusCode.Found, noToken.StatusCode)
        Assert.Empty(env.App.Megafono.RequestsTo "POST /internal/actors/*/blocks")
    }

[<Fact>]
let ``the section reads in Spanish`` () =
    task {
        use! env = newEnv ()
        let es = env.App.NewBrowser "es"
        let! _ = signInWith es "sub-alice"
        let! response = es.PostForm("/account", "/account/blocks/add", [ "target", "@bob@mastodon.social" ])
        let! page = es.Follow response
        Assert.Contains(Texts.es.BlocksTitle, page.Html)
        Assert.Contains(Texts.es.BlockNotice "block-added", page.Html)
        Assert.Contains(Texts.es.BlocksRemoveButton, page.Html)
    }

// ---------------------------------------------------------------------------
// Instance blocks (operators)
// ---------------------------------------------------------------------------

[<Fact>]
let ``the operator adds, lists and lifts instance blocks, each recorded on their account`` () =
    task {
        use! env = newEnv ()
        env.App.Megafono.SeedBlock("", "old.example", true)

        // the account page links to it
        let! account = env.Boss.Get "/account"
        Assert.Contains("data-fediverse-link", account.Html)

        let! first = env.Boss.Get "/review/fediverse"
        Assert.Equal(HttpStatusCode.OK, first.Status)
        Assert.Contains("data-instance-blocks", first.Html)
        Assert.Equal<string list>([ "old.example" ], listed first.Html)

        let! byHandle =
            env.Boss.PostForm("/review/fediverse", "/review/fediverse/blocks/add", [ "target", "@troll@spam.example" ])

        Assert.Equal("/review/fediverse?msg=block-added", locationOf byHandle)
        let! byDomain = env.Boss.PostForm("/review/fediverse", "/review/fediverse/blocks/add", [ "target", "bad.example" ])
        Assert.Equal("/review/fediverse?msg=block-added", locationOf byDomain)
        let! again = env.Boss.PostForm("/review/fediverse", "/review/fediverse/blocks/add", [ "target", "bad.example" ])
        Assert.Equal("/review/fediverse?msg=block-already", locationOf again)

        // Megáfono's instance calls, not the user's
        Assert.Equal(3, env.App.Megafono.Count "POST /internal/blocks")
        Assert.Empty(env.App.Megafono.RequestsTo "POST /internal/actors/*/blocks")

        let! page = env.Boss.Get "/review/fediverse"
        Assert.Equal<string list>([ "bad.example"; "@troll@spam.example"; "old.example" ], listed page.Html)

        let! lifted =
            env.Boss.PostForm("/review/fediverse", "/review/fediverse/blocks/remove", [ "kind", "domain"; "target", "old.example" ])

        Assert.Equal("/review/fediverse?msg=block-removed", locationOf lifted)
        Assert.Equal<string list>([ "bad.example"; "@troll@spam.example" ], env.App.Megafono.BlocksOf "")

        let! recorded = events env.App

        Assert.Equal<string list>(
            [ "boss fediverse_block_added user"; "boss fediverse_block_added user"; "boss fediverse_block_removed user" ],
            recorded
        )
    }

[<Fact>]
let ``Megafono's refusals show on the operator page too and change nothing`` () =
    task {
        use! env = newEnv ()
        env.App.Megafono.Route("POST /internal/blocks", [ Status 400 ])
        let! invalid = env.Boss.PostForm("/review/fediverse", "/review/fediverse/blocks/add", [ "target", "mastodon.social" ])
        Assert.Equal("/review/fediverse?msg=block-invalid", locationOf invalid)
        let! page = env.Boss.Follow invalid
        Assert.Contains("data-block-error=\"block-invalid\"", page.Html)
        Assert.Contains(Texts.en.BlockNotice "block-invalid", page.Html)

        env.App.Megafono.Route("GET /internal/blocks", [ Status 503 ])
        let! down = env.Boss.Get "/review/fediverse"
        Assert.Equal(HttpStatusCode.OK, down.Status)
        Assert.Contains("data-block-load-failed", down.Html)

        let! recorded = events env.App
        Assert.Empty recorded
    }

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! body = response.Content.ReadAsStringAsync()
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", WebUtility.HtmlDecode body)
        Assert.DoesNotContain("data-instance-blocks", body)
        Assert.Null response.Headers.Location
    }

let private assertClosedFor (env: Env) (who: string) (browser: Browser) =
    task {
        let! before = events env.App
        let! page = browser.GetRaw "/review/fediverse"
        do! assertNotFound $"{who} GET" page

        for action in [ "/review/fediverse/blocks/add"; "/review/fediverse/blocks/remove" ] do
            let! withToken = browser.PostForm("/account", action, [ "kind", "domain"; "target", "spam.example" ])
            do! assertNotFound $"{who} POST {action} with a token" withToken
            let! withoutToken = browser.PostFormWithoutToken(action, [ "kind", "domain"; "target", "spam.example" ])
            do! assertNotFound $"{who} POST {action} without a token" withoutToken

        // a 429 would reveal the routes
        for i in 1..35 do
            let! response = browser.PostFormWithoutToken("/review/fediverse/blocks/add", [ "target", "spam.example" ])
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")

        let! after = events env.App
        Assert.Equal<string list>(before, after)
    }

[<Fact>]
let ``visitors and users who aren't operators get the plain 404 on every route and verb, never a 429`` () =
    task {
        use! env = newEnv ()
        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "user" env.Alice
        // nothing reached Megáfono, and the operator page is not in the account page of a non-operator
        Assert.Empty(env.App.Megafono.RequestsTo "* /internal/blocks")
        let! page = env.Alice.Get "/account"
        Assert.DoesNotContain("data-fediverse-link", page.Html)
    }

[<Fact>]
let ``an operator from the table gets in, and the instance blocks stay out of a user's list`` () =
    task {
        use! env = newEnv ()
        let! added = OperatorActions.add env.App.Db (Some "boss") "alice" OperatorActions.Server
        Assert.True(Result.isOk added)

        let! page = env.Alice.Get "/review/fediverse"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        let! response = env.Alice.PostForm("/review/fediverse", "/review/fediverse/blocks/add", [ "target", "spam.example" ])
        Assert.Equal("/review/fediverse?msg=block-added", locationOf response)

        // the account page lists only the user's own blocks
        let! account = env.Alice.Get "/account"
        Assert.Empty(listed account.Html)
    }
