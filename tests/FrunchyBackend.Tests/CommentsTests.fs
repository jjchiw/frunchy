module FrunchyBackend.Tests.CommentsTests

open System
open System.Diagnostics
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Views
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 009, User Story 2: reading and writing comments (contracts/episode-page.md). The 2026 sample, a
// numbered episode with a season, and users who sign up through the fake provider.

let private numberedEpisode (db: FrunchyBackend.Infrastructure.Database.DbConfig) =
    pickEpisode db "e.episode_number IS NOT NULL AND sea.season_number IS NOT NULL AND e.title IS NOT NULL"

/// A signed-up user whose browser speaks the given language
let private signedUpIn (app: TestApp) (language: string) (subject: string) (username: string) =
    task {
        let! invite = createInvite app
        let browser = app.NewBrowser language
        let! response = submitSignUp browser subject invite username (username.ToUpperInvariant())
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        return browser
    }

let private rows (app: TestApp) =
    Db.dump app.Db "SELECT content, coalesce(summary, '-'), sensitive::text FROM comments ORDER BY created_at, id"

let private locationOf (response: System.Net.Http.HttpResponseMessage) = string response.Headers.Location

let private pageOf (ep: EpisodeRef) = episodeUrl ep.Series ep.Episode

/// A POST to the comment route with explicit fields (to forge ids and the like)
let private postFields (browser: FrunchyBackend.Tests.Infrastructure.Browser.Browser) (ep: EpisodeRef) (fields: (string * string) list) =
    browser.PostForm(pageOf ep, $"{pageOf ep}/comments", fields)

let private isoPattern = Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")

/// Every <details> of a page that carries data-spoiler, as its opening tag
let private spoilerTags (html: string) =
    [ for m in Regex.Matches(html, "<details\\b[^>]*>") do
          if hasHook "spoiler" m.Value then
              m.Value ]

// ---------------------------------------------------------------------------
// Create, order, spoilers (SC-002, SC-003)
// ---------------------------------------------------------------------------

[<Fact>]
let ``two users comment: the thread is oldest first, the spoiler box starts ticked and the warning is stored in the author's language`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        let! facts = episodeFacts app.Db ep
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "es" "sub-b" "bob"

        // the form: ticked by default, with a minted id of 22 base64url characters
        let! form = alice.Get(pageOf ep)
        Assert.Equal(HttpStatusCode.OK, form.Status)
        let toggle = Regex.Match(form.Html, "<input\\b[^>]*data-spoiler-toggle[^>]*>").Value
        Assert.NotEqual<string>("", toggle)
        Assert.Contains("checked", toggle)
        Assert.Matches("^[A-Za-z0-9_-]{22}$", (formCommentId form.Html).Value)

        // alice leaves it ticked; bob unticks it
        let! first = postComment alice ep.Series ep.Episode "Alice's take on the ending" true
        Assert.Equal(HttpStatusCode.Found, first.StatusCode)
        let! second = postComment bob ep.Series ep.Episode "Bob's take" false
        Assert.Equal(HttpStatusCode.Found, second.StatusCode)

        let! _, html = episodePage app "en" ep.Series ep.Episode
        let thread = comments html
        Assert.Equal<string list>([ "alice"; "bob" ], thread |> List.map _.Author)
        Assert.Equal<string list>([ ep.Episode; ep.Episode ], thread |> List.map _.Episode)
        Assert.All(thread, fun c -> Assert.Matches(isoPattern, c.Created))
        Assert.True(thread[0].Created <= thread[1].Created)
        Assert.Contains("Alice's take on the ending", thread[0].Text)
        Assert.Contains("Bob's take", thread[1].Text)
        Assert.Equal("2", hookValue "thread" html)
        Assert.False(hasHook "thread-empty" html)
        Assert.Equal<bool list>([ true; false ], thread |> List.map _.Spoiler)

        // each comment is where the redirect said, and its author links to the dashboard
        Assert.Equal($"{pageOf ep}#comment-{thread[0].Id}", locationOf first)
        Assert.Equal($"{pageOf ep}#comment-{thread[1].Id}", locationOf second)
        Assert.Contains("href=\"/u/alice\"", thread[0].Html)

        // stored: alice's warning in English (her language), bob's none
        let number = if facts.Number = "" then None else Some(int facts.Number)
        let season = if facts.Season = "" then None else Some facts.Season
        let! stored = rows app
        Assert.Equal<string list>([ $"Alice's take on the ending|{Texts.en.SpoilerSummary season number None}|true"; "Bob's take|-|false" ], stored)
        Assert.StartsWith("Spoilers: S", Texts.en.SpoilerSummary season number None)

        // the warning shows closed, with the stored text
        Assert.Contains(Texts.en.SpoilerSummary season number None, thread[0].Text)
        Assert.Contains(Texts.en.CommentShowSpoiler, thread[0].Text)

        // an author writing in Spanish gets the Spanish warning
        let! third = postComment bob ep.Series ep.Episode "Otra cosa" true
        Assert.Equal(HttpStatusCode.Found, third.StatusCode)
        let! stored = rows app
        Assert.Equal($"Otra cosa|{Texts.es.SpoilerSummary season number None}|true", List.last stored)
        Assert.StartsWith("Spoilers: T", Texts.es.SpoilerSummary season number None)
    }

