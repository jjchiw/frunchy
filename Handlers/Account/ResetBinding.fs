module FrunchyBackend.Handlers.Account.ResetBinding

open Microsoft.AspNetCore.Builder
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Handler: same effect as the operator's user-reset-binding (FR-011), history kept
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx
            do! AccountActions.resetBinding (ctx.GetService<DbConfig>()) userId Actor.User
            return! WebAuth.redirectTo ctx "/account?msg=binding-reset"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/account/binding/reset" handler |> configureEndpoint _.RequireRateLimiting("account-write") ]
