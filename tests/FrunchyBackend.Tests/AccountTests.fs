module FrunchyBackend.Tests.AccountTests

open System
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private events (app: TestApp) =
    Db.dump app.Db "SELECT event, coalesce(provider,'-') FROM security_events ORDER BY id"

[<Fact>]
let ``changing the display name shows on the dashboard and is recorded`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! response = browser.PostForm("/account", "/account/display-name", [ "displayName", "Neo Nerd" ])
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        let! dashboard = browser.Get "/u/animenerd"
        Assert.Contains(">Neo Nerd<", dashboard.Html)
        let! all = events app
        Assert.Contains("display_name_changed|-", all)

        let! invalid = browser.PostForm("/account", "/account/display-name", [ "displayName", String('x', 61) ])
        let! page = browser.Follow invalid
        Assert.Contains("The display name must be 1-60 characters.", page.Html)
        let! name = Db.scalar<string> app.Db "SELECT display_name FROM users" []
        Assert.Equal("Neo Nerd", name)
    }

[<Fact>]
let ``resetting the binding keeps history and records it`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! key = connectExtension browser
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db
        let! account = browser.Get "/account"
        Assert.Contains("Bound to a Crunchyroll profile.", account.Html)

        let! response = browser.PostForm("/account", "/account/binding/reset", [])
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        let! bound = Db.scalar<bool> app.Db "SELECT cr_account_id IS NOT NULL FROM users" []
        let! history = Db.count app.Db "watch_events"
        Assert.False bound
        Assert.Equal(101L, history)
        let! all = events app
        Assert.Contains("binding_reset|-", all)
    }

[<Fact>]
let ``linking a second identity, signing in with it, and unlinking`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "first-id" "animenerd" |> fun t -> t.Result
        let! linked = browser.Get "/account/link/test2?sub=second-id"
        Assert.Equal("/account?msg=linked", linked.Path)
        let! identities = Db.dump app.Db "SELECT provider_user_id FROM user_identities ORDER BY linked_at"
        Assert.Equal<string list>([ "first-id"; "second-id" ], identities)

        use other = app.NewBrowser()
        let! viaSecond = other.Get "/signin/test2?sub=second-id"
        Assert.Equal("/account", viaSecond.Path)
        Assert.Contains(">animenerd<", viaSecond.Html)

        let! all = events app
        Assert.Contains("identity_linked|test2", all)
    }

[<Fact>]
let ``unlinking works while another identity remains, never the last one`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "only-id" "animenerd" |> fun t -> t.Result
        let! last = browser.PostForm("/account", "/account/unlink/test", [])
        let! page = browser.Follow last
        Assert.Contains("You can't remove your last sign-in method.", page.Html)
        let! still = Db.count app.Db "user_identities"
        Assert.Equal(1L, still)

        // with a second provider linked, the first can go
        let! _ = browser.Get "/account/link/test2?sub=x"
        let! ok = browser.PostForm("/account", "/account/unlink/test", [])
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode)
        let! remaining = Db.dump app.Db "SELECT provider FROM user_identities"
        Assert.Equal<string list>([ "test2" ], remaining)
        use again = app.NewBrowser()
        let! signIn = signInWith again "only-id"
        Assert.Equal("/signup", signIn.Path) // unlinked identity no longer signs in
        let! all = events app
        Assert.Contains("identity_unlinked|test", all)
    }

[<Fact>]
let ``an identity of another account can't be linked`` () =
    task {
        use! app = newApp ()
        use a = signedUp app "id-a" "usera" |> fun t -> t.Result
        use b = signedUp app "id-b" "userb" |> fun t -> t.Result
        let! page = a.Get "/account/link/test?sub=id-b"
        Assert.Equal("/account?msg=link-taken", page.Path)
        Assert.Contains("already linked to another Frunchy account", page.Html)
        let! rows = Db.dump app.Db "SELECT u.username, i.provider_user_id FROM user_identities i JOIN users u ON u.id = i.user_id ORDER BY 1"
        Assert.Equal<string list>([ "usera|id-a"; "userb|id-b" ], rows)
    }

[<Fact>]
let ``a second identity of the same provider is refused`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "id-1" "animenerd" |> fun t -> t.Result
        let! page = browser.Get "/account/link/test?sub=id-2"
        // the first link of a provider already used is refused: one identity per provider per account
        Assert.Equal("/account?msg=link-duplicate", page.Path)
        let! count = Db.count app.Db "user_identities"
        Assert.Equal(1L, count)
    }

[<Fact>]
let ``the account page lists the last 10 syncs and last 20 security events`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! key = connectExtension browser

        for i in 1..12 do
            let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf [ syntheticEntry $"E{i}" "S1" (DateTime(2025, 1, i, 0, 0, 0, DateTimeKind.Utc)) true 1400 ] ])
            ()

        let! _ = processAll app.Db

        for i in 1..25 do
            let! _ = browser.PostForm("/account", "/account/display-name", [ "displayName", $"Name {i}" ])
            ()

        let! page = browser.Get "/account"
        Assert.Equal(10, Regex.Matches(page.Html, "data-sync=").Count)
        Assert.Equal(20, Regex.Matches(page.Html, "data-security-event=").Count)
        // newest event first
        let firstEvent = Regex.Match(page.Html, "data-security-event=\"([a-z_]+)\"").Groups[1].Value
        Assert.Equal("display_name_changed", firstEvent)
        Assert.Contains("completed", page.Html.ToLowerInvariant())
    }

[<Fact>]
let ``a user deleted elsewhere is signed out on the next request`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! _ = FrunchyBackend.Operations.DeleteUser.deleteUser app.Db false "animenerd"
        let! response = browser.GetRaw "/account"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.StartsWith("/signin", string response.Headers.Location)
    }

[<Fact>]
let ``account page in Spanish`` () =
    task {
        use! app = newApp ()
        let! invite = createInvite app
        use browser = app.NewBrowser "es"
        let! _ = submitSignUp browser "sub-1" invite "animenerd" "AnimeNerd"
        let! page = browser.Get "/account"
        Assert.Contains("lang=\"es\"", page.Html)
        Assert.Contains("Tu cuenta", page.Html)
        Assert.Contains("Conectar la extensión", page.Html)
    }