[<Fact>]
let ``a spoiler on a numberless episode names its label`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = pickEpisode app.Db "e.episode_number IS NULL"
        let! label = Db.scalar<string> app.Db $"SELECT coalesce(episode_label, '') FROM cr_episodes WHERE id = '{ep.Episode}'" []
        let! facts = episodeFacts app.Db ep
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! response = postComment alice ep.Series ep.Episode "About the special" true
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        let season = if facts.Season = "" then None else Some facts.Season
        let expected = Texts.en.SpoilerSummary season None (if label = "" then None else Some label)
        let! stored = rows app
        Assert.Equal<string list>([ $"About the special|{expected}|true" ], stored)
    }

[<Fact>]
let ``spoilers are closed on load for every viewer`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"
        let! _ = postComment alice ep.Series ep.Episode "secret one" true
        let! _ = postComment bob ep.Series ep.Episode "secret two" true
        let! _ = postComment bob ep.Series ep.Episode "not a secret" false

        let! _, anonymous = episodePage app "en" ep.Series ep.Episode
        let! asAlice = alice.Get(pageOf ep)
        let! asBob = bob.Get(pageOf ep)

        for html in [ anonymous; asAlice.Html; asBob.Html ] do
            let tags = spoilerTags html
            Assert.Equal(2, tags.Length)
            Assert.All(tags, fun tag -> Assert.DoesNotMatch("\\bopen\\b", tag))
            Assert.Equal(2, Regex.Matches(html, "data-spoiler-summary").Count)
    }

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

[<Fact>]
let ``empty, whitespace-only, too long and malformed ids give 422 with the error, the typed text and no row`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"

        let page () = alice.Get(pageOf ep)

        let freshId () =
            task {
                let! p = page ()
                return (formCommentId p.Html).Value
            }

        let refused (key: string) (typed: string) (fields: (string * string) list) =
            task {
                let! response = postFields alice ep fields
                let! html = readHtml response
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode)
                Assert.Equal(key, hookValue "comment-error" html)
                Assert.Contains(Texts.en.CommentError key, html)
                Assert.Contains($">{typed}</textarea>", html)
                Assert.True(hasHook "comment-form" html)
                // the page is still the episode's, with a new id in the form
                Assert.Equal(ep.Episode, hookValue "episode-page" html)
                Assert.NotEqual<string>("", (formCommentId html).Value)
                let! count = Db.count app.Db "comments"
                Assert.Equal(0L, count)
            }

        let! id = freshId ()
        do! refused "empty" "" [ "id", id; "content", "" ]
        do! refused "empty" "   \n\t  " [ "id", id; "content", "   \n\t  " ]
        let tooLong = String.replicate 2001 "😀"
        do! refused "too-long" tooLong [ "id", id; "content", tooLong ]
        // 2,000 characters and a bit more in UTF-16 units: counted in runes, so 2,001 emoji is one too many
        do! refused "too-long" (String.replicate 2001 "a") [ "id", id; "content", String.replicate 2001 "a" ]
        do! refused "bad-id" "some text" [ "id", "not valid!"; "content", "some text" ]
        do! refused "bad-id" "some text" [ "id", ""; "content", "some text" ]
        do! refused "bad-id" "some text" [ "id", String.replicate 65 "a"; "content", "some text" ]
        do! refused "bad-id" "some text" [ "content", "some text" ]

    }

[<Fact>]
let ``a refusal keeps the spoiler choice, and exactly 2,000 runes is accepted`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! page = alice.Get(pageOf ep)
        let id = (formCommentId page.Html).Value
        let toggle (html: string) = Regex.Match(html, "<input\\b[^>]*data-spoiler-toggle[^>]*>").Value

        // a ticked box stays ticked, an unticked one stays unticked, after a refusal
        let! ticked = postFields alice ep [ "id", id; "content", ""; "spoiler", "on" ]
        let! tickedHtml = readHtml ticked
        Assert.Contains("checked", toggle tickedHtml)
        let! unticked = postFields alice ep [ "id", id; "content", "" ]
        let! untickedHtml = readHtml unticked
        Assert.DoesNotContain("checked", toggle untickedHtml)

        // exactly 2,000 runes (emoji included) is accepted
        let exact = String.replicate 2000 "😀"
        let! ok = postFields alice ep [ "id", id; "content", exact ]
        Assert.Equal(HttpStatusCode.Found, ok.StatusCode)
        let! length = Db.scalar<int> app.Db "SELECT char_length(content) FROM comments" []
        Assert.Equal(2000, length)
    }

