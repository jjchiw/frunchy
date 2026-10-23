/// Feature 015 (US3, T010): how remote comments (replies from the fediverse) look and behave on the episode page.
/// They arrive through the signed webhook (MegafonoWebhooksTests holds that protocol); here the page is read through
/// its `data-*` hooks. Remote content is turned into plain text and only ever shown escaped.
module FrunchyBackend.Tests.RemoteCommentsTests

open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MegafonoWebhooksTests
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private pageUrl (env: Env) = episodeUrl env.Ep.Series env.Ep.Episode

/// The page as the server wrote it (entities not decoded), for checks on escaping
let private rawPage (env: Env) : Task<string> =
    task {
        use! response = getWithLanguage env.App.Client "en" (pageUrl env)
        return! response.Content.ReadAsStringAsync()
    }

/// The remote comment of the page (the only one with `data-remote`), as a visitor sees it
let private remoteOf (shown: PageComment list) =
    shown |> List.filter (fun c -> hasHook "remote" c.Html) |> List.exactlyOne

let private tagWith (hook: string) (html: string) =
    Regex.Matches(html, "<[a-zA-Z0-9]+\\b[^>]*>")
    |> Seq.map (fun m -> m.Value)
    |> Seq.find (hasHook hook)

// ---------------------------------------------------------------------------
// What a remote comment shows
// ---------------------------------------------------------------------------

[<Fact>]
let ``a remote comment shows the author's name and handle as a profile link, the time and the text`` () =
    task {
        let! env = newEnv ()
        use env = env

        let r =
            { reply env.ThreadId 1 with
                Content = "<p>first <b>bold</b> &amp; <a href=\"https://evil.example/x\">a link</a></p><p>second<br>line</p>"
                Updated = "2026-10-12T18:30:00Z" }

        do! deliver env (receivedJson r)
        let! shown = pageComments env
        Assert.Equal(2, shown.Length)

        // oldest first: Bob's reply is dated before Alice's comment (made now); the hooks of 009 plus data-remote and the handle
        let bob, alice = shown[0], shown[1]
        Assert.Equal("alice", alice.Author)
        Assert.False(hasHook "remote" alice.Html)
        Assert.Equal(Fediverse.remoteCommentId r.ObjectId, bob.Id)
        Assert.Equal("@bob@mastodon.example", bob.Author)
        Assert.True(hasHook "remote" bob.Html)
        Assert.Equal(env.Ep.Episode, bob.Episode)
        Assert.Equal("2026-10-12T17:59:58Z", bob.Created)
        Assert.True(bob.Edited, "updated is set, so the comment says edited")
        Assert.False(bob.Spoiler)
        Assert.False(bob.Hidden)

        // the author: name and handle inside one link to the profile, in a new tab, nofollow ugc
        let link = tagWith "remote-author" bob.Html
        Assert.StartsWith("<a ", link)
        Assert.Contains("href=\"https://mastodon.example/@bob\"", link)
        Assert.Contains("rel=\"noopener nofollow ugc\"", link)
        Assert.Contains("target=\"_blank\"", link)
        Assert.Contains("Bob", bob.Text)
        Assert.Contains("@bob@mastodon.example", bob.Text)

        // the time element of every comment
        Assert.Matches("<time [^>]*datetime=\"2026-10-12T17:59:58Z\"", bob.Html)

        // the text: tags stripped, entities decoded, paragraphs and <br> as line breaks, nothing clickable inside it
        Assert.Contains("first bold & a link\nsecond\nline", bob.Html)
        Assert.DoesNotContain("evil.example", bob.Html)
        Assert.DoesNotContain("<b>", bob.Html)
        Assert.Equal(1, Regex.Matches(bob.Html, "<a\\b").Count) // only the author link

        // a profile URL that isn't https is never a link
        do! Db.execute env.App.Db "UPDATE remote_actors SET profile_url = 'http://mastodon.example/@bob'"
        let! shown = pageComments env
        let bob = remoteOf shown
        Assert.True(hasHook "remote-author" bob.Html)
        Assert.StartsWith("<span ", tagWith "remote-author" bob.Html)
        Assert.DoesNotContain("<a ", bob.Html)
        Assert.Contains("Bob", bob.Text)
    }

