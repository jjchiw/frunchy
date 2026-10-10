/// Who can reach the MyAnimeList review pages (feature 007, US3): operators, and only operators.
/// Everyone else gets the plain 404 page on every route and verb, before any form is read.
module FrunchyBackend.Tests.MalReviewAccessTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Slime's "OVA Season 1" (series GYZJ43JMR): the 2026 sample's season nobody could match
let slimeSeason = "GR8D0NQ7R"
let slimeSeries = "GYZJ43JMR"

/// Two TV entries with the series' own title: the first is held after enrichment, the second is fetched when chosen
let slimeA = spec 37430 "Tensei shitara Slime Datta Ken" (Some "That Time I Got Reincarnated as a Slime") "TV" "2018-10-02"
let slimeB = spec 59000 "Slime Remake" (Some "That Time I Got Reincarnated as a Slime") "TV" "2024-01-01"

type ReviewEnv =
    { App: TestApp
      /// An operator (username "boss") when the configuration names it
      Boss: Browser
      /// Signed in, not an operator
      Pleb: Browser
      /// Not signed in
      Visitor: Browser }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

/// An app whose operators are configured as given, with a boss, a pleb and a visitor. With `enrich`, the 2026 sample is
/// imported and matched, and Slime's OVA season is set ambiguous with two candidates (the matcher itself leaves it without any).
let newReviewEnv (operators: (string * string) list) (enrich: bool) : Task<ReviewEnv> =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db operators ignore

        if enrich then
            let! key = createUser app.Db "animenerd"
            let! _ = submitAccepted app key (load2026Sample ())
            let! _ = processAll app.Db
            routeSampleCatalog app.Mal
            routeSearch app.Mal "That Time I Got Reincarnated as a Slime" [ slimeA; slimeB ]
            routeFull app.Mal slimeA
            routeFull app.Mal slimeB
            let! _ = enrichAll app
            // the matcher leaves this season without candidates; the catalog has two Slime entries it can't tell apart
            let! _ =
                Db.scalar<int>
                    app.Db
                    """UPDATE cr_season_matches SET status = 'ambiguous', reason = 'several-first-seasons',
                              candidates = '[{"malId":37430,"title":"That Time I Got Reincarnated as a Slime","type":"TV","year":"2018"},
                                             {"malId":59000,"title":"That Time I Got Reincarnated as a Slime (2024)","type":"TV","year":"2024"}]'::jsonb
                       WHERE season_id = @s RETURNING 1"""
                    [ "s", box slimeSeason ]

            app.Mal.Clear()

        let! boss = signedUp app "sub-boss" "boss"
        let! pleb = signedUp app "sub-pleb" "pleb"
        return { App = app; Boss = boss; Pleb = pleb; Visitor = app.NewBrowser() }
    }

let operatorsOf (names: string list) = names |> List.mapi (fun i n -> $"Frunchy:Operators:{i}", n)

/// Everything the review actions can change
let reviewSnapshot (app: TestApp) =
    task {
        let! matches = Db.dump app.Db "SELECT m::text FROM cr_season_matches m ORDER BY season_id"
        let! events = Db.dump app.Db "SELECT e::text FROM security_events e ORDER BY id"
        let! anime = Db.dump app.Db "SELECT mal_id::text FROM mal_anime ORDER BY mal_id"
        return matches, events, anime
    }

/// POST with a valid antiforgery token taken from a page this browser can load (the account page)
let postWithToken (browser: Browser) (path: string) (fields: (string * string) list) =
    browser.PostForm("/account", path, fields)

let postActions (season: string) =
    [ "/review/mal/set", [ "season", season; "mal", "15" ]
      "/review/mal/none", [ "season", season ]
      "/review/mal/retry", [ "season", season ]
      "/review/mal/auto", [ "season", season ] ]

let getRoutes = [ "/review/mal"; "/review/mal?status=failed"; "/review/mal?status=ambiguous&msg=set"; "/review/mal/manual" ]

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! body = response.Content.ReadAsStringAsync()
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", WebUtility.HtmlDecode body)
        Assert.DoesNotContain("data-review", body)
        Assert.Null response.Headers.Location
    }

