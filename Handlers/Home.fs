module FrunchyBackend.Handlers.Home

open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Views.Layout

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

let view () =
    div(class'="hero bg-base-200 rounded-lg") {
        div(class'="hero-content text-center") {
            div(class'="max-w-md") {
                h1(class'="text-5xl font-bold") { "🎬 Frunchy" }
                p(class'="py-6") { "Last.fm para anime. Tu identidad anime en números." }
                a(href="/dashboard", class'="btn btn-primary") { "Ir al Dashboard" }
            }
        }
    }
    |> page "Inicio"

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx -> ctx.WriteHtmlView(view ())

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/" handler ]
