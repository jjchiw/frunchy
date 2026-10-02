module FrunchyBackend.Handlers.Dashboard

open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Views.Layout

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

/// Stat card component
let statCard (label: string) (value: string) (icon: string) =
    div(class'="stat bg-base-300 rounded-lg shadow") {
        div(class'="stat-figure text-primary text-2xl") { icon }
        div(class'="stat-title") { label }
        div(class'="stat-value text-primary") { value }
    }

/// Watch event card
let watchCard (animeTitle: string) (episode: int) (watchedAt: string) =
    div(class'="card bg-base-300 shadow-md hover:shadow-lg transition") {
        div(class'="card-body p-4") {
            h3(class'="card-title text-sm") { animeTitle }
            p(class'="text-xs text-gray-500") { $"Episodio {episode}" }
            p(class'="text-xs text-gray-400") { watchedAt }
        }
    }

let view
    (totalAnimes: int)
    (totalEpisodes: int)
    (totalHours: float)
    (recentWatches: list<string * int * string>) =
    div() {
        div() {
            h1(class'="text-4xl font-bold mb-4") { "Dashboard" }
            p(class'="text-gray-400") { "Tu identidad anime en números" }
        }
        div(class'="stats stats-vertical lg:stats-horizontal shadow-lg w-full mb-8") {
            statCard "Total Animes" (string totalAnimes) "📺"
            statCard "Episodios Vistos" (string totalEpisodes) "▶️"
            statCard "Horas Vistas" (sprintf "%.1f" totalHours) "⏱️"
        }
        div() {
            h2(class'="text-2xl font-bold mb-4") { "Últimos Vistos" }
            if List.isEmpty recentWatches then
                div(class'="alert alert-info") { span() { "No hay visualizaciones recientes" } }
            else
                div(class'="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4") {
                    for title, ep, date in recentWatches do
                        watchCard title ep date
                }
        }
    }
    |> page "Dashboard"

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        // TODO: Fetch real stats from database
        let totalAnimes = 42
        let totalEpisodes = 1337
        let totalHours = 456.5
        let recentWatches = [
            ("Frieren: Beyond Journey's End", 17, "Hace 2 horas")
            ("DanDaDan", 3, "Hace 5 horas")
            ("Wind Breaker", 8, "Ayer")
        ]
        ctx.WriteHtmlView(view totalAnimes totalEpisodes totalHours recentWatches)

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/dashboard" handler ]
