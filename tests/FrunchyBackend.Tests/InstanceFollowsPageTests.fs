/// Feature 028 (T008): `/review/follows`, where an operator follows the same series on other Frunchy instances.
/// NodeInfo comes from `FakeNodeInfo` and Megáfono from `FakeMegafono`; the 2026 sample gives the series watched
/// here. Outsiders get the plain 404 on every route and verb; every refusal writes nothing.
module FrunchyBackend.Tests.InstanceFollowsPageTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.FakeNodeInfo
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Views

type Env =
    { App: TestApp
      /// Signed in, not an operator
      Alice: Browser
      /// The root operator (username "boss")
      Boss: Browser
      Visitor: Browser
      /// The series watched here (the 2026 sample), ordinal order
      Series: string list }

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

let private newEnvWith (federated: bool) : Task<Env> =
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
        let! alice = signedUp app "sub-alice" "alice"
        let boss = app.NewBrowser()
        let! invite = createInvite app
        let! _ = submitSignUp boss "sub-boss" invite "boss" "Boss"

        let! series =
            Db.dump
                app.Db
                "SELECT DISTINCT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL"

        return
            { App = app
              Alice = alice
              Boss = boss
              Visitor = app.NewBrowser()
              Series = series |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) }
    }

let private newEnv () = newEnvWith true

let private locationOf (response: HttpResponseMessage) = string response.Headers.Location

let private add (browser: Browser) (target: string) =
    browser.PostForm("/review/follows", "/review/follows/add", [ "target", target ])

let private removeFollow (browser: Browser) (fields: (string * string) list) =
    browser.PostForm("/review/follows", "/review/follows/remove", fields)

let private ordinal (items: string list) = List.sortWith (fun (a: string) b -> String.CompareOrdinal(a, b)) items

let private follows (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT series_id, domain, source, state FROM series_follows"
        return ordinal rows
    }

let private outbox (app: TestApp) =
    task {
        let! rows = Db.dump app.Db "SELECT kind, subject FROM fediverse_outbox WHERE kind IN ('follow_series', 'unfollow_series')"
        return ordinal rows
    }

/// "username event actor detail" of the follow events
let private events (app: TestApp) =
    Db.dump
        app.Db
        "SELECT u.username || ' ' || e.event || ' ' || e.actor || ' ' || coalesce(e.provider, '-') FROM security_events e JOIN users u ON u.id = e.user_id WHERE e.event LIKE '%follow_%' ORDER BY e.id"

/// Nothing written anywhere: no row, no outbox item, no event
let private assertNothingWritten (env: Env) =
    task {
        let! f = follows env.App
        let! i = Db.count env.App.Db "instance_follows"
        let! o = outbox env.App
        let! e = events env.App
        Assert.Empty f
        Assert.Equal(0L, i)
        Assert.Empty o
        Assert.Empty e
    }

let private hasHook (hook: string) (html: string) = html.Contains $"data-{hook}"

let private noticeOf (html: string) =
    let m = Regex.Match(html, "data-follow-notice=\"([^\"]+)\"")
    if m.Success then m.Groups[1].Value else ""

/// The `data-series-follow` values of a page
let private seriesFollowsOn (html: string) =
    [ for m in Regex.Matches(html, "data-series-follow=\"([^\"]+)\"") -> m.Groups[1].Value ]

// ---------------------------------------------------------------------------
// Who can use it
// ---------------------------------------------------------------------------

let private assertNotFound (what: string) (response: HttpResponseMessage) =
    task {
        let! body = response.Content.ReadAsStringAsync()
        Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{what}: expected 404, got {int response.StatusCode}")
        Assert.Contains("Page not found", WebUtility.HtmlDecode body)
        Assert.DoesNotContain("data-follows-review", body)
        Assert.Null response.Headers.Location
    }

