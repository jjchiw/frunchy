module FrunchyBackend.Tests.CommentEditTests

open System
open System.Net
open System.Net.Http
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

// Feature 009, User Story 4: authors edit and delete their comments; nobody else can (SC-006). Routes:
// GET/POST /comments/{id}/edit, POST /comments/{id}/delete. `comment-write` is 10 POSTs a minute per IP and counts
// every POST, so each test stays under it.

let private numberedEpisode (app: TestApp) =
    pickEpisode app.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL AND e.title IS NOT NULL"

let private pageOf (ep: EpisodeRef) = episodeUrl ep.Series ep.Episode

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

/// A signed-up user whose browser speaks the given language
let private signedUpIn (app: TestApp) (language: string) (subject: string) (username: string) : Task<Browser> =
    task {
        let! invite = createInvite app
        let browser = app.NewBrowser language
        let! response = submitSignUp browser subject invite username (username.ToUpperInvariant())
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        return browser
    }

/// The id of the only comment by this author
let private commentOf (app: TestApp) (username: string) : Task<string> =
    Db.scalar<string>
        app.Db
        "SELECT c.id FROM comments c JOIN users u ON u.id = c.author_user_id WHERE u.username = @u ORDER BY c.created_at, c.id LIMIT 1"
        [ "u", box username ]

/// The token comes from a page the browser can load (the episode page when the edit page is refused)
let private edit (browser: Browser) (tokenPage: string) (id: string) (fields: (string * string) list) =
    browser.PostForm(tokenPage, $"/comments/{id}/edit", fields)

let private delete (browser: Browser) (tokenPage: string) (id: string) = browser.PostForm(tokenPage, $"/comments/{id}/delete", [])

let private row (app: TestApp) (id: string) =
    Db.dump
        app.Db
        $"SELECT content, coalesce(summary, '-'), sensitive::text, audience, coalesce(in_reply_to, '-'), coalesce(edited_at::text, '-'), coalesce(deleted_at::text, '-') FROM comments WHERE id = '{id}'"

let private toggle (html: string) =
    System.Text.RegularExpressions.Regex.Match(html, "<input\\b[^>]*data-spoiler-toggle[^>]*>").Value

// ---------------------------------------------------------------------------
// Edit
// ---------------------------------------------------------------------------

[<Fact>]
let ``the author edits the text and the spoiler flag: edited shows, the summary follows the flag in the editor's language, audience and reply are untouched`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        let! facts = episodeFacts app.Db ep
        use! alice = signedUpIn app "es" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "first draft" true
        let! id = commentOf app "alice"

        // the other columns an edit must never touch
        do! Db.execute app.Db $"UPDATE comments SET audience = 'unlisted', in_reply_to = 'some-parent' WHERE id = '{id}'"

        // the edit page: the text prefilled, the flag as stored, a cancel link back to the comment
        let! page = alice.Get $"/comments/{id}/edit"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Equal(id, hookValue "comment-edit-page" page.Html)
        Assert.Contains(">first draft</textarea>", page.Html)
        Assert.Contains("checked", toggle page.Html)
        Assert.True(hasHook "comment-form" page.Html)
        Assert.Equal(Some $"{pageOf ep}#comment-{id}", hrefOfHook "comment-cancel" page.Html)
        Assert.Contains("__RequestVerificationToken", page.Html)

        // untick the spoiler flag and change the text
        let! response = edit alice $"/comments/{id}/edit" id [ "content", "  second draft\r\nwith a line  " ]
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal($"{pageOf ep}#comment-{id}", locationOf response)
        let! stored = row app id
        let p = (List.exactlyOne stored).Split '|'
        Assert.Equal("second draft\nwith a line", p[0])
        Assert.Equal("-", p[1])
        Assert.Equal("false", p[2])
        Assert.Equal("unlisted", p[3])
        Assert.Equal("some-parent", p[4])
        Assert.NotEqual<string>("-", p[5])
        Assert.Equal("-", p[6])

        let! _, html = episodePage app "en" ep.Series ep.Episode
        let c = comments html |> List.exactlyOne
        Assert.True(c.Edited)
        Assert.False(c.Spoiler)

        // tick it again: the warning is computed now, in the editor's language (Spanish here)
        let! again = edit alice $"/comments/{id}/edit" id [ "content", "second draft\nwith a line"; "spoiler", "on" ]
        Assert.Equal(HttpStatusCode.Found, again.StatusCode)
        let number = if facts.Number = "" then None else Some(int facts.Number)
        let season = if facts.Season = "" then None else Some facts.Season
        let! stored = row app id
        let p = (List.exactlyOne stored).Split '|'
        Assert.Equal(Texts.es.SpoilerSummary season number None, p[1])
        Assert.Equal("true", p[2])
        Assert.Equal("unlisted", p[3])
        Assert.Equal("some-parent", p[4])
        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.True((comments html |> List.exactlyOne).Spoiler)
    }

