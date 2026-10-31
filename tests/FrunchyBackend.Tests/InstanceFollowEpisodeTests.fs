/// Feature 028 (T009): what the episode page shows of other Frunchy instances: a line per live remote thread of the
/// page's group ("Also discussed on {domain}", a link there) and the "via {domain}" mark on the comments that came
/// through one. The enriched 2026 sample has an original season (GRK5CNJ14) and its dub, so the group rule is the real one.
module FrunchyBackend.Tests.InstanceFollowEpisodeTests

open System
open System.Net
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.InstanceFollowWebhooksTests
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Views

type World =
    { App: TestApp
      Series: string
      Orig: string
      Dub: string
      /// Our group key for the pair: `{original season}#{number}`
      GroupKey: string
      /// The root operator
      Boss: Browser }

    interface IAsyncDisposable with
        member w.DisposeAsync() = (w.App :> IAsyncDisposable).DisposeAsync()

let private world (federated: bool) : Task<World> =
    task {
        let! db = newDatabase ()

        let! app =
            if federated then
                createFederatedAppWith db [ "Frunchy:RootOperator", "boss" ] ignore
            else
                createAppWith db [ "Frunchy:RootOperator", "boss" ] ignore

        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app

        let! rows =
            Db.dump
                app.Db
                """SELECT dub.series_id, orig.id, dub.id, orig.episode_number
                   FROM cr_episodes dub
                   JOIN cr_season_matches m ON m.season_id = dub.season_id AND m.status = 'matched' AND m.method = 'dub'
                   JOIN cr_episodes orig ON orig.season_id = m.shared_from AND orig.episode_number = dub.episode_number
                   WHERE m.shared_from = 'GRK5CNJ14' AND dub.series_id = orig.series_id
                     AND dub.duration_ms IS NOT NULL AND orig.duration_ms IS NOT NULL
                   ORDER BY dub.id, orig.id LIMIT 1"""

        let p = rows.Head.Split '|'

        do!
            Db.execute
                app.Db
                $"""INSERT INTO series_follows (series_id, domain, remote_actor, source, state)
                    VALUES ('{p[0]}', '{domain}', 'https://{domain}/ap/actors/series-{p[0]}', 'series', 'accepted')"""

        let boss = app.NewBrowser()
        let! invite = createInvite app
        let! _ = submitSignUp boss "sub-boss" invite "boss" "Boss"

        return
            { App = app
              Series = p[0]
              Orig = p[1]
              Dub = p[2]
              GroupKey = $"GRK5CNJ14#{p[3]}"
              Boss = boss }
    }

let private deliver (w: World) (body: string) : Task =
    task {
        let! status = MegafonoWebhooksTests.sendRaw w.App (Some MegafonoWebhooksTests.bearer) (Some(MegafonoWebhooksTests.signedAt DateTimeOffset.UtcNow body)) body
        Assert.Equal(HttpStatusCode.NoContent, status)
    }

/// A thread of ours for the original episode, delivered
let private threadOnOriginal (w: World) (n: int) : Task<Post> =
    task {
        let t = { threadOf w.Series n with Extensions = ours w.Series w.Orig w.GroupKey }
        do! deliver w (received t)
        return t
    }

/// The `li[data-remote-thread]` lines of a page: domain and the href inside
let private threadLines (html: string) =
    [ for m in Regex.Matches(html, "<li\\b[^>]*data-remote-thread=\"([^\"]+)\"[^>]*>(.*?)</li>", RegexOptions.Singleline) ->
          let href = Regex.Match(m.Groups[2].Value, "href=\"([^\"]*)\"")
          m.Groups[1].Value, (if href.Success then WebUtility.HtmlDecode href.Groups[1].Value else "")
          , m.Groups[2].Value ]

[<Fact>]
let ``a thread of the other instance shows on the episode and on its dub, linking to the thread's page there`` () =
    task {
        let! w = world true
        use w = w
        let! t = threadOnOriginal w 1

        for episode in [ w.Orig; w.Dub ] do
            let! status, html = episodePage w.App "en" w.Series episode
            Assert.Equal(HttpStatusCode.OK, status)
            let lines = threadLines html
            Assert.Equal(1, lines.Length)
            let d, href, inner = lines.Head
            Assert.Equal("other.test", d)
            Assert.Equal(t.Url, href)
            Assert.Contains("Also discussed on other.test", WebUtility.HtmlDecode inner)
            // a link out of Frunchy carries no referrer or ranking and can't reach the opener
            Assert.Contains("rel=\"nofollow noopener noreferrer\"", inner)
    }