let private assertClosedFor (env: Env) (who: string) (browser: Browser) =
    task {
        let! page = browser.GetRaw "/review/follows"
        do! assertNotFound $"{who} GET" page

        for action in [ "/review/follows/add"; "/review/follows/remove" ] do
            let fields = [ "target", "other.test"; "domain", "other.test" ]
            let! withToken = browser.PostForm("/account", action, fields)
            do! assertNotFound $"{who} POST {action} with a token" withToken
            let! withoutToken = browser.PostFormWithoutToken(action, fields)
            do! assertNotFound $"{who} POST {action} without a token" withoutToken

        // a 429 would reveal the routes
        for i in 1..35 do
            let! response = browser.PostFormWithoutToken("/review/follows/add", [ "target", "other.test" ])
            Assert.True(response.StatusCode = HttpStatusCode.NotFound, $"{who} POST {i} got {int response.StatusCode}")

        do! assertNothingWritten env
    }

[<Fact>]
let ``visitors and users who aren't operators get the plain 404 on every route and verb, never a 429`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        do! assertClosedFor env "visitor" env.Visitor
        do! assertClosedFor env "user" env.Alice

        // nothing was looked up, and a non-operator's account page has no link
        Assert.Empty env.App.NodeInfo.Requests
        Assert.Empty(env.App.Megafono.RequestsTo "GET /internal/remote-actors")
        let! page = env.Alice.Get "/account"
        Assert.DoesNotContain("data-follows-link", page.Html)
    }

[<Fact>]
let ``with federation off even the root operator gets the plain 404`` () =
    task {
        use! env = newEnvWith false
        do! assertClosedFor env "root with federation off" env.Boss
        let! page = env.Boss.Get "/account"
        Assert.DoesNotContain("data-follows-link", page.Html)
    }

[<Fact>]
let ``the root and an operator from the table can use it, and the account page links to it`` () =
    task {
        use! env = newEnv ()
        let! added = OperatorActions.add env.App.Db (Some "boss") "alice" OperatorActions.Server
        Assert.True(Result.isOk added)

        for browser in [ env.Boss; env.Alice ] do
            let! account = browser.Get "/account"
            Assert.Contains("data-follows-link", account.Html)
            let! page = browser.Get "/review/follows"
            Assert.Equal(HttpStatusCode.OK, page.Status)
            Assert.Contains("data-follows-review", page.Html)
            Assert.Contains("data-follow-add", page.Html)
            Assert.Equal(2, Regex.Matches(page.Html, "data-follows-empty").Count)
    }

// ---------------------------------------------------------------------------
// Following a whole instance
// ---------------------------------------------------------------------------

[<Fact>]
let ``following an instance makes a series follow for every series watched here, queued, with one event`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)

        let! response = add env.Boss "Other.Test"
        Assert.Equal("/review/follows?msg=follow-added", locationOf response)

        // the NodeInfo check asked the typed host, and Megáfono was not asked about an account
        Assert.Equal<string list>([ "https://other.test/.well-known/nodeinfo"; "https://other.test/nodeinfo/2.0" ], env.App.NodeInfo.Requests)
        Assert.Empty(env.App.Megafono.RequestsTo "GET /internal/remote-actors")

        let! rows = follows env.App
        Assert.Equal<string list>(env.Series |> List.map (fun s -> $"{s}|other.test|instance|queued") |> ordinal, rows)
        let! queued = outbox env.App
        Assert.Equal<string list>(env.Series |> List.map (fun s -> $"follow_series|{s}@other.test") |> ordinal, queued)
        let! recorded = events env.App
        Assert.Equal<string list>([ "boss instance_follow_added user other.test" ], recorded)

        // the page lists them
        let! page = env.Boss.Follow response
        Assert.Equal("follow-added", noticeOf page.Html)
        Assert.Contains("data-instance-follow=\"other.test\"", page.Html)
        Assert.Contains($"data-series-count=\"{env.Series.Length}\"", page.Html)
        Assert.Equal<string list>(env.Series |> List.map (fun s -> $"{s}@other.test") |> ordinal, seriesFollowsOn page.Html |> ordinal)
        Assert.Contains("data-follow-state=\"queued\"", page.Html)
        Assert.Contains("data-follow-source=\"instance\"", page.Html)

        // again: nothing new, no second event
        let! again = add env.Boss "other.test"
        Assert.Equal("/review/follows?msg=follow-already", locationOf again)
        let! recordedAgain = events env.App
        Assert.Equal<string list>(recorded, recordedAgain)
    }

[<Fact>]
let ``a server that isn't Frunchy, is down or is this one is refused and nothing is written`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("masto.test", Software "mastodon")
        env.App.NodeInfo.Set("down.test", Down)
        env.App.NodeInfo.Set("big.test", Oversize)

        let! mastodon = add env.Boss "masto.test"
        Assert.Equal("/review/follows?msg=follow-not-frunchy", locationOf mastodon)
        let! down = add env.Boss "down.test"
        Assert.Equal("/review/follows?msg=follow-unreachable", locationOf down)
        let! big = add env.Boss "big.test"
        Assert.Equal("/review/follows?msg=follow-unreachable", locationOf big)
        let! unknown = add env.Boss "nowhere.test"
        Assert.Equal("/review/follows?msg=follow-unreachable", locationOf unknown)

        // our own domain is refused before anything is asked of it
        env.App.NodeInfo.Clear()
        let! self = add env.Boss FakeMegafono.Domain
        Assert.Equal("/review/follows?msg=follow-self", locationOf self)
        let! selfCase = add env.Boss (FakeMegafono.Domain.ToUpperInvariant())
        Assert.Equal("/review/follows?msg=follow-self", locationOf selfCase)
        Assert.Empty env.App.NodeInfo.Requests

        do! assertNothingWritten env

        // and the page says it in words
        let! page = env.Boss.Follow mastodon
        Assert.Equal("follow-not-frunchy", noticeOf page.Html)
        Assert.Contains(Texts.en.FollowNotice "follow-not-frunchy", WebUtility.HtmlDecode page.Html)
    }

