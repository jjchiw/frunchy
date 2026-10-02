module FrunchyBackend.Tests.CsrfTests

open System.Net
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Everything a forged POST could change
let private snapshot (app: TestApp) =
    Db.dump app.Db
        "SELECT (SELECT count(*) FROM users)::text || '|' || (SELECT count(*) FROM user_identities) || '|' || (SELECT string_agg(display_name || coalesce(sync_key_hash,'-') || coalesce(cr_account_id,'-'), ',' ORDER BY username) FROM users) || '|' || (SELECT count(*) FROM security_events)"

[<Fact>]
let ``signed-in state-changing posts without an antiforgery token are rejected and change nothing`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! _ = browser.Get "/account/link/test2?sub=second" // two identities, so unlink would be allowed

        for action, fields in
            [ "/account/display-name", [ "displayName", "Forged" ]
              "/account/key", []
              "/account/binding/reset", []
              "/account/unlink/test2", []
              "/account/delete", [ "confirmUsername", "animenerd" ]
              "/signout", [] ] do
            let! before = snapshot app
            let! response = browser.PostFormWithoutToken(action, fields)
            let! after = snapshot app
            Assert.True(response.StatusCode = HttpStatusCode.BadRequest, $"{action} → {response.StatusCode}")
            Assert.Equal<string list>(before, after)

        // still signed in (the forged sign-out didn't happen)
        let! account = browser.GetRaw "/account"
        Assert.Equal(HttpStatusCode.OK, account.StatusCode)
    }

[<Fact>]
let ``sign-up and claim posts without a token are rejected`` () =
    task {
        use! app = newApp ()
        let! invite = createInvite app
        let! key = createUser app.Db "jjchiw"
        use browser = app.NewBrowser()
        let! _ = signInWith browser "newcomer"
        let! signup = browser.PostFormWithoutToken("/signup", [ "invite", invite; "username", "forged"; "displayName", "F" ])
        let! claim = browser.PostFormWithoutToken("/claim", [ "syncKey", key ])
        Assert.Equal(HttpStatusCode.BadRequest, signup.StatusCode)
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode)
        let! identities = Db.count app.Db "user_identities"
        let! users = Db.count app.Db "users"
        Assert.Equal(0L, identities)
        Assert.Equal(1L, users)
    }

[<Fact>]
let ``the extension API needs no antiforgery token (contract v1.2 unchanged)`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! status, _ = submit app key (load2022Envelope ())
        Assert.Equal(202, status)
    }
