module FrunchyBackend.Tests.CommentModerationTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 009, User Story 5: operators hide and unhide comments; outsiders can't tell the routes exist. The root
// is "boss" (`Frunchy:RootOperator`), "opr" is an operator from the table, "alice" writes, "bob" is a regular user.

type Env =
    { App: TestApp
      Ep: EpisodeRef
      Boss: Browser
      Opr: Browser
      Alice: Browser
      Bob: Browser
      Visitor: Browser
      /// alice's comment
      Id: string }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

let private pageOf (ep: EpisodeRef) = episodeUrl ep.Series ep.Episode

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

/// The 2026 sample, a root, a table operator, an author with one comment, a regular user and a visitor
let private newEnv () : Task<Env> =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db [ "Frunchy:RootOperator", "boss" ] ignore
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL AND e.title IS NOT NULL"
        let! boss = signedUp app "sub-boss" "boss"
        let! opr = signedUp app "sub-opr" "opr"
        let! alice = signedUp app "sub-a" "alice"
        let! bob = signedUp app "sub-b" "bob"
        let! added = OperatorActions.add app.Db (Some "boss") "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        let! _ = postComment alice ep.Series ep.Episode "alice's opinion" true

        let! id =
            Db.scalar<string> app.Db "SELECT c.id FROM comments c JOIN users u ON u.id = c.author_user_id WHERE u.username = 'alice'" []

        return
            { App = app
              Ep = ep
              Boss = boss
              Opr = opr
              Alice = alice
              Bob = bob
              Visitor = app.NewBrowser()
              Id = id }
    }

let private hide (env: Env) (browser: Browser) (id: string) = browser.PostForm(pageOf env.Ep, $"/comments/{id}/hide", [])
let private unhide (env: Env) (browser: Browser) (id: string) = browser.PostForm(pageOf env.Ep, $"/comments/{id}/unhide", [])

/// What hiding can change
let private snapshot (env: Env) =
    task {
        let! comments = rawCommentRows env.App.Db
        let! events = Db.dump env.App.Db "SELECT e::text FROM security_events e ORDER BY id"
        return comments, events
    }

let private commentEvents (env: Env) =
    Db.dump
        env.App.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'comment_%' ORDER BY e.id"

/// The `action` of the first `<form>` that carries the hook
let private formAction (hook: string) (html: string) : string option =
    Regex.Matches(html, "<form\\b[^>]*>")
    |> Seq.map (fun m -> m.Value)
    |> Seq.tryFind (hasHook hook)
    |> Option.map (fun tag -> Regex.Match(tag, "\\baction=\"([^\"]*)\"").Groups[1].Value)

let private theComment (html: string) = comments html |> List.exactlyOne

// ---------------------------------------------------------------------------
// Access
// ---------------------------------------------------------------------------

