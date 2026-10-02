module FrunchyBackend.Tests.DeleteAccountTests

open System
open System.Net
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private userRows (app: TestApp) (username: string) =
    task {
        let count (sql: string) = Db.scalar<int64> app.Db sql [ "u", box username ]
        let! users = count "SELECT count(*) FROM users WHERE username = @u"
        let! identities = count "SELECT count(*) FROM user_identities i JOIN users u ON u.id = i.user_id WHERE u.username = @u"
        let! imports = count "SELECT count(*) FROM cr_imports i JOIN users u ON u.id = i.user_id WHERE u.username = @u"
        let! events = count "SELECT count(*) FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = @u"
        return users + identities + imports + events
    }

[<Fact>]
let ``a wrong username is refused and nothing is deleted`` () =
    task {
        use! app = newApp ()
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! response = browser.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "someoneelse" ])
        let! html = response.Content.ReadAsStringAsync()
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        Assert.Contains("doesn't match", WebUtility.HtmlDecode html)
        let! users = Db.count app.Db "users"
        Assert.Equal(1L, users)
    }

[<Fact>]
let ``deleting removes everything, signs out and frees the identity and username`` () =
    task {
        use! app = newApp ()
        use other = signedUp app "other-sub" "userb" |> fun t -> t.Result
        let! otherKey = connectExtension other
        let! _ = submitAccepted app otherKey (envelopeOf (syntheticPages "11111111-2222-3333-4444-555555555555" 1 10))
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        let! key = connectExtension browser
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! otherBefore = getWithLanguage app.Client "en" "/u/userb"
        let! otherHtmlBefore = readHtml otherBefore

        let! response = browser.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "AnimeNerd" ])
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/?deleted=1", string response.Headers.Location)
        let! home = browser.Follow response
        Assert.Contains("data-account-deleted", home.Html)

        let! remaining = userRows app "animenerd"
        Assert.Equal(0L, remaining)
        let! orphanPages = Db.scalar<int64> app.Db "SELECT count(*) FROM cr_import_pages p LEFT JOIN cr_imports i ON i.id = p.import_id WHERE i.id IS NULL" []
        Assert.Equal(0L, orphanPages)
        let! invite = Db.dump app.Db "SELECT (used_at IS NOT NULL)::text, coalesce(used_by_user_id::text, '-') FROM invites ORDER BY created_at DESC LIMIT 1"
        Assert.Equal<string list>([ "true|-" ], invite)

        let! account = browser.GetRaw "/account"
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode)
        let! dashboard = getWithLanguage app.Client "en" "/u/animenerd"
        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode)
        let! me = getWithKey app.Client (Some key) "/api/me"
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode)

        use again = app.NewBrowser()
        let! landing = signInWith again "sub-1"
        Assert.Equal("/signup", landing.Path)
        let! invite2 = createInvite app
        let! reused = again.PostForm("/signup", "/signup", [ "invite", invite2; "username", "animenerd"; "displayName", "Back" ])
        Assert.Equal(HttpStatusCode.Redirect, reused.StatusCode)

        let! otherAfter = getWithLanguage app.Client "en" "/u/userb"
        let! otherHtmlAfter = readHtml otherAfter
        let stats (h: string) = Regex.Matches(h, "data-stat=\"(\\w+)\" data-value=\"([^\"]+)\"") |> Seq.map _.Value |> List.ofSeq
        Assert.Equal<string list>(stats otherHtmlBefore, stats otherHtmlAfter)
    }

[<Fact>]
let ``deletion needs a recent sign-in`` () =
    task {
        let clock = TestClock()
        let! db = newDatabase ()
        use! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        use browser = signedUp app "sub-1" "animenerd" |> fun t -> t.Result
        clock.Now <- clock.Now.AddMinutes 11.0
        let! blocked = browser.GetRaw "/account/delete"
        Assert.Equal("/signin/reconfirm?returnUrl=%2Faccount%2Fdelete", string blocked.Headers.Location)
        let! back = browser.Get(string blocked.Headers.Location + "&sub=sub-1")
        Assert.Equal("/account/delete", back.Path)
        Assert.Contains("name=\"confirmUsername\"", back.Html)
    }

[<Fact>]
let ``the delete page warns in Spanish`` () =
    task {
        use! app = newApp ()
        let! invite = createInvite app
        use browser = app.NewBrowser "es"
        let! _ = submitSignUp browser "sub-1" invite "animenerd" "AnimeNerd"
        let! page = browser.Get "/account/delete"
        Assert.Contains("lang=\"es\"", page.Html)
        Assert.Contains("No se puede deshacer", page.Html)
    }
