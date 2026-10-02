module FrunchyBackend.Tests.SyncKeyTests

open System.Net
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``connect extension shows a working key exactly once`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! confirm = browser.Get "/account/key"
        Assert.Contains("action=\"/account/key\"", confirm.Html)

        let! key = connectExtension browser
        Assert.Matches("^frk_[A-Za-z0-9_-]{43}$", key)
        let! me = getWithKey app.Client (Some key) "/api/me"
        Assert.Equal(HttpStatusCode.OK, me.StatusCode)

        let! account = browser.Get "/account"
        Assert.DoesNotContain(key, account.Html)
        Assert.Contains("data-key-active=\"true\"", account.Html)
    }

[<Fact>]
let ``a new key revokes the previous one and is recorded`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! first = connectExtension browser
        let! second = connectExtension browser
        let! old = getWithKey app.Client (Some first) "/api/me"
        let! current = getWithKey app.Client (Some second) "/api/me"
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode)
        Assert.Equal(HttpStatusCode.OK, current.StatusCode)
        let! events = Db.dump app.Db "SELECT event, actor FROM security_events WHERE event = 'sync_key_generated'"
        Assert.Equal<string list>([ "sync_key_generated|user"; "sync_key_generated|user" ], events)
    }

[<Fact>]
let ``the extension contract is unchanged for keys made on the website`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! key = connectExtension browser
        let! status, _ = FrunchyBackend.Tests.TestHelpers.submit app key (load2022Envelope ())
        Assert.Equal(202, status)
    }