[<Fact>]
let ``the spoiler toggle follows the sensitive flag and the content warning`` () =
    task {
        let! env = newEnv ()
        use env = env

        do! deliver env (receivedJson { reply env.ThreadId 1 with Summary = "Spoilers: S2 E5"; Sensitive = true; Content = "<p>sensitive words</p>" })
        // a warning text without the flag still hides the text
        do! deliver env (receivedJson { reply env.ThreadId 2 with Summary = "CW: ending"; Sensitive = false; Content = "<p>warned words</p>" })
        do! deliver env (receivedJson { reply env.ThreadId 3 with Content = "<p>plain words</p>" })

        let! shown = pageComments env
        let byText (word: string) = shown |> List.find (fun c -> c.Text.Contains word)

        let sensitive = byText "sensitive words"
        Assert.True(sensitive.Spoiler)
        Assert.Contains("Spoilers: S2 E5", sensitive.Html)
        Assert.Contains("data-spoiler-summary", sensitive.Html)

        let warned = byText "warned words"
        Assert.True(warned.Spoiler)
        Assert.Contains("CW: ending", warned.Html)

        Assert.False((byText "plain words").Spoiler)
    }

[<Fact>]
let ``markup in remote content shows as text and no remote markup reaches the page`` () =
    task {
        let! env = newEnv ()
        use env = env

        // a real tag is stripped (its text stays); an escaped one is text and stays escaped
        do! deliver env (receivedJson { reply env.ThreadId 1 with Content = "<p>hi <script>alert(1)</script> <img src=x onerror=alert(2)></p>" })
        do! deliver env (receivedJson { reply env.ThreadId 2 with Content = "<p>&lt;script&gt;alert(3)&lt;/script&gt; &lt;b&gt;shown&lt;/b&gt;</p>"; Name = "<i>Mallory</i>" })

        let! raw = rawPage env
        // The comment thread, where remote content lands: the page itself legitimately carries Frunchy's own
        // markup there is none of in the thread (the poster's onerror fallback, its img)
        let thread =
            let m = System.Text.RegularExpressions.Regex.Match(raw, "<section [^>]*data-thread=[\\s\\S]*?</section>")
            Assert.True(m.Success, "the comment thread section is on the page")
            m.Value

        Assert.DoesNotContain("<script>alert", raw)
        Assert.DoesNotContain("onerror", thread)
        Assert.DoesNotContain("<img", thread)
        Assert.DoesNotContain("<img src=x", raw)
        Assert.DoesNotContain("<b>shown", raw)
        Assert.DoesNotContain("<i>Mallory", raw)
        Assert.Contains("&lt;script&gt;alert(3)&lt;/script&gt; &lt;b&gt;shown&lt;/b&gt;", raw)
        Assert.Contains("&lt;i&gt;Mallory&lt;/i&gt;", raw)

        let! shown = pageComments env
        Assert.Contains(shown, fun c -> c.Text.Contains "hi alert(1)")
    }

