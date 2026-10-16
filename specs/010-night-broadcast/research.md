# Research: Night Broadcast Design System

## R1. Palette (same names, new values)

Contrast was computed with the WCAG 2.1 formula `DesignSystemTests` uses. Every pair the test checks
passes: text on all five surfaces ≥ 4.5, `on-*` pairs ≥ 4.5, outline as a UI boundary ≥ 3.

| Token | Neo-Scrobble | Night Broadcast | Job |
|---|---|---|---|
| `surface` | `#0f131c` | `#13111a` | the page (night) |
| `surface-container-lowest` | `#0a0e16` | `#0d0b12` | wells, inputs on cards |
| `surface-container-low` | `#181c24` | `#1a1722` | cards |
| `surface-container` | `#1c2028` | `#1e1a27` | inputs, raised rows |
| `surface-container-high` | `#262a33` | `#2a2435` | secondary buttons, thumbnails' placeholder |
| `surface-container-highest` | `#31353e` | `#352e42` | tracks, hover |
| `on-surface` | `#dfe2ee` | `#f1ece4` | text (warm paper) |
| `on-surface-variant` | `#bcc9cd` | `#d2cabe` | secondary text |
| `outline` | `#869397` | `#9b9286` | placeholders, input borders |
| `outline-variant` | `#3d494c` | `#4a4255` | dividers |
| `primary` | `#4cd7f6` | `#ffb547` | amber: primary action, the page's own figures |
| `on-primary` | `#003640` | `#2a1700` | text on amber |
| `primary-container` | `#06b6d4` | `#e0942a` | darker amber (mark, pressed) |
| `primary-fixed` | `#acedff` | `#ffd08a` | primary hover |
| `secondary` | `#c0c1ff` | `#ff6fa8` | rose: other people, spoilers (lowest ratio 4.99 on `highest`) |
| `secondary-container` | `#3131c0` | `#5c2342` | rose wells |
| `tertiary` | `#ffb0cd` | `#c9bcff` | lilac: tags, info |
| `error` | `#ffb4ab` | `#ff9b85` | coral, distinct from rose and amber |
| `on-error` | `#690005` | `#4a0b00` | |
| `error-container` | `#93000a` | `#8c1d0e` | |
| `on-error-container` | `#ffdad6` | `#ffdad3` | |

Notes:
- **`outline` on `surface-container-highest`** is 4.23:1. It's a border there, never text; the
  existing comment in the test still holds.
- **Selection colour** (`::selection`) moves to amber at 30%.
- **`theme-color` and the favicon tile** use `#13111a`.

## R2. Fonts

| Token | Value | Use |
|---|---|---|
| `--font-family` | `"Figtree", system-ui, sans-serif` | text (body, labels, buttons) |
| `--font-display` (**new**) | `"Bricolage Grotesque", "Figtree", system-ui, sans-serif` | h1–h3, the brand wordmark |
| `--font-mono` | `"IBM Plex Mono", ui-monospace, monospace` | figures (metric tiles, stat counters), usernames, keys, URLs |

- **Website**: one Google Fonts stylesheet: `Bricolage Grotesque:opsz,wght@12..96,600..800`,
  `Figtree:wght@400;600;700`, `IBM Plex Mono:wght@500;600`, with `display=swap`. Material Symbols
  stays as it is.
- **Extension**: it self-hosts fonts. Its equivalents are `@fontsource-variable/bricolage-grotesque`,
  `@fontsource-variable/figtree` and `@fontsource/ibm-plex-mono`; which ones to install is the
  extension session's call.
- **Tailwind**: gets `fontFamily.display`. Headings use `font-display` through the type-style
  classes (`text-headline-*`, `text-display-*`) in the component layer, so pages don't change markup.