[<Fact>]
let ``what isn't a server name, a handle or a series account address is invalid and asks nobody`` () =
    task {
        use! env = newEnv ()

        for typed in [ ""; "   "; "not a host"; "ftp://other.test/x"; "https://other.test"; "http://other.test/ap/actors/series-GG5H5XQX4"; "@@"; "a@b@c" ] do
            let! response = add env.Boss typed
            Assert.Equal("/review/follows?msg=follow-invalid", locationOf response)

        Assert.Empty env.App.NodeInfo.Requests
        Assert.Empty env.App.Megafono.Requests
        do! assertNothingWritten env
    }

// ---------------------------------------------------------------------------
// Following one series
// ---------------------------------------------------------------------------

[<Fact>]
let ``a handle follows one series, found by id, with its own event`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let series = env.Series.Head

        let! response = add env.Boss $"@{series.ToLowerInvariant()}@other.test"
        Assert.Equal("/review/follows?msg=follow-added", locationOf response)

        let lookups = env.App.Megafono.RequestsTo "GET /internal/remote-actors"
        Assert.Equal(1, lookups.Length)
        Assert.Contains("handle=", lookups.Head.Query)

        let! rows = follows env.App
        Assert.Equal<string list>([ $"{series}|other.test|series|queued" ], rows)
        let! queued = outbox env.App
        Assert.Equal<string list>([ $"follow_series|{series}@other.test" ], queued)
        let! recorded = events env.App
        Assert.Equal<string list>([ $"boss series_follow_added user {series}@other.test" ], recorded)

        // the actor's web address works too, and a repeat changes nothing
        let! again = add env.Boss $"https://other.test/ap/actors/series-{series}"
        Assert.Equal("/review/follows?msg=follow-already", locationOf again)
        let! recordedAgain = events env.App
        Assert.Equal(1, recordedAgain.Length)

        let! page = env.Boss.Follow response
        Assert.Equal<string list>([ $"{series}@other.test" ], seriesFollowsOn page.Html)
        Assert.Contains("data-follow-source=\"series\"", page.Html)
    }

[<Fact>]
let ``an account that isn't a Frunchy series, or that Megafono can't resolve, is refused with its own notice`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let series = env.Series.Head
        let ok = $"https://other.test/ap/actors/series-{series}"

        let actorBody (id: string) (kind: string) =
            FakeMegafono.Body(200, $$"""{"id":"{{id}}","handle":"@x@other.test","name":"X","iconUrl":null,"profileUrl":null,"type":"{{kind}}"}""")

        let cases =
            [ "a person", [ actorBody ok "Person" ], "follow-not-series"
              "a user actor", [ actorBody "https://other.test/ap/actors/some-user-id" "Group" ], "follow-not-series"
              "a series actor with a path", [ actorBody (ok + "/extra") "Group" ], "follow-not-series"
              "a series actor with a port", [ actorBody $"https://other.test:8443/ap/actors/series-{series}" "Group" ], "follow-not-series"
              "an actor of Mastodon", [ actorBody "https://mastodon.social/users/bob" "Person" ], "follow-not-series"
              "not found", [ FakeMegafono.Status 404 ], "follow-not-found"
              "blocked", [ FakeMegafono.Body(409, """{"error":"blocked","message":"blocked"}""") ], "follow-blocked"
              "a refused address", [ FakeMegafono.Status 400 ], "follow-invalid"
              "unreachable", [ FakeMegafono.Status 502 ], "follow-unreachable"
              "a server error", [ FakeMegafono.Status 503 ], "follow-unreachable"
              "an unreadable answer", [ FakeMegafono.Body(200, "nope") ], "follow-unreachable" ]

        for what, replies, expected in cases do
            env.App.Megafono.Route("GET /internal/remote-actors", replies)
            let! response = add env.Boss "@someone@other.test"
            Assert.True((locationOf response = $"/review/follows?msg={expected}"), $"{what}: {locationOf response}")

        do! assertNothingWritten env

        // the series account of our own domain
        env.App.Megafono.Route("GET /internal/remote-actors", [ actorBody $"https://{FakeMegafono.Domain}/ap/actors/series-{series}" "Group" ])
        let! self = add env.Boss "@someone@frunchy.test"
        Assert.Equal("/review/follows?msg=follow-self", locationOf self)

        // a series account on a server that isn't Frunchy
        env.App.NodeInfo.Set("masto.test", Software "gotosocial")
        env.App.Megafono.Route("GET /internal/remote-actors", [ actorBody $"https://masto.test/ap/actors/series-{series}" "Group" ])
        let! notFrunchy = add env.Boss "@someone@masto.test"
        Assert.Equal("/review/follows?msg=follow-not-frunchy", locationOf notFrunchy)

        do! assertNothingWritten env
    }

