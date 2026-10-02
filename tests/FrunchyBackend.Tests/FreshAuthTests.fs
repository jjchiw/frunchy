module FrunchyBackend.Tests.FreshAuthTests

open System
open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// A clock the test can move forward
type TestClock() =
    inherit TimeProvider()
    member val Now = DateTimeOffset.UtcNow with get, set
    override this.GetUtcNow() = this.Now

let private appWithClock () =
    task {
        let clock = TestClock()
        let! db = newDatabase ()
        let! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        return app, clock
    }

[<Fact>]
let ``a stale session must re-confirm before generating a key, then continues`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        clock.Now <- clock.Now.AddMinutes 11.0

        let! blocked = browser.GetRaw "/account/key"
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode)
        let location = string blocked.Headers.Location
        Assert.Equal("/signin/reconfirm?returnUrl=%2Faccount%2Fkey", location)

        let! back = browser.Get(location + "&sub=sub-1")
        Assert.Equal("/account/key", back.Path)
        Assert.Contains("action=\"/account/key\"", back.Html)
        let! events = Db.dump app.Db "SELECT event FROM security_events WHERE event = 'reconfirmed'"
        Assert.Equal<string list>([ "reconfirmed" ], events)

        let! key = connectExtension browser
        Assert.StartsWith("frk_", key)
    }

[<Fact>]
let ``re-confirming with a different identity doesn't count`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        clock.Now <- clock.Now.AddMinutes 11.0
        let! page = browser.Get "/signin/reconfirm?returnUrl=%2Faccount%2Fkey&sub=somebody-else"
        Assert.Equal("/account?msg=reconfirm-failed", page.Path)
        let! stillBlocked = browser.GetRaw "/account/key"
        Assert.StartsWith("/signin/reconfirm", string stillBlocked.Headers.Location)
    }

[<Fact>]
let ``cancelling the re-confirmation changes nothing and keeps the session`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        clock.Now <- clock.Now.AddMinutes 11.0
        let! _ = browser.Get "/signin/reconfirm?returnUrl=%2Faccount%2Fkey&fail=1"
        let! account = browser.GetRaw "/account"
        Assert.Equal(HttpStatusCode.OK, account.StatusCode)
        let! keys = Db.scalar<bool> app.Db "SELECT sync_key_hash IS NULL FROM users" []
        Assert.True keys
    }

[<Fact>]
let ``a fresh session goes straight to the confirmation`` () =
    task {
        let! app, clock = appWithClock ()
        use app = app
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        clock.Now <- clock.Now.AddMinutes 9.0
        let! page = browser.GetRaw "/account/key"
        Assert.Equal(HttpStatusCode.OK, page.StatusCode)
    }
