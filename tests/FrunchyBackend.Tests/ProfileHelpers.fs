/// Feature 017 (phase 2): what the visibility, share-link and link tests share. A private "animegolden" with the 2022
/// history, a second user, the root operator and a signed-out browser; helpers for the account page's hooks.
module FrunchyBackend.Tests.ProfileHelpers

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let defaultKey = Profiles.DefaultVisibilityKey

type World =
    { App: TestApp
      Owner: Browser
      Other: Browser
      Root: Browser
      Stranger: Browser
      SeriesId: string }

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            (this.Owner :> IDisposable).Dispose()
            (this.Other :> IDisposable).Dispose()
            (this.Root :> IDisposable).Dispose()
            (this.Stranger :> IDisposable).Dispose()
            (this.App :> IAsyncDisposable).DisposeAsync()

/// "animegolden" with the 2022 history, a second user "other", the root operator "boss" and a signed-out browser
let world (extraConfig: (string * string) list) : Task<World> =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db (("Frunchy:RootOperator", "boss") :: extraConfig) ignore
        let! owner = signedUp app "sub-owner" "animegolden"
        let! key = connectExtension owner
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db
        let! other = signedUp app "sub-other" "other"
        let! root = signedUp app "sub-boss" "boss"

        let! seriesId =
            Db.scalar<string>
                app.Db
                "SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY e.series_id LIMIT 1"
                []

        return
            { App = app
              Owner = owner
              Other = other
              Root = root
              Stranger = app.NewBrowser()
              SeriesId = seriesId }
    }

/// Every page of a profile that needs no sign-in
let openPaths (username: string) (seriesId: string) =
    [ $"/u/{username}"
      $"/u/{username}/dna"
      $"/u/{username}/year"
      $"/u/{username}/year/2022"
      $"/u/{username}/anime/{seriesId}" ]

/// The status of a GET (no redirect following)
let statusOf (browser: Browser) (path: string) : Task<HttpStatusCode> =
    task {
        use! response = browser.GetRaw path
        return response.StatusCode
    }

/// The first `data-{hook}="…"` of the owner's account page ("" when absent)
let accountHook (browser: Browser) (hook: string) : Task<string> =
    task {
        let! page = browser.Get "/account"
        return EpisodeHelpers.hookValue hook page.Html
    }

/// Post the visibility form from the account page; the response is not followed
let setVisibility (browser: Browser) (value: string) : Task<HttpResponseMessage> =
    browser.PostForm("/account", "/account/visibility", [ "visibility", value ])

/// Create (or replace) the share link from the account page. Returns the link the show-once page prints, as a path
/// (`/s/frs_…`) and the full text.
let createShareLink (browser: Browser) : Task<string * string> =
    task {
        let! response = browser.PostForm("/account", "/account/share-link", [])
        let! html = response.Content.ReadAsStringAsync()
        let m = Regex.Match(html, "data-share-link=\"\"[^>]*>([^<]+)<")

        if not m.Success then
            failwith $"no share link shown: {response.StatusCode}"

        let link = WebUtility.HtmlDecode m.Groups[1].Value
        return Uri(link).PathAndQuery, link
    }

/// Turn the share link off from the account page
let revokeShareLink (browser: Browser) : Task<HttpResponseMessage> =
    browser.PostForm("/account", "/account/share-link/revoke", [])

/// Does the response carry any Set-Cookie header?
let setsCookie (response: HttpResponseMessage) = response.Headers.Contains "Set-Cookie"

/// A page without the footer language link's returnUrl, which is the address that was asked for: two 404s for
/// different addresses are then the same page
let withoutLanguageReturn (html: string) =
    System.Text.RegularExpressions.Regex.Replace(html, "(/language/(en|es)\\?returnUrl=)[^\"]*", "$1")
