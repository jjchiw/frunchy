# Frunchy Frontend: Server-Rendered Views

## Stack

- **[Oxpecker.ViewEngine](https://lanayx.github.io/Oxpecker/src/Oxpecker.ViewEngine/)**: HTML written as F#
  computation expressions. Type-checked and HTML-escaped by default (non-ASCII is written as `&#NNN;`,
  which browsers render normally).
- **[HTMX 4](https://four.htmx.org)** via [Oxpecker.Htmx](https://lanayx.github.io/Oxpecker/src/Oxpecker.Htmx/)
  and **[Alpine.js](https://alpinejs.dev)** via [Oxpecker.Alpine](https://lanayx.github.io/Oxpecker/src/Oxpecker.Alpine/):
  loaded and ready, but feature 001 doesn't use them yet.
- **Tailwind CSS + DaisyUI** for styling.

Everything comes from CDNs in `Views/Layout.fs`; there's no frontend build step. Keep the htmx script on
**4.x**, because the `Oxpecker.Htmx` package emits htmx 4 attributes.

## Pages

| Route | Slice | Content |
|---|---|---|
| `GET /` | `Handlers/Home.fs` | what Frunchy is and how to sync from the extension (no user list) |
| `GET /u/{username}` | `Handlers/Users/Dashboard.fs` | public dashboard: series, episodes, hours, days, 6 most recent episodes, last sync, sync-in-progress notice, empty state |
| unknown `/u/…` or any other path | `Program.fs` / the slice | localized 404 page (`/api/…` paths get the JSON error shape instead) |

The old placeholder `/dashboard` and `/profile` pages were removed in feature 001: they showed invented
numbers in hardcoded Spanish. The "Anime DNA" profile comes back with feature 004. The page contract
(including the `data-stat` / `data-recent-item` test hooks) is in
`specs/001-crunchyroll-history-import/contracts/dashboard-ui.md`.

## Where views live

Views belong to their endpoint's **vertical slice** in `Handlers/`: view at the top, then the slice's SQL,
the handler, and the route at the bottom. Small components (stat cards, recent items) stay in the slice
that uses them. Only cross-slice UI lives in `Views/`:

- `Views/Layout.fs`: `page texts title content` (`<html lang>`, CDN scripts, navbar, footer, the time
  script) and `messagePage` for 404 and error pages.
- `Views/Texts.fs`: every user-facing string.

## Languages (en default, es)

- The language comes from the request's `Accept-Language` header: Spanish when the browser prefers it,
  English otherwise (unsupported languages too). ASP.NET Core's `UseRequestLocalization` resolves it per
  request, so full pages and future htmx fragments are localized the same way. There's no switcher or
  cookie yet.
- `Views/Texts.fs` defines one F# record `Texts` with a value per language (`en`, `es`). Views take it as
  a parameter: `Texts.current ()` in the handler.
  - **Add a string**: add a field to the record and a value in both `en` and `es`. The compiler refuses to
    build until every language has it.
  - **Add a language**: add a third value, extend `forCulture`, and add the culture in
    `Program.localizationOptions`. No view changes.
- Numbers are formatted with the text set's `Culture` (`118.2` vs `118,2`). Anime titles are shown as
  Crunchyroll sent them, untranslated.
- Never put user-facing literals in views. `Views/Texts.fs` is the only place for them.

## Times in the viewer's time zone

The server renders exact UTC instants with an unambiguous fallback:

```html
<time datetime="2026-09-30T22:46:02Z" data-fr-time="relative">30 Sep 2026, 22:46 UTC</time>
```

The inline script in `Views/Layout.fs` rewrites each `time[data-fr-time]` with `Intl.RelativeTimeFormat`
plus `Intl.DateTimeFormat`, in `<html lang>` and the viewer's own time zone. For example, "hace 2 horas ·
2 oct 2026, 11:00" in Madrid, or "2 days ago · Sep 30, 2026, 7:46 PM" in Buenos Aires. Between 1 and 7
days it counts calendar days in the viewer's zone, so "ayer" / "yesterday" means the previous local date.

## Design references

`design/` holds mockups (`dashboard_frunchy/`, `anime_dna_perfil_frunchy/`,
`extensi_n_frunchy_popup_sincronizador/`: `code.html` + `screen.png`) and `neo_scrobble_profile/DESIGN.md`
(color and typography tokens). The dashboard uses stock DaisyUI dark styling and the parts of the design
that real data supports. Ratings, ranks, trends, weekly activity and genres are out of scope until a
feature adds their data source.