[<Fact>]
let ``a visitor, a regular user and the author get 404 on hide and unhide with or without a token, never a 429, and nothing changes`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! before = snapshot env

        let notFound (pending: Task<HttpResponseMessage>) =
            task {
                let! response = pending
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
            }

        for who in [ env.Visitor; env.Bob; env.Alice ] do
            do! notFound (hide env who env.Id)
            do! notFound (unhide env who env.Id)
            do! notFound (who.PostFormWithoutToken($"/comments/{env.Id}/hide", []))
            do! notFound (who.PostFormWithoutToken($"/comments/{env.Id}/unhide", []))

        // the same page as an operator's refusal: the plain 404
        let! response = hide env env.Bob env.Id
        let! html = readHtml response
        Assert.Contains(Texts.en.NotFoundTitle, html)

        // 35 POSTs, more than the operators' limit of 30 a minute: outsiders still never see a 429
        for i in 1..35 do
            let who = if i % 2 = 0 then env.Visitor else env.Bob
            let! response = if i % 3 = 0 then unhide env who env.Id else hide env who env.Id
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"POST {i} got {int response.StatusCode}")

        // unknown and malformed ids are 404 for them too
        do! notFound (hide env env.Bob "doesnotexist")
        do! notFound (hide env env.Bob "not!valid")

        let! after = snapshot env
        Assert.Equal(before, after)
    }

[<Fact>]
let ``operators get 404 for unknown, malformed and deleted comments`` () =
    task {
        let! env = newEnv ()
        use env = env

        let! unknown = hide env env.Boss "doesnotexist"
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode)
        let! malformed = hide env env.Boss "not!valid"
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode)

        let! deleted = env.Alice.PostForm(pageOf env.Ep, $"/comments/{env.Id}/delete", [])
        Assert.Equal(HttpStatusCode.Found, deleted.StatusCode)
        let! before = snapshot env
        let! gone = hide env env.Boss env.Id
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode)
        let! goneAgain = unhide env env.Opr env.Id
        Assert.Equal(HttpStatusCode.NotFound, goneAgain.StatusCode)
        let! after = snapshot env
        Assert.Equal(before, after)
    }

// ---------------------------------------------------------------------------
// Hiding, what each viewer sees
// ---------------------------------------------------------------------------

[<Fact>]
let ``a hidden comment is a placeholder for visitors and the author and the full text with controls for operators`` () =
    task {
        let! env = newEnv ()
        use env = env

        // before: operators see a hide control, nobody sees the placeholder, only operators see the note
        let! boss = env.Boss.Get(pageOf env.Ep)
        let visible = theComment boss.Html
        Assert.True(hasHook "comment-hide" visible.Html)
        Assert.False(hasHook "comment-unhide" visible.Html)
        Assert.False(visible.Hidden)
        Assert.Equal(Some $"/comments/{env.Id}/hide", formAction "comment-hide" visible.Html)
        Assert.True(hasHook "moderation-note" boss.Html)
        Assert.Contains(Texts.en.CommentModerationNote, boss.Html)

        // the root hides it
        let! response = hide env env.Boss env.Id
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal($"{pageOf env.Ep}#comment-{env.Id}", locationOf response)
        let! hiddenBy = Db.scalar<Guid> env.App.Db "SELECT hidden_by FROM comments" []
        let! boss' = Db.scalar<Guid> env.App.Db "SELECT id FROM users WHERE username = 'boss'" []
        Assert.Equal(boss', hiddenBy)

        for who in [ env.Visitor; env.Alice; env.Bob ] do
            let! page = who.Get(pageOf env.Ep)
            let c = theComment page.Html
            Assert.True(c.Hidden)
            Assert.Equal("alice", c.Author)
            Assert.Equal(env.Ep.Episode, c.Episode)
            Assert.True(hasHook "hidden-placeholder" c.Html)
            Assert.Contains(Texts.en.CommentHiddenPlaceholder, c.Text)
            Assert.DoesNotContain("alice's opinion", page.Html)
            Assert.False(c.Spoiler)
            Assert.False(hasHook "spoiler-summary" c.Html)
            Assert.False(hasHook "comment-unhide" c.Html)
            Assert.False(hasHook "comment-hide" c.Html)
            Assert.False(hasHook "moderation-note" page.Html)
            Assert.Equal("1", hookValue "thread" page.Html)

        for who in [ env.Boss; env.Opr ] do
            let! page = who.Get(pageOf env.Ep)
            let c = theComment page.Html
            Assert.True(c.Hidden)
            Assert.Contains("alice's opinion", c.Text)
            Assert.Contains(Texts.en.CommentHiddenPlaceholder, c.Text)
            Assert.False(hasHook "hidden-placeholder" c.Html)
            Assert.True(hasHook "comment-unhide" c.Html)
            Assert.False(hasHook "comment-hide" c.Html)
            Assert.Equal(Some $"/comments/{env.Id}/unhide", formAction "comment-unhide" c.Html)
            Assert.True(hasHook "moderation-note" page.Html)
            Assert.Equal("1", hookValue "thread" page.Html)

        // the author still has edit and delete on it
        let! asAlice = env.Alice.Get(pageOf env.Ep)
        let mine = theComment asAlice.Html
        Assert.True(hasHook "comment-edit" mine.Html)
        Assert.True(hasHook "comment-delete" mine.Html)
    }

[<Fact>]
let ``a table operator hides too, and the 429 limit counts operators only`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! response = hide env env.Opr env.Id
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        let! hiddenBy = Db.scalar<string> env.App.Db "SELECT u.username FROM comments c JOIN users u ON u.id = c.hidden_by" []
        Assert.Equal("opr", hiddenBy)

        // operators are limited to 30 POSTs a minute
        let mutable limited = false

        for _ in 1..35 do
            let! r = hide env env.Opr env.Id
            if r.StatusCode = HttpStatusCode.TooManyRequests then limited <- true

        Assert.True(limited)
    }

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

