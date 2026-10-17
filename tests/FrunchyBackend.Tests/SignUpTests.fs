module FrunchyBackend.Tests.SignUpTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private rowCounts (app: TestApp) =
    task {
        let! users = Db.count app.Db "users"
        let! identities = Db.count app.Db "user_identities"
        let! events = Db.count app.Db "security_events"
        return users, identities, events
    }

let private assertRefused (app: TestApp) (invite: string) (username: string) (displayName: string) (expected: string) =
    task {
        use browser = app.NewBrowser()
        let! before = rowCounts app
        let! response = submitSignUp browser $"sub-{Guid.NewGuid():N}" invite username displayName
        let! html = response.Content.ReadAsStringAsync()
        let! after = rowCounts app
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        Assert.Contains(expected, Net.WebUtility.HtmlDecode html)
        Assert.Equal(before, after)
    }

[<Fact>]
let ``every invalid invite is refused with the same message and creates nothing`` () =
    task {
        use! app = newApp ()
        let! used = createInvite app
        use _ = signedUp app "first" "first" |> fun t -> t.Result
        let! usedCode = createInvite app
        use consumer = app.NewBrowser()
        let! _ = submitSignUp consumer "consumer" usedCode "consumer" "C"

        let! revoked = FrunchyBackend.Operations.InviteCreate.createInvite app.Db 7
        let! _ = FrunchyBackend.Operations.InviteRevoke.revokeInvite app.Db revoked.Id
        let! expired = FrunchyBackend.Operations.InviteCreate.createInvite app.Db 7
        do! Db.execute app.Db $"UPDATE invites SET expires_at = now() - interval '1 second' WHERE id = '{expired.Id}'"

        for code in [ ""; "inv_NOTAREALCODE"; usedCode; revoked.Code; expired.Code ] do
            do! assertRefused app code "newbie" "Newbie" "This invite code isn't valid."

        ignore used
    }

[<Fact>]
let ``username and display name rules`` () =
    task {
        use! app = newApp ()
        use _ = signedUp app "first" "taken" |> fun t -> t.Result
        let! code = createInvite app
        do! assertRefused app code "taken" "X" "That username is already taken."
        do! assertRefused app code "a b" "X" "That username isn't allowed"
        do! assertRefused app code "admin" "X" "That username isn't allowed"
        do! assertRefused app code "newbie" "" "The display name must be 1-60 characters."
        do! assertRefused app code "newbie" (String('x', 61)) "The display name must be 1-60 characters."
        // the invite survived all refusals
        let! state = Db.scalar<bool> app.Db "SELECT bool_and(used_at IS NULL) FROM invites WHERE used_by_user_id IS NULL" []
        Assert.True state
    }

[<Fact>]
let ``a trailing dash is refused with the username error and creates nothing, but _ and inner dashes are fine`` () =
    task {
        use! app = newApp ()
        let! code = createInvite app
        do! assertRefused app code "abc-" "X" "That username isn't allowed"

        // the invite survived the refusal, so it still works for allowed names
        use underscore = app.NewBrowser()
        let! response = submitSignUp underscore "sub-underscore" code "abc_" "Underscore"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        let! second = createInvite app
        use inner = app.NewBrowser()
        let! response = submitSignUp inner "sub-inner" second "a-b" "Inner"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        let! names = Db.dump app.Db "SELECT username FROM users ORDER BY username"
        Assert.Equal<string list>([ "a-b"; "abc_" ], names)
    }

[<Fact>]
let ``a valid sign-up creates the account, consumes the invite and signs in`` () =
    task {
        use! app = newApp ()
        let! code = createInvite app
        use browser = app.NewBrowser()
        let! response = submitSignUp browser "sub-1" code "AnimeNerd" "Anime Nerd"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/account", string response.Headers.Location)

        let! user = Db.dump app.Db "SELECT username, display_name, coalesce(sync_key_hash,'-') FROM users"
        let! identity = Db.dump app.Db "SELECT provider, provider_user_id FROM user_identities"
        let! invite = Db.dump app.Db "SELECT u.username FROM invites i JOIN users u ON u.id = i.used_by_user_id WHERE i.used_at IS NOT NULL"
        let! events = Db.dump app.Db "SELECT event, actor FROM security_events"
        Assert.Equal<string list>([ "animenerd|Anime Nerd|-" ], user)
        Assert.Equal<string list>([ "test|sub-1" ], identity)
        Assert.Equal<string list>([ "animenerd" ], invite)
        Assert.Equal<string list>([ "account_created|user" ], events)

        let! account = browser.Get "/account"
        Assert.Contains("data-account=\"username\"", account.Html)
        Assert.Contains("data-key-active=\"false\"", account.Html)
    }

[<Fact>]
let ``two sign-ups racing for one invite create exactly one account`` () =
    task {
        use! app = newApp ()
        let! code = createInvite app
        use a = app.NewBrowser()
        use b = app.NewBrowser()
        let! _ = signInWith a "racer-a"
        let! _ = signInWith b "racer-b"

        let! results =
            Task.WhenAll(
                [| a.PostForm("/signup", "/signup", [ "invite", code; "username", "racera"; "displayName", "A" ])
                   b.PostForm("/signup", "/signup", [ "invite", code; "username", "racerb"; "displayName", "B" ]) |]
            )

        let statuses = results |> Array.map (fun r -> int r.StatusCode) |> Array.sort
        Assert.Equal<int array>([| 302; 400 |], statuses)
        let! users = Db.count app.Db "users"
        Assert.Equal(1L, users)
    }

[<Fact>]
let ``an abandoned sign-up leaves nothing behind`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = signInWith browser "dropout"
        Assert.Equal("/signup", page.Path)
        let! counts = rowCounts app
        Assert.Equal((0L, 0L, 0L), counts)
        // coming back later asks again
        use later = app.NewBrowser()
        let! again = signInWith later "dropout"
        Assert.Equal("/signup", again.Path)
    }

[<Fact>]
let ``sign-up without a pending provider identity goes to sign-in`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = browser.Get "/signup"
        Assert.Equal("/signin", page.Path)
    }