[<Fact>]
let ``a series our catalog doesn't have is refused`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let! response = add env.Boss "@zzzzzz9@other.test"
        Assert.Equal("/review/follows?msg=follow-unknown-series", locationOf response)
        do! assertNothingWritten env
    }

// ---------------------------------------------------------------------------
// Explicit follows and instance follows together (FR-005)
// ---------------------------------------------------------------------------

[<Fact>]
let ``an explicit follow of a covered series keeps it when the instance follow is removed`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let kept = env.Series.Head

        let! _ = add env.Boss "other.test"
        let! promoted = add env.Boss $"@{kept.ToLowerInvariant()}@other.test"
        Assert.Equal("/review/follows?msg=follow-added", locationOf promoted)

        let! removed = removeFollow env.Boss [ "domain", "other.test" ]
        Assert.Equal("/review/follows?msg=follow-removed", locationOf removed)

        let! rows = follows env.App
        Assert.Equal<string list>([ $"{kept}|other.test|series|queued" ], rows)
        let! instances = Db.count env.App.Db "instance_follows"
        Assert.Equal(0L, instances)

        // the others got an unfollow each
        let! queued = outbox env.App
        let unfollows = queued |> List.filter (fun s -> s.StartsWith "unfollow_series|")
        Assert.Equal<string list>(env.Series |> List.filter ((<>) kept) |> List.map (fun s -> $"unfollow_series|{s}@other.test") |> ordinal, unfollows)

        let! recorded = events env.App
        Assert.Contains("boss instance_follow_removed user other.test", recorded)

        let! page = env.Boss.Follow removed
        Assert.Equal<string list>([ $"{kept}@other.test" ], seriesFollowsOn page.Html)
        // no instance is followed as a whole any more
        Assert.Contains("data-follows-empty=\"instances\"", page.Html)
    }

[<Fact>]
let ``removing one series follow removes it, whatever made it, and removing what isn't followed says so`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let! _ = add env.Boss "other.test"
        let series = env.Series.Head

        let! removed = removeFollow env.Boss [ "domain", "other.test"; "series", series ]
        Assert.Equal("/review/follows?msg=follow-removed", locationOf removed)
        // the instance follow stays, so the row stays as a marker the sweep leaves alone
        let! rows = follows env.App
        Assert.Equal(env.Series.Length, rows.Length)
        Assert.Contains($"{series}|other.test|series|unfollowed", rows)
        let! page = env.Boss.Follow removed
        Assert.Contains($"data-follow-state=\"unfollowed\"", page.Html)
        let! queued = outbox env.App
        Assert.Contains($"unfollow_series|{series}@other.test", queued)
        let! recorded = events env.App
        Assert.Contains($"boss series_follow_removed user {series}@other.test", recorded)

        let! again = removeFollow env.Boss [ "domain", "other.test"; "series", series ]
        Assert.Equal("/review/follows?msg=follow-not-followed", locationOf again)
        let! nothing = removeFollow env.Boss [ "domain", "nothing.test" ]
        Assert.Equal("/review/follows?msg=follow-not-followed", locationOf nothing)

        for fields in [ [ "domain", "" ]; [ "domain", "not a host" ]; [ "domain", "other.test"; "series", "lower" ]; [ "series", series ] ] do
            let! invalid = removeFollow env.Boss fields
            Assert.Equal("/review/follows?msg=follow-invalid", locationOf invalid)
    }