[<Fact>]
let ``hiding and unhiding record events on the author's account and the operator's, and repeating either records nothing`` () =
    task {
        let! env = newEnv ()
        use env = env

        let! _ = hide env env.Boss env.Id
        let! afterHide = commentEvents env
        Assert.Equal<string list>([ "alice comment_hidden operator"; "boss comment_hidden user" ], afterHide)

        // hiding what is hidden: a redirect, no second event, hidden_at unchanged
        let! before = snapshot env
        let! again = hide env env.Opr env.Id
        Assert.Equal(HttpStatusCode.Found, again.StatusCode)
        Assert.Equal($"{pageOf env.Ep}#comment-{env.Id}", locationOf again)
        let! after = snapshot env
        Assert.Equal(before, after)

        let! unhidden = unhide env env.Opr env.Id
        Assert.Equal(HttpStatusCode.Found, unhidden.StatusCode)
        Assert.Equal($"{pageOf env.Ep}#comment-{env.Id}", locationOf unhidden)

        let! events = commentEvents env

        Assert.Equal<string list>(
            [ "alice comment_hidden operator"
              "boss comment_hidden user"
              "alice comment_unhidden operator"
              "opr comment_unhidden user" ],
            events
        )

        // unhiding what is visible: nothing
        let! before = snapshot env
        let! again = unhide env env.Boss env.Id
        Assert.Equal(HttpStatusCode.Found, again.StatusCode)
        let! after = snapshot env
        Assert.Equal(before, after)
    }

// ---------------------------------------------------------------------------
// The author's rights on a hidden comment
// ---------------------------------------------------------------------------

[<Fact>]
let ``the author can edit a hidden comment, it stays hidden, and can delete it`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! _ = hide env env.Boss env.Id
        let! hiddenAt = Db.scalar<string> env.App.Db "SELECT hidden_at::text FROM comments" []

        let! page = env.Alice.Get $"/comments/{env.Id}/edit"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        let! edited = env.Alice.PostForm($"/comments/{env.Id}/edit", $"/comments/{env.Id}/edit", [ "content", "alice rewrote it" ])
        Assert.Equal(HttpStatusCode.Found, edited.StatusCode)

        let! stored = Db.dump env.App.Db "SELECT content, hidden_at::text, (hidden_by IS NOT NULL)::text FROM comments"
        Assert.Equal<string list>([ $"alice rewrote it|{hiddenAt}|true" ], stored)

        let! visitor = env.Visitor.Get(pageOf env.Ep)
        Assert.True((theComment visitor.Html).Hidden)
        Assert.DoesNotContain("alice rewrote it", visitor.Html)
        let! asBoss = env.Boss.Get(pageOf env.Ep)
        Assert.Contains("alice rewrote it", (theComment asBoss.Html).Text)

        let! deleted = env.Alice.PostForm(pageOf env.Ep, $"/comments/{env.Id}/delete", [])
        Assert.Equal(HttpStatusCode.Found, deleted.StatusCode)
        let! gone = env.Visitor.Get(pageOf env.Ep)
        Assert.Equal<string list>([], comments gone.Html |> List.map _.Id)
        Assert.Equal("0", hookValue "thread" gone.Html)
    }

// ---------------------------------------------------------------------------
// Operator accounts and unhide
// ---------------------------------------------------------------------------

[<Fact>]
let ``when the operator's account is deleted the hide stays with hidden_by empty`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! _ = hide env env.Opr env.Id

        let! deleted = env.Opr.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "opr" ])
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode)

        let! stored = Db.dump env.App.Db "SELECT (hidden_at IS NOT NULL)::text, coalesce(hidden_by::text, '-') FROM comments"
        Assert.Equal<string list>([ "true|-" ], stored)
        let! page = env.Visitor.Get(pageOf env.Ep)
        Assert.True(hasHook "hidden-placeholder" (theComment page.Html).Html)
    }

[<Fact>]
let ``unhiding restores the comment exactly as it was`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! before = rawCommentRows env.App.Db
        let! visitorBefore = env.Visitor.Get(pageOf env.Ep)

        let! _ = hide env env.Boss env.Id
        let! hiddenRows = rawCommentRows env.App.Db
        Assert.NotEqual<string list>(before, hiddenRows)
        let! _ = unhide env env.Opr env.Id

        let! after = rawCommentRows env.App.Db
        Assert.Equal<string list>(before, after)
        let! visitorAfter = env.Visitor.Get(pageOf env.Ep)
        let c = theComment visitorAfter.Html
        Assert.False(c.Hidden)
        Assert.False(hasHook "hidden-placeholder" c.Html)
        Assert.True(c.Spoiler)
        Assert.Equal((theComment visitorBefore.Html).Text, c.Text)
    }