[<Fact>]
let ``only the live threads of the page's group show, and a deleted one goes`` () =
    task {
        let! w = world true
        use w = w
        let! t = threadOnOriginal w 1

        // another number of the same seasons is another group
        let other = { threadOf w.Series 2 with Extensions = ours w.Series "REMOTEEP2" (w.GroupKey.Split('#')[0] + "#9999") }
        do! deliver w (received other)

        // a second thread by the same instance for the same group is its own line
        let again = { threadOf w.Series 3 with Extensions = ours w.Series "REMOTEEP3" w.GroupKey }
        do! deliver w (received again)

        let! _, html = episodePage w.App "en" w.Series w.Orig
        Assert.Equal(2, (threadLines html).Length)
        Assert.Contains("data-remote-threads=\"2\"", html)

        do! deliver w (deletedJson "p-del" t.ObjectId)
        let! _, afterDelete = episodePage w.App "en" w.Series w.Orig
        Assert.Equal<string list>([ again.Url ], threadLines afterDelete |> List.map (fun (_, href, _) -> href))

        do! deliver w (deletedJson "p-del2" again.ObjectId)
        let! _, none = episodePage w.App "en" w.Series w.Orig
        Assert.DoesNotContain("data-remote-thread", none)
    }

[<Fact>]
let ``a thread stored before our catalog knew the episode shows by its group key or its remote episode id`` () =
    task {
        let! w = world true
        use w = w

        for n, byGroup in [ 1, true; 2, false ] do
            let groupKey = if byGroup then $"'{w.GroupKey}'" else "NULL"
            let remoteEpisode = if byGroup then "'ELSEWHERE1'" else $"'{w.Orig}'"

            do!
                Db.execute
                    w.App.Db
                    $"""INSERT INTO remote_threads (object_id, domain, remote_actor, series_id, remote_episode_id, remote_group_key,
                                                    cr_episode_id, url, published_at)
                        VALUES ('https://{domain}/ap/objects/old{n}', '{domain}', 'https://{domain}/ap/actors/series-{w.Series}',
                                '{w.Series}', {remoteEpisode}, {groupKey}, NULL, 'https://{domain}/anime/{w.Series}/episode/OLD{n}', now())"""

        let! _, html = episodePage w.App "en" w.Series w.Dub
        Assert.Equal(2, (threadLines html).Length)
    }

[<Fact>]
let ``the link is the thread's https url on its instance, else its object id there, else there is no link, and a script url is never one`` () =
    task {
        let! w = world true
        use w = w

        let cases =
            [ 1, "'http://other1.test/x'", "https://other1.test/ap/objects/t1"
              2, "'javascript:alert(1)'", "https://other2.test/ap/objects/t2"
              3, "NULL", "https://other3.test/ap/objects/t3"
              4, "'https://other4.test/anime/x/episode/OK4'", "https://other4.test/anime/x/episode/OK4"
              5, "'http://other5.test/x'", ""
              // a link to another host is never shown, the object id on the instance is
              6, "'https://elsewhere.test/anime/x/episode/OK6'", "https://other6.test/ap/objects/t6"
              7, "'https://elsewhere.test/x'", "" ]

        for n, url, expected in cases do
            let objectId =
                match n with
                | 5 -> $"http://other5.test/ap/objects/t{n}"
                | 7 -> $"https://elsewhere.test/ap/objects/t{n}"
                | _ -> $"https://other{n}.test/ap/objects/t{n}"

            do!
                Db.execute
                    w.App.Db
                    $"""INSERT INTO remote_threads (object_id, domain, remote_actor, series_id, remote_episode_id, remote_group_key,
                                                    cr_episode_id, url, published_at)
                        VALUES ('{objectId}', 'other{n}.test', 'https://other.test/ap/actors/series-{w.Series}', '{w.Series}',
                                'R{n}', NULL, '{w.Orig}', {url}, now())"""

        // a thread that names this episode under another series never shows here
        do!
            Db.execute
                w.App.Db
                $"""INSERT INTO remote_threads (object_id, domain, remote_actor, series_id, remote_episode_id, remote_group_key,
                                                cr_episode_id, url, published_at)
                    VALUES ('https://wrong.test/ap/objects/w', 'wrong.test', 'https://wrong.test/ap/actors/series-ZZZZZZ1', 'ZZZZZZ1',
                            '{w.Orig}', NULL, NULL, 'https://wrong.test/x', now())"""

        let! _, html = episodePage w.App "en" w.Series w.Orig
        let lines = threadLines html
        Assert.Equal(7, lines.Length)
        Assert.DoesNotContain(lines, fun (d, _, _) -> d = "wrong.test")

        for (n, _, expected) in cases do
            let _, href, _ = lines |> List.find (fun (d, _, _) -> d = $"other{n}.test")
            Assert.Equal(expected, href)

        Assert.DoesNotContain("javascript:", html)
    }