[<Fact>]
let ``an edit that changes nothing leaves edited_at alone`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "same text" false
        let! id = commentOf app "alice"
        let! before = rawCommentRows app.Db

        // same text (after normalizing) and the same flag
        let! response = edit alice $"/comments/{id}/edit" id [ "content", "  same text  " ]
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal($"{pageOf ep}#comment-{id}", locationOf response)
        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)

        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.False((comments html |> List.exactlyOne).Edited)

        // a real edit sets it, and repeating that one doesn't move it
        let! _ = edit alice $"/comments/{id}/edit" id [ "content", "changed" ]
        let! edited = rawCommentRows app.Db
        let! _ = edit alice $"/comments/{id}/edit" id [ "content", "changed" ]
        let! repeated = rawCommentRows app.Db
        Assert.NotEqual<string list>(before, edited)
        Assert.Equal<string list>(edited, repeated)
    }

[<Fact>]
let ``a comment that stays a spoiler keeps its warning when edited in another language`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "es" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "same text" true
        let! id = commentOf app "alice"

        // as if it had been written in English
        do! Db.execute app.Db $"UPDATE comments SET summary = 'Spoilers: written in English' WHERE id = '{id}'"
        let! before = rawCommentRows app.Db

        // saved again from the Spanish browser, unchanged: nothing moves, edited_at included
        let! response = edit alice $"/comments/{id}/edit" id [ "content", "same text"; "spoiler", "on" ]
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)

        // a text change keeps the warning too
        let! _ = edit alice $"/comments/{id}/edit" id [ "content", "new text"; "spoiler", "on" ]
        let! stored = row app id
        Assert.Equal("Spoilers: written in English", (List.exactlyOne stored).Split('|')[1])
    }

[<Fact>]
let ``an invalid edit gives 422 with the edit page, the error and the typed text, and nothing changes`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "keep me" false
        let! id = commentOf app "alice"
        let! before = rawCommentRows app.Db

        let refused (key: string) (typed: string) (fields: (string * string) list) =
            task {
                let! response = edit alice $"/comments/{id}/edit" id fields
                let! html = readHtml response
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode)
                Assert.Equal(id, hookValue "comment-edit-page" html)
                Assert.Equal(key, hookValue "comment-error" html)
                Assert.Contains(Texts.en.CommentError key, html)
                Assert.Contains($">{typed}</textarea>", html)
                let! after = rawCommentRows app.Db
                Assert.Equal<string list>(before, after)
            }

        do! refused "empty" "" [ "content", "" ]
        do! refused "empty" "  \n\t " [ "content", "  \n\t " ]
        let tooLong = String.replicate 2001 "a"
        do! refused "too-long" tooLong [ "content", tooLong ]

        // the spoiler choice is kept too
        let! response = edit alice $"/comments/{id}/edit" id [ "content", ""; "spoiler", "on" ]
        let! html = readHtml response
        Assert.Contains("checked", toggle html)
    }

// ---------------------------------------------------------------------------
// Delete
// ---------------------------------------------------------------------------

[<Fact>]
let ``deleting leaves a tombstone, takes the comment off the page and a create with its id stores nothing`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"
        let! _ = postComment alice ep.Series ep.Episode "alice's secret" true
        let! _ = postComment bob ep.Series ep.Episode "bob stays" false
        let! id = commentOf app "alice"

        let! response = delete alice (pageOf ep) id
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal(pageOf ep, locationOf response)

        let! stored = row app id
        let p = (List.exactlyOne stored).Split '|'
        Assert.Equal("", p[0])
        Assert.Equal("-", p[1])
        Assert.Equal("false", p[2])
        Assert.NotEqual<string>("-", p[6])

        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.Equal<string list>([ "bob" ], comments html |> List.map _.Author)
        Assert.Equal("1", hookValue "thread" html)
        Assert.DoesNotContain("alice's secret", html)

        // a create reusing the tombstone's id changes nothing
        let! before = rawCommentRows app.Db
        let! reuse = alice.PostForm(pageOf ep, $"{pageOf ep}/comments", [ "id", id; "content", "back from the dead" ])
        Assert.True(reuse.StatusCode = HttpStatusCode.Found || reuse.StatusCode = HttpStatusCode.UnprocessableEntity)
        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)
    }

