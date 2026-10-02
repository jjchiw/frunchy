# Frunchy Frontend: Server-Rendered Views

## Stack

- **[Oxpecker.ViewEngine](https://lanayx.github.io/Oxpecker/src/Oxpecker.ViewEngine/)**: HTML written as F# computation expressions. Type-checked and HTML-escaped by default, no template files.
- **[HTMX 4](https://four.htmx.org)** through [Oxpecker.Htmx](https://lanayx.github.io/Oxpecker/src/Oxpecker.Htmx/): server-driven interactions (`.hxGet(...)`, `.hxTarget(...)`, ...).
- **[Alpine.js](https://alpinejs.dev)** through [Oxpecker.Alpine](https://lanayx.github.io/Oxpecker/src/Oxpecker.Alpine/): small client-side state (`.xData(...)`, `.xOn(...)`, ...).
- **Tailwind CSS + DaisyUI**: styling and components.

Tailwind, DaisyUI, htmx and Alpine are all loaded from CDNs in `Views/Layout.fs`, so there is no frontend build step. Keep the htmx script on **4.x**, because the `Oxpecker.Htmx` package emits htmx 4 attributes.

## Where views live

Views are part of their endpoint's **vertical slice** in `Handlers/`. Each slice file has the view at the top and the route at the bottom:

```fsharp
module FrunchyBackend.Handlers.Dashboard

// View
let statCard (label: string) (value: string) (icon: string) =
    div(class'="stat bg-base-300 rounded-lg shadow") { ... }

let view totalAnimes totalEpisodes totalHours recentWatches =
    div() { ... statCard "Total Animes" (string totalAnimes) "📺" ... }
    |> page "Dashboard"            // wrap in the shared layout

// Handler
let handler: EndpointHandler =
    fun ctx -> ctx.WriteHtmlView(view ...)

// Endpoint
let endpoint = GET [ route "/dashboard" handler ]
```

Small components such as `statCard`, `watchCard` and `genreBar` stay in the slice that uses them. Move a component to `Views/` only once a second slice needs it.

`Views/Layout.fs` holds `page title content`, which renders `<html>` with the CDN scripts, the dark-theme navbar (`data-theme="dark"`) and the footer.

## Pages

| Route | Slice | Content |
|---|---|---|
| `GET /` | `Handlers/Home.fs` | Hero with a link to the dashboard |
| `GET /dashboard` | `Handlers/Dashboard.fs` | Stat cards (animes, episodes, hours) and a grid of recent watches |
| `GET /profile` | `Handlers/Profile.fs` | Top 5 genres with percentage bars and the "Anime DNA" card (hours, favorite) |

Dashboard and profile still render **hardcoded placeholder data** (marked `TODO` in their handlers). Today the views have hardcoded Spanish text. Per constitution v1.1.0 all UI text moves to per-language text (English default, Spanish), which is being introduced in feature 001.

## View-engine notes

- Attributes that clash with F# keywords take a trailing `'`: `class'`, `type'`, `for'`.
- Arbitrary attributes: `.attr("name", "value")`. `data-*` attributes: `.data("key", "value")`.
- Text is escaped. Use `raw "..."` for trusted HTML only.
- `for`, `if` and `match` work inside the builders. When `match` branches return different element types, upcast with `:> HtmlElement`, or use `Fragment()` for "nothing".
- Very long CE views slow down compilation, so split them into small functions.

See the `oxpecker` skill (`.claude/skills/oxpecker/`) for the full API, htmx and Alpine idioms, and which features the pinned package versions support.

## Design references

`design/` holds mockups (`dashboard_frunchy/`, `anime_dna_perfil_frunchy/`: `code.html` + `screen.png`) and `neo_scrobble_profile/DESIGN.md`, which has the intended color, typography and spacing tokens. The current views use stock DaisyUI dark styling and haven't been aligned with those mockups yet.

## Next steps

1. **Real data**: query `watch_events` and the projection tables inside the Dashboard and Profile slices.
2. **Import UI**: upload a history file and poll its status with htmx (`hxTrigger("load delay:2s")` on a status fragment, as in the upstream ContactApp archive example).
3. **Authentication**: identify the user instead of hardcoding one.