- **Figures**: `.fr-stat` (metric tiles' value) and `stat-counter` use `font-mono`, with tabular
  figures already on `body`.

## R3. Shape and type scale

- **Radii**: `sm` 0.375rem, `default` 0.75rem, `md` 1rem, `lg` 1.25rem, `xl` 1.75rem, `full`
  unchanged. Cards use `lg`, hero panels `xl`.
- **Buttons and pills**: buttons become `rounded-full`; pills already are.
- **Type styles**: names and sizes stay, except headline weights move to 700–800 with tighter
  tracking (`headline-xl` -0.03em, `display-hero` -0.035em) to suit Bricolage. Line heights are
  unchanged, so layouts don't shift.

## R4. Components (`Views/Theme.fs` component layer)

| Component | Change |
|---|---|
| `.fr-card` | `rounded-lg`, no border (`--border-micro` becomes `rgb(241 236 228 / 0.06)` for the few that keep one) |
| `.fr-btn` | `rounded-full`; primary amber on `on-primary`; secondary outlined (`border-ghost`, transparent) |
| `.fr-pill-*` | primary amber 14%, secondary rose 16%, tertiary lilac 14%; text in the colour |
| `.fr-fill` (genre bar) | solid `primary`, no gradient (FR-009); track `surface-container-highest` |
| `.fr-label` | stays an uppercase label-sm: it is a form label, not an eyebrow (FR-010 targets decorative overlines) |
| `.fr-overline` | audit every use; keep only where it labels a value (metric tile), never above a heading |
| focus ring | 2px amber, offset 2px |
| `--glow-primary` | `0 4px 20px -4px rgb(255 181 71 / 0.45)` |
| `--edge-highlight` | `rgb(255 181 71 / 0.35)` |
| Discord / Google buttons | unchanged brand colours, `rounded-full` |

## R5. The mark

`design/brand/frunchy-mark.svg` keeps its geometry. Its fills change as follows:
- arm and frame `#4cd7f6` → `#ffb547`;
- bars `#06b6d4` → `#e0942a`;
- last bar `#c0c1ff` → `#ff6fa8`;
- the clapper's cut lines → `#13111a`.

`Theme.markSvg` and the favicon tile follow, with the tile on `#13111a`.

## R6. Pages

Pages use shared classes and tokens, so most follow automatically. The rest:
- **Restyling pass**: per page, read through `impeccable` for leftovers: hard-coded sizes that
  clash with the new radii, `fr-overline` used as an eyebrow, and gradients.
- **Pages not drawn in the pitch**: DNA, year, anime, account, sign-in, sign-up, claim, review,
  operators and the message pages are covered by the audit.
- **No changes allowed**: no new copy, no new layout sections, and no figure format change
  (FR-006).

## R7. Tests that change (SC-002)

Only the expected design values:
- `pages load the design system`: the font family name and the primary value.
- `pageProblems`: the `--color-primary` marker it looks for.
- The contrast test is unchanged; it runs on the new values.
- A new assertion: no Neo-Scrobble value (`#4cd7f6`, `#0f131c`, `#06b6d4`, `#c0c1ff`) and no
  `Plus Jakarta Sans` or `JetBrains Mono` anywhere in a rendered page (SC-001).
- A new assertion: no `linear-gradient` in the component CSS.

## R8. The extension

- **Contract**: `contracts/design-system.md` in this feature is the new shared contract. It
  supersedes 004's values; the names are the same, plus `--font-display`.
- **Message**: a ready message for the extension session sits in `contracts/extension-handoff.md`.
  No extension session is running (checked 2026-10-15), so the owner sends it or starts that
  session with it.
- **Parity test**: it is skipped where `../browser-extension` isn't beside the repository, the
  worktrees included. In the owner's main checkout it fails until the extension lands, which is
  expected and stated in the quickstart.

## R9. Design reference

`design/night_broadcast/DESIGN.md` records the values above as the source of truth, links the pitch
canvas, and states the rules: amber is self, rose is others, no gradients, no eyebrows.
`design/neo_scrobble_profile/DESIGN.md` gets a one-line "superseded by" note.
