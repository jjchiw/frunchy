# Contract: the shared design system (website ↔ browser extension)

## Tokens

The tokens are CSS custom properties declared once in `Views/Theme.fs` (`:root`). The **names and
values must match** the extension's `browser-extension/src/styles/tokens.css` for every name both
define. `DesignSystemTests` checks this when the sibling repository is present.

| Group | Names (both sides) |
|---|---|
| Colours | `--color-surface`, `--color-surface-container-low`, `--color-surface-container`, `--color-surface-container-high`, `--color-surface-container-highest`, `--color-on-surface`, `--color-on-surface-variant`, `--color-outline`, `--color-outline-variant`, `--color-primary`, `--color-on-primary`, `--color-primary-fixed`, `--color-secondary`, `--color-tertiary`, `--color-error`, `--color-error-container`, `--color-on-error-container` |
| Type | `--text-<style>-size`, `-line`, `-weight`, `-tracking` for `headline-md`, `body-lg`, `body-md`, `label-md`, `label-sm` |
| Radius | `--radius-sm` 0.25rem, `--radius-default` 0.5rem, `--radius-md` 0.75rem, `--radius-lg` 1rem, `--radius-full` |
| Spacing | `--space-xs`, `--space-sm`, `--space-md`, `--space-lg` |
| Effects | `--border-micro`, `--border-ghost`, `--edge-highlight`, `--shadow-level-2`, `--glow-primary` |

**Notes on the token lists:**
- **Website-only tokens**: the website may define more, such as the `display-hero`,
  `headline-xl`, `headline-lg` and `stat-counter` type styles, and `--color-secondary-container`.
  These names follow `DESIGN.md`'s YAML. If the extension later needs one, it takes the same name.
- **`--font-mono`** (JetBrains Mono, for keys, usernames and URLs) is website-only for now.
- **`--font-family`** is excluded from value parity. The extension bundles
  "Plus Jakarta Sans Variable"; the website loads "Plus Jakarta Sans" from Google Fonts.
- **Changing a shared token** means changing it on both sides and telling the other session.

## Components (`Views/Components.fs`)

| Component | Use |
|---|---|
| `card` | surface-container-low, radius-lg, `--border-micro` |
| `metricTile` | overline label (`label-sm`, uppercase) + value (`stat-counter`, tabular numbers) |
| `pill` | status and category chips (radius-full; primary 12% background + 1px border) |
| `genreBar` | 6px track, secondary→primary gradient fill, share at the end |
| `button` | `Primary` / `Secondary` / `Ghost` / `Destructive`; `providerButton` for Discord and Google |
| `field` | label, input, hint and error message (input gets `aria-invalid` and `aria-describedby`) |
| `alert` | `Success` / `Error` / `Info`, with `role="status"` or `role="alert"` |
| `emptyState`, `processing` | icon, title, body, optional action |
| `confirmLayout` | the destructive-action page (title, warning, field, destructive button, cancel link) |

Every interactive component has a visible `:focus-visible` ring and meets WCAG 2.1 AA contrast
(`DesignSystemTests` computes the token pairs). Motion is wrapped in
`@media (prefers-reduced-motion: no-preference)`.

## Test hooks that must survive the redesign (FR-022)

These attributes are what the existing tests read, so each page keeps every one of them on the
equivalent element:

`data-account`, `data-account-deleted`, `data-account-notice`, `data-claim-error`,
`data-claim-link`, `data-delete-account`, `data-delete-error`, `data-fr-time`, `data-genre`,
`data-genre-episodes`, `data-genre-share`, `data-genres-coverage`, `data-identity`,
`data-key-active`, `data-link-provider`, `data-mal-genres`, `data-mal-link`, `data-mal-score`,
`data-nav`, `data-new-sync-key`, `data-provider`, `data-recent-item`, `data-security-event`,
`data-signin-error`, `data-signin-unavailable`, `data-signup-error`, `data-stat`, `data-sync`,
`data-top-genres`, `data-value`.

Added by feature 004 (keep them too): `data-dna-link`, `data-copy-link`, `data-copy-done`,
`data-mal-studio`, `data-mal-format`, `data-mal-year`, `data-home-cta`, `data-home-step`, and every
DNA hook in `contracts/dna-page.md`.

## Pages and their mockups

| Page | Route | Mockup |
|---|---|---|
| Layout (navigation, footer) | all | `dashboard_frunchy` (navigation, footer) |
| DNA | `/u/{username}/dna` | `anime_dna_perfil_frunchy`, being redone (brief 5.2) |
| Dashboard | `/u/{username}` | `dashboard_frunchy`, plus brief 5.1 states |
| Home | `/` | brief 4.1 (pending) |
| Sign in / sign up / claim / reconfirm | `/signin`, `/signup`, `/claim`, `/signin/reconfirm` | brief 4.2–4.4, 4.7 (pending) |
| Account / key / delete | `/account`, `/account/key`, `/account/delete` | brief 4.5, 4.6, 4.8 (pending) |
| Message pages | 404, 500, 429, expired form | brief 4.9 (pending) |

A page without its mockup at build time uses the components and goes on the revisit list in
`quickstart.md`.
