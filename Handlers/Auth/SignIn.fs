module FrunchyBackend.Handlers.Auth.SignIn

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/web-account.md)
// ---------------------------------------------------------------------------

let view (ctx: HttpContext) (t: Texts) (providers: AuthConfig.FrunchyProvider list) (returnUrl: string) (failed: bool) =
    div(class' = "max-w-md mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.SignInTitle }
        p(class' = "text-gray-400") { t.SignInIntro }

        if failed then
            div(class' = "alert alert-warning").data("signin-error", "") { span() { t.SignInFailed } }

        match providers with
        | [] -> div(class' = "alert alert-info").data("signin-unavailable", "") { span() { t.SignInUnavailable } }
        | _ ->
            div(class' = "flex flex-col gap-3") {
                for provider in providers do
                    a(href = $"/signin/{provider.Name}?returnUrl={Uri.EscapeDataString returnUrl}", class' = "btn btn-primary")
                        .data("provider", provider.Name) {
                        t.SignInWith provider.DisplayName
                    }
            }
    }
    |> Layout.page ctx t t.SignInTitle

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private providers (ctx: HttpContext) =
    ctx.RequestServices.GetServices<AuthConfig.FrunchyProvider>() |> List.ofSeq

let private returnUrlOf (ctx: HttpContext) =
    WebAuth.localReturnUrl (ctx.TryGetQueryValue "returnUrl" |> Option.defaultValue "") "/account"

let page: EndpointHandler =
    fun ctx ->
        let failed = ctx.TryGetQueryValue "error" |> Option.isSome
        ctx.WriteHtmlView(view ctx (Texts.current ()) (providers ctx) (returnUrlOf ctx) failed)

/// Start the provider round trip in sign-in mode
let start (provider: string) : EndpointHandler =
    fun ctx ->
        match providers ctx |> List.tryFind (fun p -> p.Name = provider) with
        | Some p -> WebAuth.challenge ctx p.Name p.Scheme "signin" (returnUrlOf ctx) None
        | None ->
            ctx.SetStatusCode StatusCodes.Status404NotFound
            let t = Texts.current ()
            ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    GET [
        route "/signin" page |> configureEndpoint _.RequireRateLimiting("signin")
        routef "/signin/{%s}" start |> configureEndpoint _.RequireRateLimiting("signin")
    ]
