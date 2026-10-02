module FrunchyBackend.Handlers.Home

open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

let view (ctx: Microsoft.AspNetCore.Http.HttpContext) (t: Texts) (deleted: bool) =
    div(class' = "space-y-8") {
        if deleted then
            div(class' = "alert alert-success").data("account-deleted", "") { span() { t.AccountDeletedNotice } }

        div(class' = "hero bg-base-200 rounded-lg") {
            div(class' = "hero-content text-center") {
                div(class' = "max-w-xl") {
                    h1(class' = "text-5xl font-bold") { $"🎬 {t.SiteName}" }
                    p(class' = "py-4 text-xl") { t.HomeHeadline }
                    p(class' = "text-gray-400") { t.HomeIntro }
                }
            }
        }
        div(class' = "card bg-base-300 shadow-md max-w-xl mx-auto") {
            div(class' = "card-body") {
                h2(class' = "card-title") { t.HomeHowToSyncTitle }
                ol(class' = "list-decimal list-inside space-y-2") {
                    for step in t.HomeSteps do
                        li() { step }
                }
                p(class' = "text-sm text-gray-400 pt-2") { t.HomeProfilesNote }
            }
        }
    }
    |> Layout.page ctx t t.HomeTitle

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        let deleted = ctx.TryGetQueryValue "deleted" |> Option.isSome
        ctx.WriteHtmlView(view ctx (Texts.current ()) deleted)

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/" handler ]