// ---------------------------------------------------------------------------
// States and words
// ---------------------------------------------------------------------------

[<Fact>]
let ``a follow.accepted webhook shows as following on the page, with the remote handle`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let series = env.Series.Head
        let! _ = add env.Boss $"@{series.ToLowerInvariant()}@other.test"

        let body =
            $$"""{"id":"evt-f1","version":1,"type":"follow.accepted","occurredAt":"2026-10-12T18:00:00Z","seq":1,
                "user":{"id":"series-{{series}}","handle":"x"},
                "actor":{"id":"https://other.test/ap/actors/series-{{series}}","handle":"@frieren@other.test","name":"Frieren","iconUrl":null,"profileUrl":null},
                "follow":{"id":"follow-1","direction":"outbound","status":"accepted"},"extensions":null}"""

        let! status =
            MegafonoWebhooksTests.sendRaw
                env.App
                (Some MegafonoWebhooksTests.bearer)
                (Some(MegafonoWebhooksTests.signedAt DateTimeOffset.UtcNow body))
                body

        Assert.Equal(HttpStatusCode.NoContent, status)

        let! page = env.Boss.Get "/review/follows"
        Assert.Contains("data-follow-state=\"accepted\"", page.Html)
        Assert.Contains("@frieren@other.test", page.Html)
        Assert.Contains(Texts.en.FollowState "accepted", WebUtility.HtmlDecode page.Html)
    }

[<Fact>]
let ``the last answer of a failed follow shows its code and nothing else`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("other.test", Frunchy)
        let series = env.Series.Head
        let! _ = add env.Boss $"@{series.ToLowerInvariant()}@other.test"
        do! Db.execute env.App.Db "UPDATE series_follows SET state = 'failed', last_error = '409 blocked'"

        let! page = env.Boss.Get "/review/follows"
        Assert.Contains("data-follow-state=\"failed\"", page.Html)
        Assert.Contains("data-follow-error", page.Html)
        Assert.Contains("409 blocked", page.Html)
    }

[<Fact>]
let ``the page and its notices read in Spanish`` () =
    task {
        use! env = newEnv ()
        env.App.NodeInfo.Set("masto.test", Software "mastodon")
        let es = env.App.NewBrowser "es"
        let! _ = signInWith es "sub-boss"

        let! response = es.PostForm("/review/follows", "/review/follows/add", [ "target", "masto.test" ])
        let! page = es.Follow response
        let html = WebUtility.HtmlDecode page.Html
        Assert.Contains(Texts.es.FollowsTitle, html)
        Assert.Contains(Texts.es.FollowNotice "follow-not-frunchy", html)
        Assert.Contains(Texts.es.FollowsAddButton, html)
        Assert.Contains(Texts.es.FollowsInstancesEmpty, html)

        let! account = es.Get "/account"
        Assert.Contains(Texts.es.FollowsAccountLink, WebUtility.HtmlDecode account.Html)
    }
