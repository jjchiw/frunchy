/// Feature 017 (T008): a private profile. (Feature 021 made `members` the default, so these tests ask for `private`
/// explicitly.) Every route under /u/{username} answers the unknown-user 404
/// to anyone but the owner, and a hidden profile can't be told apart from a username that doesn't exist.
module FrunchyBackend.Tests.PrivateProfileTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private defaultKey = Profiles.DefaultVisibilityKey

type private World =
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

/// "animenerd" with the 2022 history, a second user, the root operator ("boss") and a signed-out browser
let private world (extraConfig: (string * string) list) : Task<World> =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db (("Frunchy:RootOperator", "boss") :: extraConfig) ignore
        let! owner = signedUp app "sub-owner" "animenerd"
        let! key = connectExtension owner
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db
        let! other = signedUp app "sub-other" "other"
        let! root = signedUp app "sub-boss" "boss"
        let! seriesId = Db.scalar<string> app.Db "SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY e.series_id LIMIT 1" []

        return
            { App = app
              Owner = owner
              Other = other
              Root = root
              Stranger = app.NewBrowser()
              SeriesId = seriesId }
    }

/// Every page of a profile that needs no sign-in, for one username
let private openPaths (username: string) (seriesId: string) =
    [ $"/u/{username}"
      $"/u/{username}/dna"
      $"/u/{username}/year"
      $"/u/{username}/year/2022"
      $"/u/{username}/anime/{seriesId}" ]

/// The same plus compare, which needs a signed-in viewer
let private allPaths (username: string) (seriesId: string) =
    openPaths username seriesId @ [ $"/u/{username}/compare" ]

/// The response body without the per-request antiforgery token
let private body (browser: Browser) (path: string) : Task<HttpStatusCode * string> =
    task {
        use! response = browser.GetRaw path
        let! text = response.Content.ReadAsStringAsync()
        let stripped = Regex.Replace(text, "(__RequestVerificationToken\"[^>]*value=\")[^\"]*(\")", "$1$2")
        return response.StatusCode, stripped
    }

// ---------------------------------------------------------------------------
// Strangers get the unknown-user 404, byte for byte
// ---------------------------------------------------------------------------

[<Fact>]
let ``a private profile is a 404 on every route for strangers, other users and the root`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        // signed out: every page that needs no sign-in (compare sends a signed-out visitor to sign in, as before)
        for path in openPaths "animenerd" w.SeriesId do
            let! status, _ = body w.Stranger path
            Assert.True((status = HttpStatusCode.NotFound), $"signed out {path}: {status}")

        // signed in as someone else, and as the root operator: operators get no exception
        for browser, who in [ w.Other, "other"; w.Root, "root" ] do
            for path in allPaths "animenerd" w.SeriesId do
                let! status, _ = body browser path
                Assert.True((status = HttpStatusCode.NotFound), $"{who} {path}: {status}")
    }

[<Fact>]
let ``a hidden profile cannot be told apart from an unknown username`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        // Same route shape, a username that doesn't exist; only the per-request antiforgery token is removed
        let routes =
            [ "/u/{0}"; "/u/{0}/dna"; "/u/{0}/year"; "/u/{0}/year/2022"; "/u/{0}/anime/" + w.SeriesId; "/u/{0}/compare" ]

        for browser in [ w.Other; w.Root ] do
            for route in routes do
                let! hiddenStatus, hidden = body browser (route.Replace("{0}", "animenerd"))
                let! unknownStatus, unknown = body browser (route.Replace("{0}", "nobodyhere"))
                Assert.Equal(HttpStatusCode.NotFound, unknownStatus)
                Assert.Equal(unknownStatus, hiddenStatus)
                Assert.Equal(unknown, hidden)

        // signed out too (compare redirects there, so it is left out)
        for route in routes |> List.filter (fun r -> not (r.EndsWith "/compare")) do
            let! hiddenStatus, hidden = body w.Stranger (route.Replace("{0}", "animenerd"))
            let! unknownStatus, unknown = body w.Stranger (route.Replace("{0}", "nobodyhere"))
            Assert.Equal(unknownStatus, hiddenStatus)
            Assert.Equal(unknown, hidden)

        // and it is the unknown-user page, not the plain not-found one
        let! _, page = body w.Stranger "/u/animenerd"
        let! _, plain = body w.Stranger "/nothing-here"
        Assert.False((plain = page))
    }

// ---------------------------------------------------------------------------
// The owner
// ---------------------------------------------------------------------------

