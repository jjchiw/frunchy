/// Feature 015 (US1, T014): the dashboard and the series page carry the fediverse handle with a copy button, and
/// point Mastodon to their actor (`<link rel="alternate">` and the `Link` header). Nothing of this with federation off.
/// The episode page's thread link has its tests with the outbox (`FediverseOutboxTests`, T009).
module FrunchyBackend.Tests.FediversePagesTests

open System
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Views

/// The 2026 sample imported by "animenerd" and enriched; `federated` picks the app
let private sample (federated: bool) : Task<TestApp> =
    task {
        let! app = if federated then newFederatedApp () else newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app
        return app
    }

let private alternateLinks (html: string) =
    [ for m in Regex.Matches(html, "<link\\b[^>]*\\brel=\"alternate\"[^>]*>") -> m.Value ]

let private linkHeader (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Link" with
    | true, values -> Some(String.Join(",", values))
    | _ -> None

let private page (app: TestApp) (language: string) (path: string) =
    task {
        let! response = getWithLanguage app.Client language path
        let! html = readHtml response
        return response, html
    }

let private activityPub (url: string) = $"<{url}>; rel=\"alternate\"; type=\"application/activity+json\""

/// The text of the handle line (`data-fediverse-handle`), tags stripped
let private handleLine (html: string) =
    // the line is a div holding the label, the handle and the copy widget (a p couldn't hold the widget's div)
    let m = Regex.Match(html, "<div\\b[^>]*data-fediverse-handle=\"([^\"]*)\"[^>]*>(.*?)<div\\b[^>]*data-copy-handle", RegexOptions.Singleline)
    if m.Success then Some(m.Groups[1].Value, Regex.Replace(m.Groups[2].Value, "<[^>]*>", " ")) else None

// ---------------------------------------------------------------------------
// The dashboard
// ---------------------------------------------------------------------------

[<Fact>]
let ``the dashboard shows the user's handle with a copy button and points to their actor`` () =
    task {
        use! app = sample true
        let! id = Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = 'animenerd'" []
        let! response, html = page app "en" "/u/animenerd"

        // the line: label, the handle in the page's text, and a copy button holding the handle
        match handleLine html with
        | Some(hook, text) ->
            Assert.Equal("@animenerd@frunchy.test", hook)
            Assert.Contains(Texts.en.FediverseFollowLabel, text)
            Assert.Contains("@animenerd@frunchy.test", text)
        | None -> failwith "no handle line"

        Assert.Contains("data-copy-handle", html)
        Assert.Contains("data-address=\"@animenerd@frunchy.test\"", html)
        Assert.Contains(Texts.en.CopyHandle, html)
        // the page's own copy-link control is still there, separate from the handle's
        Assert.Contains("data-copy-link", html)

        // the actor, in the head and in the header
        let url = $"https://frunchy.test/ap/actors/{id}"
        let links = alternateLinks html
        Assert.Equal(1, links.Length)
        Assert.Contains("type=\"application/activity+json\"", links[0])
        Assert.Contains($"href=\"{url}\"", links[0])
        Assert.True(html.IndexOf links[0] < html.IndexOf "</head>")
        Assert.Equal(Some(activityPub url), linkHeader response)
    }

[<Fact>]
let ``the handle line reads in Spanish`` () =
    task {
        use! app = sample true
        let! _, html = page app "es" "/u/animenerd"
        Assert.Contains(Texts.es.FediverseFollowLabel, html)
        Assert.Contains(Texts.es.CopyHandle, html)
    }

[<Fact>]
let ``a user whose username can't federate shows no handle and no actor link`` () =
    task {
        use! app = sample true
        do! Db.execute app.Db "UPDATE users SET username = 'oldname-' WHERE username = 'animenerd'"
        let! response, html = page app "en" "/u/oldname-"
        Assert.Equal(Net.HttpStatusCode.OK, response.StatusCode)
        Assert.DoesNotContain("data-fediverse-handle", html)
        Assert.Empty(alternateLinks html)
        Assert.Equal(None, linkHeader response)
    }

// ---------------------------------------------------------------------------
// The series page
// ---------------------------------------------------------------------------

[<Fact>]
let ``the series page shows the series' handle, made on first view and stable, and points to its actor`` () =
    task {
        use! app = sample true
        let! ep = pickEpisode app.Db "TRUE"
        let! none = Db.count app.Db "series_handles"
        Assert.Equal(0L, none)

        let! response, html = page app "en" $"/anime/{ep.Series}"
        let! handle = Db.scalar<string> app.Db "SELECT handle FROM series_handles WHERE series_id = @s" [ "s", box ep.Series ]
        let full = $"@{handle}@frunchy.test"

        match handleLine html with
        | Some(hook, text) ->
            Assert.Equal(full, hook)
            Assert.Contains(Texts.en.FediverseFollowLabel, text)
            Assert.Contains(full, text)
        | None -> failwith "no handle line"

        Assert.Contains("data-copy-handle", html)
        Assert.Contains($"data-address=\"{full}\"", html)

        let url = $"https://frunchy.test/ap/actors/series-{ep.Series}"
        let links = alternateLinks html
        Assert.Equal(1, links.Length)
        Assert.Contains($"href=\"{url}\"", links[0])
        Assert.Equal(Some(activityPub url), linkHeader response)

        // the same handle every time, and only one stored
        let! _, again = page app "en" $"/anime/{ep.Series}"
        Assert.Equal(Some full, handleLine again |> Option.map fst)
        let! stored = Db.count app.Db "series_handles"
        Assert.Equal(1L, stored)
    }

[<Fact>]
let ``a series page that doesn't exist makes no handle`` () =
    task {
        use! app = sample true
        let! response, _ = page app "en" "/anime/ZZZZZZZZZ"
        Assert.Equal(Net.HttpStatusCode.NotFound, response.StatusCode)
        Assert.Equal(None, linkHeader response)
        let! stored = Db.count app.Db "series_handles"
        Assert.Equal(0L, stored)
    }

// ---------------------------------------------------------------------------
// Federation off
// ---------------------------------------------------------------------------

[<Fact>]
let ``with federation off no page shows a handle, an actor link or a Link header, and no handle is made`` () =
    task {
        use! app = sample false
        let! ep = pickEpisode app.Db "TRUE"

        for path in [ "/u/animenerd"; $"/anime/{ep.Series}"; episodeUrl ep.Series ep.Episode ] do
            let! response, html = page app "en" path
            Assert.Equal(Net.HttpStatusCode.OK, response.StatusCode)
            Assert.DoesNotContain("data-fediverse-handle", html)
            Assert.DoesNotContain("data-copy-handle", html)
            Assert.Empty(alternateLinks html)
            Assert.Equal(None, linkHeader response)

        let! stored = Db.count app.Db "series_handles"
        Assert.Equal(0L, stored)
    }
