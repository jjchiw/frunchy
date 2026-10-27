/// Opt-in: saves the HTML of the signed-in pages for a design review (tools/snapshots/README.md). Skipped unless
/// FRUNCHY_SNAPSHOT_DIR is set, so a normal `dotnet test` run only reports it skipped.
module FrunchyBackend.Tests.PageSnapshots

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Where the shooter resolves the page's root-relative URLs: nowhere (it blocks this host), so nothing breaks the layout
let private placeholderHost = "http://snapshot.invalid"

/// Root-relative `src="/…"`, `href="/…"`, `action="/…"` and `url(/…)` point at the placeholder host; `//host/…` stays
let private standalone (html: string) =
    let attr =
        Regex.Replace(html, "\\b(src|href|action|formaction|poster)=\"(/(?!/)[^\"]*)\"", $"$1=\"{placeholderHost}$2\"")

    Regex.Replace(attr, "url\\((['\"]?)(/(?!/)[^)'\"]*)\\1\\)", $"url($1{placeholderHost}$2$1)")

/// The first `pages` pages of the 2026 sample as a submission for another Crunchyroll profile
let private samplePages (profileId: string) (pages: int) =
    let node = JsonNode.Parse(load2026SampleRaw ()) :?> JsonObject
    let all = node["pages"] :?> JsonArray
    let kept = JsonArray()

    for i in 0 .. (min pages all.Count) - 1 do
        kept.Add(all[i].DeepClone())

    let result = JsonObject()
    result["profileId"] <- JsonValue.Create profileId
    result["pages"] <- kept
    result.ToJsonString()

[<Fact>]
let ``save the HTML of the signed-in pages for a design review`` () =
    task {
        let dir = Environment.GetEnvironmentVariable "FRUNCHY_SNAPSHOT_DIR"

        Assert.SkipWhen(
            String.IsNullOrWhiteSpace dir,
            "snapshots only: FRUNCHY_SNAPSHOT_DIR=<folder> dotnet test --project tests/FrunchyBackend.Tests --filter-class \"*PageSnapshots*\""
        )

        let htmlDir = Path.Combine(dir, "html")
        Directory.CreateDirectory htmlDir |> ignore

        for old in Directory.EnumerateFiles(htmlDir, "*.html") do
            File.Delete old

        let! db = newDatabase ()

        use! app =
            createAppWith
                db
                [ "Frunchy:RootOperator", "animegolden"
                  // members only: what a person sees when they follow a link to a profile
                  "Frunchy:Profiles:DefaultVisibility", "members" ]
                ignore

        // ---- people: the root operator, a second viewer with history, a third who signed up with an invite
        let! rootBrowser = signedUp app "sub-animegolden" "animegolden"
        let! sakuraBrowser = signedUp app "sub-sakura" "sakura"
        let! thirdBrowser = signedUp app "sub-third" "kenji"
        (thirdBrowser :> IDisposable).Dispose()

        // ---- history: the whole 2026 sample, a shorter one for the second user
        let! rootKey = connectExtension rootBrowser
        let! _ = submitAccepted app rootKey (load2026Sample ())
        let! sakuraKey = connectExtension sakuraBrowser
        let! _ = submitAccepted app sakuraKey (samplePages "profile-sakura" 2)
        let! _ = processAll app.Db

        // feature 027: the sample's One Piece has 13 fully watched episodes, short of the long-run card's 24. Watch 24
        // more, one every three weeks from January 2025, so the card shows and the seasons section has recent seasons,
        // some of its shows watched while airing (within 7 days) and some later.
        let! onePiece =
            Db.scalar<string>
                app.Db
                "SELECT e.series_id || '|' || e.season_id FROM cr_episodes e WHERE e.series_title = 'One Piece' AND e.season_id IS NOT NULL ORDER BY e.id LIMIT 1"
                []

        let seriesOfOnePiece, seasonOfOnePiece = (let p = onePiece.Split '|' in p[0], p[1])

        let moreOnePiece =
            [ for i in 0..23 ->
                  let aired = DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc).AddDays(21.0 * float i)
                  let watched = aired.AddDays(if i % 2 = 0 then 2.0 else 20.0).AddHours 12.0
                  // the catalog keeps the show's real title: the entry names it like the sample does
                  (datedEntry $"SNAPOP{i:D3}" seriesOfOnePiece seasonOfOnePiece (1201 + i) (Some aired) watched true)
                      .Replace($"Series {seriesOfOnePiece}", "One Piece") ]

        let! _ = submitAccepted app rootKey (envelopeForProfile MainProfile [ historyUrl RealAccountId, pageOf moreOnePiece ])
        let! _ = processAll app.Db
        (rootBrowser :> IDisposable).Dispose()
        (sakuraBrowser :> IDisposable).Dispose()

        // genres, scores and posters from the scripted catalog (no real provider)
        routeSampleCatalog app.Mal
        let! _ = enrichAll app

        // ---- invites: the three accounts above used theirs; two more stay active
        let! _ = createInvite app
        let! _ = createInvite app

        let! seriesId =
            Db.scalar<string>
                app.Db
                """SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                   JOIN users u ON u.id = w.user_id
                   WHERE u.username = 'animegolden' AND e.duration_ms IS NOT NULL
                   GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1"""
                []

        let save (name: string) (language: string) (status: HttpStatusCode) (html: string) =
            Assert.True((status = HttpStatusCode.OK), $"{name}-{language}: {int status}")
            File.WriteAllText(Path.Combine(htmlDir, $"{name}-{language}.html"), standalone html)

        // raw body, not Browser.Get: that one decodes entities, which a saved page must keep
        let saveResponse (name: string) (language: string) (response: HttpResponseMessage) =
            task {
                let! html = response.Content.ReadAsStringAsync()
                save name language response.StatusCode html
            }

        let getInto (browser: Browser) (name: string) (language: string) (path: string) =
            task {
                use! response = browser.GetRaw path
                do! saveResponse name language response
            }

        for language in [ "en"; "es" ] do
            // signed out
            use visitor = app.NewBrowser language
            do! getInto visitor "home" language "/"
            do! getInto visitor "signin" language "/signin"
            do! getInto visitor "guide" language "/guide"

            // back from a provider with no account yet: the sign-up form (the guide's sign-up step)
            use newcomer = app.NewBrowser language
            let! _ = signInWith newcomer $"sub-newcomer-{language}"
            do! getInto newcomer "signup" language "/signup"

            // signed in as the root operator
            use root = app.NewBrowser language
            let! _ = signInWith root "sub-animegolden"

            do! getInto root "dashboard" language "/u/animegolden"
            do! getInto root "dna" language "/u/animegolden/dna"
            do! getInto root "year-2026" language "/u/animegolden/year/2026"
            do! getInto root "anime" language $"/u/animegolden/anime/{seriesId}"
            do! getInto root "compare" language "/u/sakura/compare"
            do! getInto root "account" language "/account"
            do! getInto root "review-invites" language "/review/invites"

            // the pages that show a new code, and a new share link, once (the POST's own response)
            use! created = root.PostForm("/review/invites", "/review/invites/create", [ "days", "7" ])
            do! saveResponse "invite-created" language created

            do! getInto root "review-settings" language "/review/settings"

            use! link = root.PostForm("/account", "/account/share-link", [])
            do! saveResponse "share-link-created" language link
            do! getInto root "account-shared" language "/account"

            // the sync key, shown once (a throwaway key of this test database; the guide's connect step)
            use! key = root.PostForm("/account/key", "/account/key", [])
            do! saveResponse "sync-key" language key

        printfn "page snapshots: %s" htmlDir
    }
