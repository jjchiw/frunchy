module FrunchyBackend.Tests.SignInTests

open System
open System.Net
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``sign-in page lists the registered providers`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = browser.Get "/signin"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-provider=\"test\"", page.Html)
        Assert.Contains("Sign in with Test", page.Html)
        Assert.DoesNotContain("data-provider=\"google\"", page.Html) // disabled in appsettings
    }

[<Fact>]
let ``sign-in page in Spanish`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser "es"
        let! page = browser.Get "/signin"
        Assert.Contains("lang=\"es\"", page.Html)
        Assert.Contains("Iniciar sesión con Test", page.Html)
    }

[<Fact>]
let ``an unknown identity goes to sign-up and nothing is created`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = signInWith browser "stranger"
        Assert.Equal("/signup", page.Path)
        Assert.Contains("Create your account", page.Html)
        let! users = Db.count app.Db "users"
        let! identities = Db.count app.Db "user_identities"
        Assert.Equal(0L, users)
        Assert.Equal(0L, identities)
    }

[<Fact>]
let ``a linked identity signs in and returns to a local returnUrl only`` () =
    task {
        use! app = newApp ()
        use first = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! _ = createUser app.Db "other" // unrelated
        use browser = app.NewBrowser()
        let! page = browser.Get "/signin/test?sub=sub-1&returnUrl=%2Fu%2Fanimenerd"
        Assert.Equal("/u/animenerd", page.Path)

        use evil = app.NewBrowser()
        let! landing = evil.Get "/signin/test?sub=sub-1&returnUrl=%2F%2Fevil.example%2Fx"
        Assert.Equal("/account", landing.Path)
    }

[<Fact>]
let ``account requires sign-in and comes back afterwards`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! response = browser.GetRaw "/account"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/signin?returnUrl=%2Faccount", string response.Headers.Location)
    }

[<Fact>]
let ``session cookie is HttpOnly, SameSite=Lax and lasts about 30 days`` () =
    task {
        use! app = newApp ()
        let! invite = createInvite app
        use browser = app.NewBrowser()
        let! response = submitSignUp browser "sub-1" invite "animenerd" "AnimeNerd"
        let cookie = response.Headers.GetValues "Set-Cookie" |> Seq.find (fun c -> c.StartsWith "frunchy.session=")
        Assert.Contains("httponly", cookie.ToLowerInvariant())
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant())
        let session = browser.Cookies.GetAllCookies() |> Seq.find (fun c -> c.Name = "frunchy.session")
        Assert.InRange(session.Expires.ToUniversalTime(), DateTime.UtcNow.AddDays 29.0, DateTime.UtcNow.AddDays 31.0)
    }

[<Fact>]
let ``signing in records last use and a signed_in event`` () =
    task {
        use! app = newApp ()
        use _ = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        do! Db.execute app.Db "UPDATE user_identities SET last_used_at = now() - interval '1 day'"
        use browser = app.NewBrowser()
        let! _ = signInWith browser "sub-1"
        let! recent = Db.scalar<bool> app.Db "SELECT last_used_at > now() - interval '1 minute' FROM user_identities" []
        let! events = Db.dump app.Db "SELECT event, provider FROM security_events ORDER BY id"
        Assert.True recent
        Assert.Equal<string list>([ "account_created|test"; "signed_in|test" ], events)
    }

[<Fact>]
let ``signing out ends only that browser's session`` () =
    task {
        use! app = newApp ()
        use a = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        use b = app.NewBrowser()
        let! _ = signInWith b "sub-1"

        let! signOut = a.PostForm("/account", "/signout", [])
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode)
        let! aAccount = a.GetRaw "/account"
        let! bAccount = b.GetRaw "/account"
        Assert.Equal(HttpStatusCode.Redirect, aAccount.StatusCode)
        Assert.Equal(HttpStatusCode.OK, bAccount.StatusCode)
    }

[<Fact>]
let ``a cancelled provider sign-in shows the failure message`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = browser.Get "/signin/test?fail=1"
        Assert.Equal("/signin?error=failed", page.Path)
        Assert.Contains("data-signin-error", page.Html)
    }

[<Fact>]
let ``signed-in navbar shows the display name, account and sign out`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! page = browser.Get "/"
        Assert.Contains("data-nav=\"user\">ANIMENERD<", page.Html)
        // the name leads to the user's public dashboard
        Assert.Matches("<a href=\"/u/animenerd\"[^>]*data-nav=\"user\">", page.Html)
        Assert.Contains("href=\"/account\"", page.Html)
        Assert.Contains("action=\"/signout\"", page.Html)
    }