[<Fact>]
let ``the owner sees every page of their private profile with the private note`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        // compare redirects an owner to their own dashboard
        for path in allPaths "animenerd" w.SeriesId do
            let! page = w.Owner.Get path
            Assert.True((page.Status = HttpStatusCode.OK), $"{path}: {page.Status}")
            Assert.Contains("data-profile-private", page.Html)
            Assert.Contains("/account#visibility", page.Html)
            // the address is no use to anyone else, so there is nothing to copy
            Assert.DoesNotContain("data-copy-link", page.Html)
    }

[<Fact>]
let ``the owner's private dashboard carries no public-profile line`` () =
    task {
        use! w = world [ defaultKey, "private" ]
        let! page = w.Owner.Get "/u/animenerd"
        Assert.DoesNotContain("anyone with the link can see these numbers", page.Html)
    }

// ---------------------------------------------------------------------------
// The public default and the real default
// ---------------------------------------------------------------------------

[<Fact>]
let ``with a public default everything opens, with the copy link and no private note`` () =
    task {
        use! w = world [ defaultKey, "public" ]

        for path in openPaths "animenerd" w.SeriesId do
            let! page = w.Stranger.Get path
            Assert.True((page.Status = HttpStatusCode.OK), $"{path}: {page.Status}")
            Assert.DoesNotContain("data-profile-private", page.Html)
            Assert.Contains("data-copy-link", page.Html)

        let! compare = w.Other.Get "/u/animenerd/compare"
        Assert.Equal(HttpStatusCode.OK, compare.Status)

        // the owner of a public profile sees no private note either
        let! own = w.Owner.Get "/u/animenerd"
        Assert.DoesNotContain("data-profile-private", own.Html)
    }

[<Fact>]
let ``the app's own default, with the setting blank, is members and no longer private`` () =
    task {
        // the fixture sets "public"; a blank value is what an instance without the setting reads (feature 021)
        use! w = world [ defaultKey, "" ]
        use! response = w.Stranger.GetRaw "/u/animenerd"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        let! own = w.Owner.Get "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, own.Status)
        Assert.DoesNotContain("data-profile-private", own.Html)
        Assert.Contains("data-profile-members", own.Html)
    }

[<Fact>]
let ``an invalid setting stops startup, naming the key`` () =
    task {
        let! db = newDatabase ()

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                task {
                    use! _ = createAppWith db [ defaultKey, "friends" ] ignore
                    ()
                }
                :> Task)

        Assert.Contains(defaultKey, ex.Message)
    }

[<Fact>]
let ``a user's own choice holds against the instance default`` () =
    task {
        // private default, but animenerd chose public
        use! w = world [ defaultKey, "private" ]

        do!
            Db.execute
                w.App.Db
                "INSERT INTO profile_settings (user_id, visibility) SELECT id, 'public' FROM users WHERE username = 'animenerd'"

        let! status, _ = body w.Stranger "/u/animenerd"
        Assert.Equal(HttpStatusCode.OK, status)

        // and the other way round: public default, but animenerd chose private
        use! v = world [ defaultKey, "public" ]

        do!
            Db.execute
                v.App.Db
                "INSERT INTO profile_settings (user_id, visibility) SELECT id, 'private' FROM users WHERE username = 'animenerd'"

        let! hidden, _ = body v.Stranger "/u/animenerd"
        Assert.Equal(HttpStatusCode.NotFound, hidden)
    }

// ---------------------------------------------------------------------------
// The home note and user-list
// ---------------------------------------------------------------------------

[<Fact>]
let ``the home note follows the instance default`` () =
    task {
        let! db = newDatabase ()

        use! privateApp = createAppWith db [ defaultKey, "private" ] ignore
        let! response = getWithLanguage privateApp.Client "en" "/"
        let! html = readHtml response
        Assert.Contains("Profiles are private", html)
        Assert.DoesNotContain("Profiles are public", html)

        let! es = getWithLanguage privateApp.Client "es" "/"
        let! esHtml = readHtml es
        Assert.Contains("Los perfiles son privados", esHtml)

        let! db3 = newDatabase ()
        use! membersApp = createAppWith db3 [ defaultKey, "members" ] ignore
        let! response = getWithLanguage membersApp.Client "en" "/"
        let! html = readHtml response
        Assert.Contains("Profiles are for members", html)
        Assert.DoesNotContain("Profiles are private", html)

        let! db2 = newDatabase ()
        use! publicApp = createAppWith db2 [ defaultKey, "public" ] ignore
        let! response = getWithLanguage publicApp.Client "en" "/"
        let! html = readHtml response
        Assert.Contains("Profiles are public unless their owner makes them private", html)
        Assert.DoesNotContain("Profiles are private", html)
    }

[<Fact>]
let ``user-list shows each user's effective visibility, starred when it comes from the default`` () =
    task {
        use! w = world [ defaultKey, "private" ]

        do!
            Db.execute
                w.App.Db
                "INSERT INTO profile_settings (user_id, visibility) SELECT id, 'public' FROM users WHERE username = 'other'"

        let settings = Profiles.ProfileSettings Profiles.Private
        let! rows = ListUsers.listUsers w.App.Db settings
        let byName name = rows |> List.find (fun r -> r.Username = name)

        Assert.Equal(Profiles.Private, (byName "animenerd").Visibility)
        Assert.True((byName "animenerd").FromDefault)
        Assert.Equal(Profiles.Public, (byName "other").Visibility)
        Assert.False((byName "other").FromDefault)
    }