[<Fact>]
let ``content is normalized: line breaks folded, control characters removed, edges trimmed`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! page = alice.Get(pageOf ep)
        let id = (formCommentId page.Html).Value
        let! ok = postFields alice ep [ "id", id; "content", "  one\r\ntwo\rthree\u0007\u001b\tfour  " ]
        Assert.Equal(HttpStatusCode.Found, ok.StatusCode)
        let! stored = Db.scalar<string> app.Db "SELECT content FROM comments" []
        Assert.Equal("one\ntwo\nthree\tfour", stored)
    }

// ---------------------------------------------------------------------------
// Double submit and ids
// ---------------------------------------------------------------------------

[<Fact>]
let ``the same form posted twice stores one comment and redirects to it; another user's id stores nothing`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        let! other = pickEpisode app.Db $"e.episode_number IS NOT NULL AND e.id <> '{ep.Episode}'"
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"

        let! page = alice.Get(pageOf ep)
        let id = (formCommentId page.Html).Value

        let! first = postFields alice ep [ "id", id; "content", "posted once" ]
        let! again = postFields alice ep [ "id", id; "content", "posted once" ]
        Assert.Equal(HttpStatusCode.Found, first.StatusCode)
        Assert.Equal(HttpStatusCode.Found, again.StatusCode)
        Assert.Equal($"{pageOf ep}#comment-{id}", locationOf first)
        Assert.Equal(locationOf first, locationOf again)
        let! before = rawCommentRows app.Db
        Assert.Equal(1, before.Length)

        // someone else posting alice's id: nothing stored, nothing changed, a new id in the re-rendered form
        let! forged = postFields bob ep [ "id", id; "content", "bob was here" ]
        let! html = readHtml forged
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode)
        Assert.Equal("bad-id", hookValue "comment-error" html)
        Assert.NotEqual<string>(id, (formCommentId html).Value)

        // alice reusing her own id on another episode is the same refusal
        let! elsewhere = postFields alice other [ "id", id; "content", "elsewhere" ]
        Assert.Equal(HttpStatusCode.UnprocessableEntity, elsewhere.StatusCode)

        let! after = rawCommentRows app.Db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// Plain text
// ---------------------------------------------------------------------------

[<Fact>]
let ``markup stays text and line breaks are kept`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let text = "<script>alert(1)</script> <a href=\"https://example.com\">link</a> <b>bold</b>\nsecond line\r\nthird line"
        let! response = postComment alice ep.Series ep.Episode text false
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        let! raw = rawEpisodePage app ep
        let comment = (comments raw) |> List.exactlyOne
        // nothing the author typed became an element: the only link inside is the author's
        Assert.DoesNotContain("<script>alert", comment.Html)
        Assert.Equal(1, Regex.Matches(comment.Html, "<a\\b").Count)
        Assert.DoesNotContain("<b>", comment.Html)
        Assert.Contains("&lt;script&gt;", comment.Html)
        // the text is all there, and its line breaks too, shown as written
        Assert.Contains("<script>alert(1)</script> <a href=\"https://example.com\">link</a> <b>bold</b>", comment.Text)
        Assert.Contains("second line\nthird line", WebUtility.HtmlDecode comment.Html)
        Assert.Contains("whitespace-pre-line", comment.Html)
    }

// ---------------------------------------------------------------------------
// Signed out
// ---------------------------------------------------------------------------

[<Fact>]
let ``signed out: a sign-in prompt instead of the form, and a POST goes to sign-in and stores nothing`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.False(hasHook "comment-form" html)
        Assert.True(hasHook "signin-prompt" html)
        Assert.Equal(Some $"/signin?returnUrl={Uri.EscapeDataString(pageOf ep)}", hrefOfHook "signin-prompt" html)
        Assert.Contains(Texts.en.CommentSignInPrompt, html)

        use visitor = app.NewBrowser()

        let! response =
            visitor.PostFormWithoutToken($"{pageOf ep}/comments", [ "id", "abcdefghijklmnopqrstuv"; "content", "sneaky" ])

        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.Equal($"/signin?returnUrl={Uri.EscapeDataString(pageOf ep)}", locationOf response)
        let! count = Db.count app.Db "comments"
        Assert.Equal(0L, count)

        // signed in, the prompt is gone and the form is there
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! page = alice.Get(pageOf ep)
        Assert.True(hasHook "comment-form" page.Html)
        Assert.False(hasHook "signin-prompt" page.Html)
    }

