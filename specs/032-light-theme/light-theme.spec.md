# Feature Specification: Light theme

**Feature Branch**: `032-light-theme`

**Created**: 2026-11-07

**Status**: Implemented

**Input**: Frunchy is dark only: `Views/Theme.fs` writes the Night Broadcast tokens under `:root { color-scheme: dark;
… }` and every page reads them. The owner wants a light theme too (task `2026-11-07-add-a-light-theme-hg3h`).

## Clarifications

### Session 2026-11-07

- Q: Which theme shows by default? → A: The system's (`prefers-color-scheme`). Without JavaScript the system setting
  decides; with no system preference, dark (today's look).
- Q: How does a person override it? → A: A footer switch next to the language link offering Light, Dark and System,
  kept in a preference cookie `frunchy.theme` (`light`, `dark` or `system`), one year, `Path=/`, `SameSite=Lax`,
  `HttpOnly`, `Secure` over https, exactly like `frunchy.lang` (feature 024). Not stored on the account. A missing
  or invalid value means `system`.
- Q: How is it set? → A: A plain link, `GET /theme/{light|dark|system}?returnUrl=<local path and query>`: it sets
  the cookie and answers 303 to `returnUrl` checked with `WebAuth.localReturnUrl` (fallback `/`). No rate limit, no
  antiforgery (a preference cookie only). An unknown mode is the plain 404 and sets no cookie.
- Q: No flash? → A: The server renders `<html data-theme="light|dark|system">` from the cookie. The CSS needs no
  script: dark tokens on `:root`, the light tokens on `[data-theme="light"]` and, inside
  `@media (prefers-color-scheme: light)`, on `[data-theme="system"]`. `color-scheme`, `<meta name="color-scheme">`
  and `<meta name="theme-color">` follow (two `theme-color` metas with `media` for `system`).
- Q: The extension shares the tokens (`browser-extension/src/styles/tokens.css`)? → A: The dark palette keeps its
  names and values, so the parity test keeps holding and is scoped to the dark tokens. The light palette is
  Frunchy's own (`Theme.lightColors`, `Theme.lightEffects`); the extension may adopt it later (its session is told;
  it is not running now).
- Q: Tailwind utilities? → A: Every Tailwind colour resolves to the CSS custom property
  (`color-mix(in srgb, var(--color-X) calc(<alpha-value> * 100%), transparent)`), so `bg-primary/10` and every
  other opacity modifier follow the theme. Nothing in a view names a hex colour except third-party marks (Google's
  "G").

## Design

The frunchy_designer session's direction (`design-direction.md`). It is binding.

## Requirements *(mandatory)*

- **FR-001**: `Theme.lightColors` defines a light value for every name in `Theme.colors`, and `Theme.lightEffects`
  for every colour-bearing effect (`--border-micro`, `--border-ghost`, `--edge-highlight`, `--shadow-level-2`,
  `--glow-primary`). `DesignSystemTests` checks WCAG AA for both palettes with the same pairs.
- **FR-002**: `GET /theme/{mode}` for `light`, `dark` and `system` sets `frunchy.theme` with the attributes above and
  answers 303 to a local `returnUrl` (else `/`). Any other mode answers the plain 404 and sets nothing.
- **FR-003**: Every page renders `<html data-theme>` with the cookie's mode (`system` when missing or invalid), and
  its CSS shows the light tokens for `light`, the dark ones for `dark`, and the system's for `system`, with no
  script.
- **FR-004**: Every page's footer has `<nav aria-label data-theme-switch>` next to the language switch, one link per
  mode (`data-theme-option="light|dark|system"`, `href="/theme/{mode}?returnUrl={this page, URL-encoded}"`), the
  current one marked `aria-current="true"`.
- **FR-005**: Everything follows the theme: charts and SVG trends, poster placeholders, progress tracks and bars,
  milestone and season tiles, share and copy widgets, the invite dialog, focus rings, selection, scrollbars, the
  mark. The favicon keeps its dark tile (it must read on light and dark browser chrome).
- **FR-006**: All copy is in `Views/Texts.fs`, English and Spanish.
- **FR-007**: The page snapshot harness renders every page in both themes (`tools/snapshots/`).
- **FR-008**: No change to the account, the data, the API, or any shared dark token.

## Success Criteria *(mandatory)*

- **SC-001**: With a light system setting and no cookie, every page is light on first paint, with no flash; with a
  dark setting it looks exactly as before.
- **SC-002**: One click in the footer forces Light or Dark on every later page and visit (one year); System returns
  to following the system.
- **SC-003**: Every text and control pair passes WCAG AA in both palettes (tested).
