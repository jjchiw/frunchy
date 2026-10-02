module FrunchyBackend.Tests.AuthHelpers

open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let createInvite (app: TestApp) : Task<string> =
    task {
        let! invite = InviteCreate.createInvite app.Db 7
        return invite.Code
    }

/// A full round trip through the fake provider; returns the final page
let signInWith (browser: Browser) (subject: string) : Task<Page> = browser.Get $"/signin/test?sub={subject}"

/// From an unknown identity to a submitted sign-up form; returns the response to the POST
let submitSignUp (browser: Browser) (subject: string) (invite: string) (username: string) (displayName: string) : Task<HttpResponseMessage> =
    task {
        let! _ = signInWith browser subject
        return! browser.PostForm("/signup", "/signup", [ "invite", invite; "username", username; "displayName", displayName ])
    }

/// Sign up a brand-new account and return the signed-in browser
let signedUp (app: TestApp) (subject: string) (username: string) : Task<Browser> =
    task {
        let! invite = createInvite app
        let browser = app.NewBrowser()
        let! response = submitSignUp browser subject invite username (username.ToUpperInvariant())

        if int response.StatusCode <> 302 then
            let! body = response.Content.ReadAsStringAsync()
            failwith $"sign-up failed: {response.StatusCode} {body}"

        return browser
    }

/// Generate a sync key from the account page and return it
let connectExtension (browser: Browser) : Task<string> =
    task {
        let! response = browser.PostForm("/account/key", "/account/key", [])
        let! html = response.Content.ReadAsStringAsync()
        let m = Regex.Match(html, "data-new-sync-key=\"\"[^>]*>([^<]+)<")
        if not m.Success then failwith $"no key shown: {response.StatusCode}"
        return m.Groups[1].Value
    }