[<Fact>]
let ``a post without the antiforgery token is refused and stores nothing`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! response = alice.PostFormWithoutToken($"{pageOf ep}/comments", [ "id", "abcdefghijklmnopqrstuv"; "content", "forged" ])
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode)
        let! count = Db.count app.Db "comments"
        Assert.Equal(0L, count)
    }

// ---------------------------------------------------------------------------
// Rate limit
// ---------------------------------------------------------------------------

[<Fact>]
let ``the 11th comment write in a minute gets 429`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"

        for i in 1..10 do
            let! response = postComment alice ep.Series ep.Episode $"comment {i}" false
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"POST {i} got {int response.StatusCode}")

        let! limited = postComment alice ep.Series ep.Episode "one too many" false
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
        let! count = Db.count app.Db "comments"
        Assert.Equal(10L, count)

        // reading is not limited
        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.Equal("10", hookValue "thread" html)
    }

// ---------------------------------------------------------------------------
// Rebuild and account deletion (FR-023)
// ---------------------------------------------------------------------------

[<Fact>]
let ``user-rebuild and re-sending the sample leave the comments byte-identical`` () =
    task {
        let! app, key = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "kept" true
        let! _ = postComment alice ep.Series ep.Episode "kept too" false
        let! before = rawCommentRows app.Db
        Assert.Equal(2, before.Length)

        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! resent = rawCommentRows app.Db
        Assert.Equal<string list>(before, resent)

        let! queued = RebuildUser.rebuildUser app.Db "animegolden"
        Assert.True(queued.IsSome)
        let! _ = processAll app.Db
        let! rebuilt = rawCommentRows app.Db
        Assert.Equal<string list>(before, rebuilt)

        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.Equal("2", hookValue "thread" html)
    }

[<Fact>]
let ``deleting an account removes its comments and keeps everyone else's`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUpIn app "en" "sub-a" "alice"
        use! bob = signedUpIn app "en" "sub-b" "bob"
        let! _ = postComment alice ep.Series ep.Episode "from alice" false
        let! _ = postComment bob ep.Series ep.Episode "from bob" false
        let! _ = postComment alice ep.Series ep.Episode "alice again" true

        let! response = alice.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "alice" ])
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        let! _, html = episodePage app "en" ep.Series ep.Episode
        let thread = comments html
        Assert.Equal<string list>([ "bob" ], thread |> List.map _.Author)
        Assert.Equal("1", hookValue "thread" html)
        let! stored = rows app
        Assert.Equal<string list>([ "from bob|-|false" ], stored)
    }

// ---------------------------------------------------------------------------
// 1,000 comments (SC-005)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a thread of 1,000 comments renders all of them in under a second`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! _alice = signedUpIn app "en" "sub-a" "alice"

        do!
            Db.execute
                app.Db
                $"""INSERT INTO comments (id, cr_episode_id, author_user_id, content, created_at)
                    SELECT 'seeded' || g, '{ep.Episode}', (SELECT id FROM users WHERE username = 'alice'),
                           'Seeded comment number ' || g || E'\nwith a second line', now() - interval '1 hour' + g * interval '1 second'
                    FROM generate_series(1, 1000) g"""

        // warm-up (JIT, connection pool), then the measured request
        let! _ = episodePage app "en" ep.Series ep.Episode
        let watch = Stopwatch.StartNew()
        let! status, html = episodePage app "en" ep.Series ep.Episode
        watch.Stop()

        Assert.Equal(HttpStatusCode.OK, status)
        Assert.Equal("1000", hookValue "thread" html)
        Assert.Equal(1000, Regex.Matches(html, "data-comment=\"").Count)
        Assert.True(watch.ElapsedMilliseconds < 1000L, $"1,000 comments took {watch.ElapsedMilliseconds} ms")
    }
