module FrunchyBackend.Handlers.Account.SyncKey

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

let confirmView (ctx: HttpContext) (t: Texts) =
    div(class' = "max-w-md mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.KeyConfirmTitle }
        p() { t.KeyConfirmBody }

        form(method = "post", action = "/account/key") {
            ctx.GetAntiforgeryInput()
            button(type' = "submit", class' = "btn btn-primary") { t.KeyConfirmButton }
        }

        a(href = "/account", class' = "link") { t.BackToAccount }
    }
    |> Layout.page ctx t t.KeyConfirmTitle

/// The only time the key is ever shown
let keyView (ctx: HttpContext) (t: Texts) (key: string) =
    div(class' = "max-w-xl mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.KeyShownTitle }
        p() { t.KeyShownBody }
        pre(class' = "bg-base-300 p-4 rounded-lg overflow-x-auto select-all") { code().data("new-sync-key", "") { key } }
        div(class' = "alert alert-warning") { span() { t.KeyShownOnce } }
        a(href = "/account", class' = "btn") { t.BackToAccount }
    }
    |> Layout.page ctx t t.KeyShownTitle

// ---------------------------------------------------------------------------
// Handlers: both need a sign-in within the last 10 minutes (FR-014)
// ---------------------------------------------------------------------------

let confirm: EndpointHandler =
    WebAuth.requireFresh "/account/key" (fun _ ctx -> ctx.WriteHtmlView(confirmView ctx (Texts.current ())))

let generate: EndpointHandler =
    WebAuth.requireFresh "/account/key" (fun userId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx
            let! key = AccountActions.regenerateKey (ctx.GetService<DbConfig>()) userId Actor.User
            ctx.Response.Headers.CacheControl <- "no-store"
            return! ctx.WriteHtmlView(keyView ctx (Texts.current ()) key)
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/account/key" confirm ]
      POST [ route "/account/key" generate |> configureEndpoint _.RequireRateLimiting("account-write") ] ]
