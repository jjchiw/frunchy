module FrunchyBackend.Handlers.Profile

open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Views.Layout

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

/// Genre bar chart component
let genreBar (genreName: string) (percentage: float) =
    div(class'="space-y-1") {
        div(class'="flex justify-between text-xs") {
            span(class'="font-semibold") { genreName }
            span(class'="text-gray-400") { $"{percentage:F1}%%" }
        }
        div(class'="w-full bg-base-300 rounded-full h-2") {
            div(class'="bg-primary h-2 rounded-full transition-all").attr("style", $"width: {percentage:F1}%%") {}
        }
    }

/// Profile/Anime DNA page
let view
    (username: string)
    (topGenres: list<string * float>)
    (totalWatchTime: float)
    (favoriteAnime: option<string>) =

    let favoriteDisplay = favoriteAnime |> Option.defaultValue "Por descubrir"

    div(class'="space-y-8") {
        div() {
            h1(class'="text-4xl font-bold mb-2") { username }
            p(class'="text-gray-400") { "Tu identidad anime única" }
        }

        div(class'="grid grid-cols-1 lg:grid-cols-3 gap-6") {
            div(class'="lg:col-span-2 space-y-6") {
                if List.isEmpty topGenres then
                    div(class'="alert alert-info") { span() { "Importa tu historial para descubrir tu Anime DNA" } }
                else
                    div(class'="card bg-base-300 shadow-md") {
                        div(class'="card-body") {
                            h3(class'="card-title text-lg") { "Top Géneros" }
                            div(class'="space-y-4") {
                                for genre, pct in topGenres |> List.truncate 5 do
                                    genreBar genre pct
                            }
                        }
                    }
            }
            div(class'="space-y-6") {
                div(class'="card bg-base-300 shadow-md") {
                    div(class'="card-body") {
                        h3(class'="card-title") { "Tu Anime DNA" }
                        div(class'="divider my-2") {}
                        div(class'="stat stat-vertical") {
                            div(class'="stat-title") { "Horas Totales" }
                            div(class'="stat-value text-primary") { sprintf "%.1f" totalWatchTime }
                        }
                        div(class'="stat stat-vertical") {
                            div(class'="stat-title") { "Anime Favorito" }
                            div(class'="stat-value text-sm") { favoriteDisplay }
                        }
                    }
                }
                div(class'="alert alert-success") {
                    span() { "💡 Tu identidad anime está tomando forma" }
                }
            }
        }
    }
    |> page "Perfil"

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        // TODO: Fetch real profile data from database
        let username = "AnimeNerd"
        let topGenres = [
            ("Action", 28.5)
            ("Drama", 22.3)
            ("Fantasy", 18.7)
            ("Comedy", 15.2)
            ("Psychological", 9.8)
        ]
        let totalWatchTime = 456.5
        let favoriteAnime = Some "Frieren"
        ctx.WriteHtmlView(view username topGenres totalWatchTime favoriteAnime)

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/profile" handler ]
