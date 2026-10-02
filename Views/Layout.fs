module FrunchyBackend.Views.Layout

open Oxpecker.ViewEngine

/// Base HTML layout with DaisyUI + HTMX
let layout (titleText: string) (content: HtmlElement) =
    html() {
        head() {
            title() { $"{titleText} | Frunchy" }
            meta(charset="UTF-8")
            meta(name="viewport", content="width=device-width, initial-scale=1.0")
            script(src="https://cdn.tailwindcss.com")
            link(
                href="https://cdn.jsdelivr.net/npm/daisyui@4.11.1/dist/full.min.css",
                rel="stylesheet",
                type'="text/css"
            )
            script(src="https://cdn.jsdelivr.net/npm/htmx.org@4.0.0", crossorigin="anonymous")
            script(defer=true, src="https://cdn.jsdelivr.net/npm/alpinejs@3.x.x/dist/cdn.min.js")
            style() { "body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif; }" }
        }
        body(class'="bg-base-100").attr("data-theme", "dark") {
            div(class'="navbar bg-base-200 sticky top-0 z-50 shadow-lg") {
                div(class'="flex-1") {
                    a(href="/", class'="btn btn-ghost text-xl") { "🎬 Frunchy" }
                }
                div(class'="flex-none gap-2") {
                    a(href="/dashboard", class'="btn btn-ghost btn-sm") { "Dashboard" }
                    a(href="/profile", class'="btn btn-ghost btn-sm") { "Perfil" }
                }
            }
            div(class'="container mx-auto px-4 py-8") {
                content
            }
            footer(class'="footer footer-center bg-base-200 text-base-content p-4 mt-16") {
                div() { p() { "Frunchy © 2026 - Last.fm para anime" } }
            }
        }
    }

let page (title: string) (content: HtmlElement)  =
    layout title content

