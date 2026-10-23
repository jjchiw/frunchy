module FrunchyBackend.Handlers.Account.SyncKey

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

/// Step 1: what creating a key does, then create it or go back
let confirmView (ctx: HttpContext) (t: Texts) =
    narrow () {
        div(class' = "fr-card space-y-6") {
            pageHeading t.KeyConfirmTitle None
            alert Notice t.KeyConfirmBody

            form(method = "post", action = "/account/key", class' = "flex flex-col gap-3 sm:flex-row sm:items-center") {
                ctx.GetAntiforgeryInput()

                button(type' = "submit", class' = btn Primary) {
                    icon Theme.Key
                    t.KeyConfirmButton
                }

                a(href = "/account", class' = btn Ghost) { t.BackToAccount }
            }
        }
    }
    |> Layout.page ctx t t.KeyConfirmTitle

/// The key in a monospace box, selectable as a whole, with a copy button when the Clipboard API exists.
/// The key is the box's only text (AuthHelpers.connectExtension reads it from data-new-sync-key).
let private keyBox (t: Texts) (key: string) =
    div(class' = "space-y-3").attr("x-data", "{ copied: false, canCopy: !!navigator.clipboard }") {
        div(class' = "rounded bg-surface-container-lowest p-4 font-mono text-body-lg text-primary break-all select-all border border-outline-variant")
            .data("new-sync-key", "")
            .attr("x-ref", "key")
            .attr("translate", "no") {
            key
        }

        button(type' = "button", class' = btnSm Secondary)
            .attr("x-show", "canCopy")
            .attr("x-on:click", "navigator.clipboard.writeText($refs.key.textContent.trim()).then(() => { copied = true; setTimeout(() => copied = false, 2000) })") {
            icon Theme.ContentCopy
            span().attr("x-show", "!copied") { t.CopyKey }
            span().attr("x-show", "copied").attr("style", "display: none") { t.KeyCopied }
        }

        span(class' = "sr-only").attr("aria-live", "polite") { span().attr("x-show", "copied").attr("style", "display: none") { t.KeyCopied } }
    }

/// What to do with the key: paste it, then the home page's last two sync steps
let private nextSteps (t: Texts) =
    let steps =
        t.KeyShownBody
        :: (match t.HomeSteps with
            | _ :: rest -> rest
            | [] -> [])

    div(class' = "space-y-4") {
        h2(class' = "text-headline-md text-on-surface") { t.HomeHowToSyncTitle }

        ol(class' = "space-y-3") {
            for i, step in List.indexed steps do
                li(class' = "flex items-start gap-3 text-body-md text-on-surface") {
                    span(class' = "flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-surface-container-high text-label-md text-primary")
                        .attr("aria-hidden", "true") {
                        string (i + 1)
                    }

                    span(class' = "pt-0.5") { step }
                }
        }

        a(href = "/guide#connect", class' = "fr-link text-body-md").data("guide-link", "") { t.GuideReadLink }
    }

/// Step 2, the only time the key is ever shown
let keyView (ctx: HttpContext) (t: Texts) (key: string) =
    narrow () {
        div(class' = "fr-card space-y-6") {
            pageHeading t.KeyShownTitle None
            keyBox t key
            alert Notice t.KeyShownOnce
            nextSteps t

            a(href = "/account", class' = btn Secondary) {
                icon Theme.ArrowBack
                t.BackToAccount
            }
        }
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
