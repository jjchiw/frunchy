module FrunchyBackend.Tests.ClaimTests

open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Dashboard stats + recent list as comparable text
let private dashboard (app: TestApp) (username: string) =
    task {
        let! response = getWithLanguage app.Client "en" $"/u/{username}"
        let! html = readHtml response
        let stats = Regex.Matches(html, "data-stat=\"(\\w+)\" data-value=\"([^\"]+)\"") |> Seq.map _.Value |> List.ofSeq
        let recent = Regex.Split(html, "data-recent-item") |> Array.skip 1 |> List.ofArray
        return stats, recent
    }

let private claim (app: TestApp) (subject: string) (key: string) =
    task {
        let browser = app.NewBrowser()
        let! page = signInWith browser subject
        Assert.Equal("/signup", page.Path)
        let! response = browser.PostForm("/claim", "/claim", [ "syncKey", key ])
        return browser, response
    }

[<Fact>]
let ``the owner claims an operator-created account with its sync key and keeps everything`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "jjchiw"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! before = dashboard app "jjchiw"

        let! browser, response = claim app "owner-sub" key
        use browser = browser
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/account", string response.Headers.Location)

        let! account = browser.Get "/account"
        Assert.Contains(">jjchiw<", account.Html)
        let! identities = Db.dump app.Db "SELECT u.username, i.provider, i.provider_user_id FROM user_identities i JOIN users u ON u.id = i.user_id"
        Assert.Equal<string list>([ "jjchiw|test|owner-sub" ], identities)
        let! events = Db.dump app.Db "SELECT event, provider, actor FROM security_events"
        Assert.Equal<string list>([ "claimed|test|user" ], events)

        let! after = dashboard app "jjchiw"
        Assert.Equal<string list>(fst before, fst after)
        Assert.Equal<string list>(snd before, snd after)
        Assert.Equal(4, (fst after).Length)

        // the extension's key keeps working and later sign-ins go straight in
        let! me = getWithKey app.Client (Some key) "/api/me"
        Assert.Equal(HttpStatusCode.OK, me.StatusCode)
        use later = app.NewBrowser()
        let! again = signInWith later "owner-sub"
        Assert.Equal("/account", again.Path)
    }

[<Fact>]
let ``a key of an already-linked account is refused and the owner is warned`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "jjchiw"
        let! first, _ = claim app "owner" key
        use first = first

        let! intruder, response = claim app "intruder" key
        use intruder = intruder
        let! html = response.Content.ReadAsStringAsync()
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        Assert.Contains("We couldn't verify that key.", WebUtility.HtmlDecode html)

        let! identities = Db.count app.Db "user_identities"
        Assert.Equal(1L, identities)
        let! events = Db.dump app.Db "SELECT event, provider FROM security_events ORDER BY id"
        Assert.Equal<string list>([ "claimed|test"; "claim_refused|test" ], events)
    }

[<Fact>]
let ``a wrong or revoked key gets the same generic refusal and records nothing`` () =
    task {
        use! app = newApp ()
        let! oldKey = createUser app.Db "jjchiw"
        let! _ = FrunchyBackend.Operations.RegenerateKey.regenerateKey app.Db "jjchiw"
        let! eventsBefore = Db.count app.Db "security_events"

        for key in [ "frk_definitely-not-a-key"; oldKey ] do
            let! browser, response = claim app "someone" key
            use browser = browser
            let! html = response.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
            Assert.Contains("We couldn't verify that key.", WebUtility.HtmlDecode html)

        let! eventsAfter = Db.count app.Db "security_events"
        let! identities = Db.count app.Db "user_identities"
        Assert.Equal(eventsBefore, eventsAfter)
        Assert.Equal(0L, identities)
    }

[<Fact>]
let ``claiming needs no invite and leaves invites untouched`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "jjchiw"
        let! _ = createInvite app
        let! browser, response = claim app "owner" key
        use browser = browser
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        let! unused = Db.scalar<int64> app.Db "SELECT count(*) FROM invites WHERE used_at IS NULL" []
        Assert.Equal(1L, unused)
    }

[<Fact>]
let ``claim attempts are rate limited`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! _ = signInWith browser "guesser"
        let statuses = ResizeArray<int>()

        for i in 1..6 do
            let! response = browser.PostForm("/claim", "/claim", [ "syncKey", $"frk_guess{i}" ])
            statuses.Add(int response.StatusCode)

        Assert.Equal<int list>([ 400; 400; 400; 400; 400; 429 ], List.ofSeq statuses)
    }

[<Fact>]
let ``the claim page needs a pending provider identity`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = browser.Get "/claim"
        Assert.Equal("/signin", page.Path)
    }

[<Fact>]
let ``sign-up offers the claim path`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser "es"
        let! page = signInWith browser "someone"
        Assert.Contains("href=\"/claim\"", page.Html)
        Assert.Contains("Ya tengo una cuenta de Frunchy", page.Html)
    }
