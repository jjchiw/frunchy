module FrunchyBackend.Handlers.Auth.SignIn

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/web-account.md; design/frunchy_sign_in_signin, per design/REVIEW.md)
// ---------------------------------------------------------------------------

/// The provider's own mark, for the providers that have one (the tests' fake providers don't)
let private providerMark (provider: string) : HtmlElement =
    match provider with
    | "google" ->
        raw
            """<svg class="h-[18px] w-[18px] shrink-0" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"/><path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/><path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z"/><path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z"/></svg>"""
    | _ -> Fragment()

let view (ctx: HttpContext) (t: Texts) (providers: AuthConfig.FrunchyProvider list) (returnUrl: string) (failed: bool) =
    Fragment() {
        pageHeading t.SignInTitle (Some t.SignInIntro)

        if failed then
            (alert Failure t.SignInFailed).data("signin-error", "")

        match providers with
        | [] -> (alert Notice t.SignInUnavailable).data("signin-unavailable", "") :> HtmlElement
        | _ ->
            div(class' = "flex flex-col gap-3") {
                for provider in providers do
                    a(href = $"/signin/{provider.Name}?returnUrl={Uri.EscapeDataString returnUrl}", class' = btn Secondary + " w-full")
                        .data("provider", provider.Name) {
                        providerMark provider.Name
                        t.SignInWith provider.DisplayName
                    }
            }

        div(class' = "border-t border-outline-variant/40 pt-4") {
            // Ghost button pulled left so its text lines up with the card's content
            a(href = "/", class' = btnSm Ghost + " -ml-3.5") {
                icon Theme.ArrowBack
                t.BackHome
            }
        }
    }
    |> centredCard
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