[<Fact>]
let ``a page with no remote thread has no such section, and with federation off nothing of it shows`` () =
    task {
        let! w = world true
        use w = w
        let! _, html = episodePage w.App "en" w.Series w.Orig
        Assert.DoesNotContain("data-remote-thread", html)

        let! off = world false
        use off = off

        do!
            Db.execute
                off.App.Db
                $"""INSERT INTO remote_threads (object_id, domain, remote_actor, series_id, remote_episode_id, cr_episode_id, url, published_at)
                    VALUES ('https://{domain}/ap/objects/t1', '{domain}', 'https://{domain}/ap/actors/series-{off.Series}', '{off.Series}',
                            'R1', '{off.Orig}', 'https://{domain}/x', now())"""

        let! _, offHtml = episodePage off.App "en" off.Series off.Orig
        Assert.DoesNotContain("data-remote-thread", offHtml)
    }

// ---------------------------------------------------------------------------
// Comments that came through a remote thread
// ---------------------------------------------------------------------------

/// A thread and Bob's comment under it, delivered; returns the comment's page id
let private threadWithComment (w: World) : Task<Post * Post * string> =
    task {
        let! t = threadOnOriginal w 1
        let c = comment 1 t.ObjectId
        do! deliver w (received c)
        return t, c, Fediverse.remoteCommentId c.ObjectId
    }

[<Fact>]
let ``a comment that came through another instance's thread says so, on the original's page and the dub's`` () =
    task {
        let! w = world true
        use w = w
        let! _, _, id = threadWithComment w

        for episode in [ w.Orig; w.Dub ] do
            let! _, html = episodePage w.App "en" w.Series episode
            let shown = comments html |> List.find (fun c -> c.Id = id)
            Assert.Equal("other.test", hookValue "comment-via" shown.Html)
            Assert.Contains("via other.test", shown.Text)
            Assert.Contains("that ending!", shown.Text)
            // it stays on the episode the thread was mapped to
            Assert.Equal(w.Orig, shown.Episode)
            Assert.Contains("data-remote", shown.Html)

        let! _, es = episodePage w.App "es" w.Series w.Orig
        Assert.Contains("vía other.test", es)
        Assert.Contains(Texts.es.RemoteThreadAlso "other.test", es)
    }

[<Fact>]
let ``a feature 015 reply has no via mark`` () =
    task {
        let! w = world true
        use w = w
        let! _ = threadWithComment w
        let! id = Db.scalar<string> w.App.Db "SELECT id FROM comments LIMIT 1" []
        do! Db.execute w.App.Db $"UPDATE comments SET remote_thread = NULL WHERE id = '{id}'"
        let! _, html = episodePage w.App "en" w.Series w.Orig
        Assert.DoesNotContain("data-comment-via", html)
    }

[<Fact>]
let ``an operator can hide such a comment, and it is a placeholder for everyone else`` () =
    task {
        let! w = world true
        use w = w
        let! _, _, id = threadWithComment w
        let page = episodeUrl w.Series w.Orig

        let! hidden = w.Boss.PostForm(page, $"/comments/{id}/hide", [])
        Assert.Equal(HttpStatusCode.Redirect, hidden.StatusCode)

        // a visitor sees the placeholder, not the text, but the thread line stays
        let! _, visitor = episodePage w.App "en" w.Series w.Orig
        let shown = comments visitor |> List.find (fun c -> c.Id = id)
        Assert.True shown.Hidden
        Assert.DoesNotContain("that ending!", shown.Text)
        Assert.Contains("data-remote-thread", visitor)

        // the operator still reads it, marked as hidden
        let! operatorPage = w.Boss.Get page
        let seen = comments operatorPage.Html |> List.find (fun c -> c.Id = id)
        Assert.Contains("that ending!", seen.Text)
        Assert.Equal("other.test", hookValue "comment-via" seen.Html)

        let! unhidden = w.Boss.PostForm(page, $"/comments/{id}/unhide", [])
        Assert.Equal(HttpStatusCode.Redirect, unhidden.StatusCode)
        let! _, again = episodePage w.App "en" w.Series w.Orig
        Assert.Contains("that ending!", (comments again |> List.find (fun c -> c.Id = id)).Text)
    }

[<Fact>]
let ``a remote comment's spoiler warning keeps the text behind the toggle`` () =
    task {
        let! w = world true
        use w = w
        let! t = threadOnOriginal w 1
        do! deliver w (received { comment 1 t.ObjectId with Summary = "Spoilers: ending"; Sensitive = true })
        let! _, html = episodePage w.App "en" w.Series w.Orig
        let shown = comments html |> List.exactlyOne
        Assert.True shown.Spoiler
        Assert.Equal("other.test", hookValue "comment-via" shown.Html)
    }