/// Every route and verb answers 404 for this browser and changes nothing
let private assertClosedFor (env: ReviewEnv) (who: string) (browser: Browser) =
    task {
        let! before = reviewSnapshot env.App

        for route in getRoutes do
            let! response = browser.GetRaw route
            do! assertNotFound $"{who} GET {route}" response

        for path, fields in postActions slimeSeason do
            let! withToken = postWithToken browser path fields
            do! assertNotFound $"{who} POST {path} with a token" withToken
            let! withoutToken = browser.PostFormWithoutToken(path, fields)
            do! assertNotFound $"{who} POST {path} without a token" withoutToken

        let! after = reviewSnapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``access matrix: visitors and non-operators get 404 everywhere, the operator gets the pages`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) true
        // a manual decision, so that "auto" has something to act on
        let! _ = MalActions.setNone env.App.Db TimeProvider.System MalActions.Any "GR19CPDWM"

        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "non-operator" env.Pleb

        for route in getRoutes do
            let! page = env.Boss.Get route
            Assert.Equal(HttpStatusCode.OK, page.Status)
            Assert.Contains("data-review", page.Html)

        // an unknown season is refused with a notice, which still counts as reaching the action
        for path, _ in postActions slimeSeason do
            let! response = postWithToken env.Boss path [ "season", "NOPE0000"; "mal", "15" ]
            Assert.Equal(HttpStatusCode.Found, response.StatusCode)
            Assert.StartsWith("/review/mal", string response.Headers.Location)
            Assert.Contains("msg=unknown-season", string response.Headers.Location)

        // the forgery check still guards the operator's own POSTs
        let! forged = env.Boss.PostFormWithoutToken("/review/mal/none", [ "season", slimeSeason ])
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode)
    }

[<Fact>]
let ``with no operators configured nobody gets in, not even a signed-in boss`` () =
    task {
        use! env = newReviewEnv [] true
        do! assertClosedFor env "boss" env.Boss
        do! assertClosedFor env "pleb" env.Pleb
        do! assertClosedFor env "visitor" env.Visitor
    }

[<Fact>]
let ``operators can be an array, a comma-separated value, in any letter case`` () =
    task {
        for name, config in
            [ "array", [ "Frunchy:Operators:0", "someone"; "Frunchy:Operators:1", "boss" ]
              "comma", [ "Frunchy:Operators", "someone, boss" ]
              "comma without spaces", [ "Frunchy:Operators", "boss,someone" ]
              "upper case", [ "Frunchy:Operators", "BOSS" ]
              "single array entry", [ "Frunchy:Operators:0", "Boss" ] ] do
            use! env = newReviewEnv config false
            let! boss = env.Boss.GetRaw "/review/mal"
            Assert.True(boss.StatusCode = HttpStatusCode.OK, $"{name}: the boss got {int boss.StatusCode}")
            let! pleb = env.Pleb.GetRaw "/review/mal"
            Assert.True(pleb.StatusCode = HttpStatusCode.NotFound, $"{name}: the pleb got {int pleb.StatusCode}")

        for name, config in
            [ "empty", [ "Frunchy:Operators", "" ]
              "blank entries", [ "Frunchy:Operators", " , ," ]
              "someone else", [ "Frunchy:Operators", "bossy" ] ] do
            use! env = newReviewEnv config false
            let! boss = env.Boss.GetRaw "/review/mal"
            Assert.True(boss.StatusCode = HttpStatusCode.NotFound, $"{name}: the boss got {int boss.StatusCode}")
    }

[<Fact>]
let ``the configuration is read per request: removing an operator closes the pages`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) false
        let! open' = env.Boss.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.OK, open'.StatusCode)

        let config = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(env.App.Services)
        config["Frunchy:Operators:0"] <- "someone-else"
        let! closed = env.Boss.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode)
    }

[<Fact>]
let ``review writes are limited to 30 a minute per address`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) false

        for i in 1..30 do
            let! response = postWithToken env.Boss "/review/mal/none" [ "season", "NOPE0000" ]
            Assert.True(response.StatusCode = HttpStatusCode.Found, $"POST {i} got {int response.StatusCode}")

        let! limited = postWithToken env.Boss "/review/mal/none" [ "season", "NOPE0000" ]
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
        // reading is not limited
        let! page = env.Boss.GetRaw "/review/mal"
        Assert.Equal(HttpStatusCode.OK, page.StatusCode)
    }

[<Fact>]
let ``outsiders are never rate-limited on review actions, so a 429 can't reveal the routes`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) false

        for who, browser in [ "pleb", env.Pleb; "visitor", env.Visitor ] do
            for i in 1..35 do
                let! response = browser.PostFormWithoutToken("/review/mal/none", [ "season", "NOPE0000" ])
                Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")
    }

[<Fact>]
let ``the account page links to the review only for operators`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) false
        let! boss = env.Boss.Get "/account"
        Assert.Contains("data-review-link", boss.Html)
        Assert.Contains("href=\"/review/mal\"", boss.Html)
        let! pleb = env.Pleb.Get "/account"
        Assert.DoesNotContain("data-review-link", pleb.Html)
        Assert.DoesNotContain("/review/mal", pleb.Html)

        use! none = newReviewEnv [] false
        let! bossWithoutConfig = none.Boss.Get "/account"
        Assert.DoesNotContain("data-review-link", bossWithoutConfig.Html)
    }

[<Fact>]
let ``each successful action adds its event to the operator's security activity`` () =
    task {
        use! env = newReviewEnv (operatorsOf [ "boss" ]) true
        let post path fields = postWithToken env.Boss path fields
        let ok (response: HttpResponseMessage) = Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        // set: Slime's candidate (held, so no request), none and retry on other open seasons, then auto
        let! set = post "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ]
        ok set
        Assert.Contains("msg=set", string set.Headers.Location)
        let! none = post "/review/mal/none" [ "season", "GR19CPDWM" ]
        Assert.Contains("msg=none", string none.Headers.Location)
        let! retry = post "/review/mal/retry" [ "season", "GRDQCG2KK" ]
        Assert.Contains("msg=retry", string retry.Headers.Location)
        let! auto = post "/review/mal/auto" [ "season", slimeSeason ]
        Assert.Contains("msg=auto", string auto.Headers.Location)

        let! events = Db.dump env.App.Db "SELECT event || ' ' || actor FROM security_events WHERE event LIKE 'mal_%' ORDER BY id"
        Assert.Equal<string list>([ "mal_set user"; "mal_none user"; "mal_retry user"; "mal_auto user" ], events)

        let! account = env.Boss.Get "/account"
        Assert.Contains("MyAnimeList match set by hand", account.Html)
        Assert.Contains("Season marked as not on MyAnimeList", account.Html)
        Assert.Contains("MyAnimeList match retried", account.Html)
        Assert.Contains("MyAnimeList match sent back to automatic", account.Html)

        // refusals are not security events
        let! stale = post "/review/mal/none" [ "season", "GR19CPDWM" ]
        Assert.Contains("msg=not-open", string stale.Headers.Location)
        let! count = Db.scalar<int64> env.App.Db "SELECT count(*) FROM security_events WHERE event LIKE 'mal_%'" []
        Assert.Equal(4L, count)
    }
