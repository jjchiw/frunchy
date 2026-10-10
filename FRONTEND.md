# Frunchy Frontend: Server-Rendered Views

## Stack

- **[Oxpecker.ViewEngine](https://lanayx.github.io/Oxpecker/src/Oxpecker.ViewEngine/)**: HTML written as F#
  computation expressions. Type-checked and HTML-escaped by default (non-ASCII is written as `&#NNN;`,
  which browsers render normally).
- **[HTMX 4](https://four.htmx.org)** via [Oxpecker.Htmx](https://lanayx.github.io/Oxpecker/src/Oxpecker.Htmx/)
  and **[Alpine.js](https://alpinejs.dev)** via [Oxpecker.Alpine](https://lanayx.github.io/Oxpecker/src/Oxpecker.Alpine/):
  loaded and ready, but feature 001 doesn't use them yet.
- **Tailwind CSS** (CDN) on the shared design system: tokens in `Views/Theme.fs` (the same names and
  values as the extension's `tokens.css`), components in `Views/Components.fs`, fonts and icons from
  Google Fonts. DaisyUI is gone (feature 004).

Everything comes from CDNs in `Views/Layout.fs`; there's no frontend build step. Keep the htmx script on
**4.x**, because the `Oxpecker.Htmx` package emits htmx 4 attributes.

## Pages

| Route | Slice | Content |
|---|---|---|
| `GET /` | `Handlers/Home.fs` | what Frunchy is and how to sync from the extension (no user list) |
| `GET /u/{username}` | `Handlers/Users/Dashboard.fs` | public dashboard: series, episodes, hours, days, 6 most recent episodes, last sync, sync-in-progress notice, empty state |
| unknown `/u/…` or any other path | `Program.fs` / the slice | localized 404 page (`/api/…` paths get the JSON error shape instead) |
| `GET /signin`, `/signup`, `/claim` | `Handlers/Auth/*.fs` | provider buttons; invite + username + display name; "I already have an account" (sync key) |
| `GET /u/{username}/dna` | `Handlers/Users/Dna.fs` | the Anime DNA (feature 004) |
| `GET /u/{username}/year`, `/u/{username}/year/{year}` | `Handlers/Users/Year.fs` | the year in review (feature 005); the first redirects to the newest year with history |
| `GET /u/{username}/anime/{seriesId}` | `Handlers/Users/Anime.fs` | one series as the user watched it (feature 006); series titles elsewhere link here |
| `GET /review/mal`, `/review/mal/manual` (+ POST `set`, `none`, `retry`, `auto`) | `Handlers/Review/MalReview.fs` | operators only (feature 007; 404 for everyone else): seasons the matcher couldn't decide with their candidates, an id/link field, retry and "not on MyAnimeList"; the manual decisions with "back to automatic" |
| `GET /account`, `/account/key`, `/account/delete` | `Handlers/Account/*.fs` | private owner pages: display name, connect extension (key shown once), Crunchyroll binding, sign-in methods, last 10 syncs, last 20 security events, deletion |

The old placeholder `/dashboard` and `/profile` pages were removed in feature 001: they showed invented
numbers in hardcoded Spanish. The "Anime DNA" profile comes back with feature 004. The page contract
(including the `data-stat` / `data-recent-item` test hooks) is in
`specs/001-crunchyroll-history-import/contracts/dashboard-ui.md`.

## Where views live

Views belong to their endpoint's **vertical slice** in `Handlers/`: view at the top, then the slice's SQL,
the handler, and the route at the bottom. Small components (stat cards, recent items) stay in the slice
that uses them. Only cross-slice UI lives in `Views/`:

- `Views/Layout.fs`: `page ctx texts title content` (`<html lang>`, CDN scripts, navbar, footer, the time
  script), `messagePage` for 404, 429 and error pages, and `timeElement`, the shared `<time>` helper.
- The **navbar** shows "Sign in" when signed out. Signed in, it shows the display name, "Account" and a
  sign-out button, which is a POST form with an antiforgery token: sign-out is never a GET link.
- **Forms**: every state-changing form renders `ctx.GetAntiforgeryInput()` and posts. Actions answer with a
  redirect to `/account?msg=…`, which shows a localized notice.
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

## Design system

- **Tokens** (`Views/Theme.fs`): CSS custom properties named like the extension's
  (`--color-*`, `--text-<style>-*`, `--radius-*`, `--space-*`), also mapped into the Tailwind CDN
  config, so mockup classes such as `bg-surface-container-low`, `text-on-surface-variant` or
  `rounded-lg` resolve to them. `DesignSystemTests` checks parity with the extension and WCAG AA
  contrast.
- **Components** (`Views/Components.fs`): card, metric tile, pill, share bar, buttons, field, alert,
  processing, empty state, icons (Material Symbols, subset to `Theme.Icon`), the mark. Pages build
  on these; add a new icon to `Theme.Icon` so the font subset includes it.
- **Test hooks are `data-*` attributes only**, never classes; `contracts/design-system.md` in
  feature 004 lists the ones the tests read. Restyling must keep every one.
- **Design work** goes through the `impeccable` skill (shape → build → audit), and the craft floor
  applies: no eyebrow labels above headings, no emoji icons, plain copy.

## Design references

`design/` holds one folder per screen from the designer (`code.html` + `screen.png`),
`neo_scrobble_profile/DESIGN.md` (the tokens), `brand/frunchy-mark.svg` (the mark) and
`REVIEW.md`, which says what to take and what to drop from each mockup. Mockup elements without a
real data source (ratings, ranks, trends, followers, avatars) stay out until a feature adds the data.