[<Fact>]
let ``remote comments get no author controls for anyone, and the edit and delete routes refuse them`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (receivedJson r)
        let id = Fediverse.remoteCommentId r.ObjectId

        // alice is signed in: her own comment has edit and delete, Bob's remote one has neither
        let! page = env.Alice.Get(pageUrl env)
        let mine, theirs = comments page.Html |> List.partition (fun c -> not (hasHook "remote" c.Html))
        Assert.True(hasHook "comment-edit" mine.Head.Html)
        Assert.True(hasHook "comment-delete" mine.Head.Html)
        Assert.False(hasHook "comment-edit" theirs.Head.Html)
        Assert.False(hasHook "comment-delete" theirs.Head.Html)
        Assert.False(hasHook "comment-hide" theirs.Head.Html)

        let! before = rawCommentRows env.App.Db
        let! edit = env.Alice.Get $"/comments/{id}/edit"
        Assert.Equal(HttpStatusCode.NotFound, edit.Status)
        let! delete = env.Alice.PostForm(pageUrl env, $"/comments/{id}/delete", [])
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode)
        let! save = env.Alice.PostForm(pageUrl env, $"/comments/{id}/edit", [ "content", "mine now" ])
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode)
        let! after = rawCommentRows env.App.Db
        Assert.Equal<string list>(before, after)

        // nothing about it was queued for Megáfono
        let! queued = Db.dump env.App.Db $"SELECT kind, subject FROM fediverse_outbox WHERE subject = '{id}'"
        Assert.Empty queued
    }

// ---------------------------------------------------------------------------
// Moderation: an operator hides and unhides a remote comment like any comment
// ---------------------------------------------------------------------------

[<Fact>]
let ``an operator hides and unhides a remote comment, and everyone else sees a placeholder`` () =
    task {
        let! env = newEnvWith [ "Frunchy:RootOperator", "boss" ]
        use env = env
        use! boss = signedUp env.App "sub-boss" "boss"
        let r = { reply env.ThreadId 1 with Content = "<p>rude words</p>" }
        do! deliver env (receivedJson r)
        let id = Fediverse.remoteCommentId r.ObjectId

        // an operator sees the hide control on it; nobody else does
        let! asBoss = boss.Get(pageUrl env)
        let bob = remoteOf (comments asBoss.Html)
        Assert.True(hasHook "comment-hide" bob.Html)
        Assert.False(hasHook "comment-edit" bob.Html)

        let! hidden = boss.PostForm(pageUrl env, $"/comments/{id}/hide", [])
        Assert.Equal(HttpStatusCode.Found, hidden.StatusCode)
        Assert.Equal($"{pageUrl env}#comment-{id}", string hidden.Headers.Location)

        // the row is flagged, not deleted
        let! flagged = Db.dump env.App.Db $"SELECT hidden_at IS NOT NULL, hidden_by IS NOT NULL, deleted_at IS NULL, content FROM comments WHERE id = '{id}'"
        Assert.Equal<string list>([ "True|True|True|<p>rude words</p>" ], flagged)

        // visitors and the local author: a placeholder, no text, no remote handle
        let! asVisitor = pageComments env
        let hiddenForVisitor = remoteOf asVisitor
        Assert.True(hiddenForVisitor.Hidden)
        Assert.True(hasHook "hidden-placeholder" hiddenForVisitor.Html)
        Assert.DoesNotContain("rude words", hiddenForVisitor.Html)
        let! asAlice = env.Alice.Get(pageUrl env)
        Assert.DoesNotContain("rude words", asAlice.Html)

        // the operator still sees the text, and an unhide control
        let! asBoss = boss.Get(pageUrl env)
        let seen = remoteOf (comments asBoss.Html)
        Assert.True(seen.Hidden)
        Assert.Contains("rude words", seen.Text)
        Assert.True(hasHook "comment-unhide" seen.Html)

        let! shownAgain = boss.PostForm(pageUrl env, $"/comments/{id}/unhide", [])
        Assert.Equal(HttpStatusCode.Found, shownAgain.StatusCode)
        let! asVisitor = pageComments env
        let back = remoteOf asVisitor
        Assert.False(back.Hidden)
        Assert.Contains("rude words", back.Text)

        // the operator's own account holds the events (a remote author has no account here)
        let! actors = Db.dump env.App.Db "SELECT u.username || ' ' || e.event FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE 'comment_%' ORDER BY e.id"
        Assert.Equal<string list>([ "boss comment_hidden"; "boss comment_unhidden" ], actors)
    }

[<Fact>]
let ``a hidden remote comment stays hidden when Megafono sends it again, and a later edit does not unhide it`` () =
    task {
        let! env = newEnvWith [ "Frunchy:RootOperator", "boss" ]
        use env = env
        use! boss = signedUp env.App "sub-boss" "boss"
        let r = { reply env.ThreadId 1 with Content = "<p>rude words</p>" }
        do! deliver env (receivedJson r)
        let! _ = boss.PostForm(pageUrl env, $"/comments/{Fediverse.remoteCommentId r.ObjectId}/hide", [])

        do! deliver env (updatedJson { r with EventId = "evt-2"; Content = "<p>ruder words</p>"; Updated = "2026-10-12T19:00:00Z" })

        let! shown = pageComments env
        let bob = remoteOf shown
        Assert.True(bob.Hidden)
        Assert.DoesNotContain("ruder words", bob.Html)
        let! asBoss = boss.Get(pageUrl env)
        Assert.Contains("ruder words", (remoteOf (comments asBoss.Html)).Text)
    }
