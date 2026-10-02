module FrunchyBackend.Handlers.Auth.Reconfirm

open Microsoft.Extensions.DependencyInjection
open Oxpecker
open FrunchyBackend.Infrastructure

// ---------------------------------------------------------------------------
// Handler: send the user to the provider they signed in with, asking it to prompt (research R6).
// Completion (refreshing auth_time) happens in Handlers/Auth/Complete.fs, reconfirm mode.
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun _ ctx ->
        let returnUrl = WebAuth.localReturnUrl (ctx.TryGetQueryValue "returnUrl" |> Option.defaultValue "") "/account"
        let providers = ctx.RequestServices.GetServices<AuthConfig.FrunchyProvider>()

        match WebAuth.currentProvider ctx |> Option.bind (fun name -> providers |> Seq.tryFind (fun p -> p.Name = name)) with
        | Some p -> WebAuth.challenge ctx p.Name p.Scheme "reconfirm" returnUrl p.ReconfirmPrompt
        | None -> WebAuth.redirectTo ctx "/account?msg=reconfirm-failed")

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/signin/reconfirm" handler ]