[<Fact>]
let ``a deleted comment's edit and delete are 404`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "bye" false
        let! id = commentOf app "alice"
        let! _ = delete alice (pageOf ep) id
        let! before = rawCommentRows app.Db

        let! page = alice.Get $"/comments/{id}/edit"
        Assert.Equal(HttpStatusCode.NotFound, page.Status)
        let! e = edit alice (pageOf ep) id [ "content", "revived" ]
        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode)
        let! d = delete alice (pageOf ep) id
        Assert.Equal(HttpStatusCode.NotFound, d.StatusCode)
        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// Refusals (SC-006)
// ---------------------------------------------------------------------------

[<Fact>]
let ``another user, a signed-out visitor and an operator get 404 on every edit and delete route, and nothing changes`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"
        use! opr = signedUpIn app "en" "sub-o" "opr"
        let! added = OperatorActions.add app.Db None "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        use visitor = app.NewBrowser()
        let! _ = postComment alice ep.Series ep.Episode "alice's words" false
        let! id = commentOf app "alice"
        let! before = rawCommentRows app.Db

        let notFound (pending: Task<HttpResponseMessage>) =
            task {
                let! response = pending
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
                let! html = readHtml response
                Assert.Contains(Texts.en.NotFoundTitle, html)
            }

        for who in [ bob; opr ] do
            let! page = who.Get $"/comments/{id}/edit"
            Assert.Equal(HttpStatusCode.NotFound, page.Status)
            Assert.Contains(Texts.en.NotFoundTitle, page.Html)
            do! notFound (edit who (pageOf ep) id [ "content", "hijacked" ])
            do! notFound (delete who (pageOf ep) id)

        let! page = visitor.Get $"/comments/{id}/edit"
        Assert.Equal(HttpStatusCode.NotFound, page.Status)
        do! notFound (visitor.PostFormWithoutToken($"/comments/{id}/edit", [ "content", "hijacked" ]))
        do! notFound (visitor.PostFormWithoutToken($"/comments/{id}/delete", []))

        // malformed ids are 404 too
        let! bad = alice.Get "/comments/not!valid/edit"
        Assert.Equal(HttpStatusCode.NotFound, bad.Status)
        let! unknown = alice.Get "/comments/doesnotexist/edit"
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status)

        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// Controls
// ---------------------------------------------------------------------------

[<Fact>]
let ``only the author sees the edit and delete controls`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"
        use! opr = signedUpIn app "en" "sub-o" "opr"
        let! added = OperatorActions.add app.Db None "opr" OperatorActions.Server
        Assert.True(Result.isOk added)
        use visitor = app.NewBrowser()
        let! _ = postComment alice ep.Series ep.Episode "alice's words" false
        let! _ = postComment bob ep.Series ep.Episode "bob's words" false
        let! aliceId = commentOf app "alice"
        let! bobId = commentOf app "bob"

        let controls (html: string) (author: string) =
            let c = comments html |> List.find (fun c -> c.Author = author)
            hasHook "comment-edit" c.Html, hasHook "comment-delete" c.Html, c

        let! asAlice = alice.Get(pageOf ep)
        let editA, deleteA, mine = controls asAlice.Html "alice"
        Assert.True(editA)
        Assert.True(deleteA)
        Assert.Equal(Some $"/comments/{aliceId}/edit", hrefOfHook "comment-edit" mine.Html)
        Assert.Contains($"action=\"/comments/{aliceId}/delete\"", mine.Html)
        Assert.Contains("__RequestVerificationToken", mine.Html)
        let editB, deleteB, _ = controls asAlice.Html "bob"
        Assert.False(editB)
        Assert.False(deleteB)

        for who in [ bob; opr; visitor ] do
            let! page = who.Get(pageOf ep)
            let e, d, _ = controls page.Html "alice"
            Assert.False(e)
            Assert.False(d)

        let! asBob = bob.Get(pageOf ep)
        let e, d, theirs = controls asBob.Html "bob"
        Assert.True(e)
        Assert.True(d)
        Assert.Equal(Some $"/comments/{bobId}/edit", hrefOfHook "comment-edit" theirs.Html)
    }
