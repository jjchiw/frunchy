# Night Broadcast

Design system of Frunchy since feature 010 (`specs/010-night-broadcast/`). It supersedes
[Neo-Scrobble Profile](../neo_scrobble_profile/DESIGN.md). Source of truth for the values is
`Views/Theme.fs`, shared with the browser extension through the contract
[`specs/010-night-broadcast/contracts/design-system.md`](../../specs/010-night-broadcast/contracts/design-system.md).
Pitch (row 2, the chosen direction): `design/pitches/visual-directions/`, the `Broadcast*` boards. The
original canvas is gone; `design/pitches/README.md` says how to view the boards.

A dark, warm night palette: paper-coloured text on a violet-black page, amber for the person the
screen is about, rose for other people.

## Colours

| Token | Value | Role |
|---|---|---|
| `surface` | `#13111a` | the page |
| `surface-container-lowest` | `#0d0b12` | wells, inputs on cards |
| `surface-container-low` | `#1a1722` | cards |
| `surface-container` | `#1e1a27` | inputs, raised rows |
| `surface-container-high` | `#2a2435` | secondary button hover, thumbnail placeholders |
| `surface-container-highest` | `#352e42` | tracks, hover |
| `on-surface` | `#f1ece4` | text (warm paper) |
| `on-surface-variant` | `#d2cabe` | secondary text |
| `outline` | `#9b9286` | placeholders, input borders (never text on `highest`) |
| `outline-variant` | `#4a4255` | dividers |
| `primary` | `#ffb547` | amber: primary action, the page's own figures |
| `on-primary` | `#2a1700` | text on amber |
| `primary-container` | `#e0942a` | darker amber (mark, pressed) |
| `primary-fixed` | `#ffd08a` | primary hover |
| `secondary` | `#ff6fa8` | rose: other people, spoilers, warnings |
| `secondary-container` | `#5c2342` | rose wells |
| `tertiary` | `#c9bcff` | lilac: tags, information |
| `error` | `#ff9b85` | coral: errors, distinct from rose and amber |
| `on-error` | `#4a0b00` | text on coral |
| `error-container` | `#8c1d0e` | |
| `on-error-container` | `#ffdad3` | |

## Fonts

| Token | Value | Use |
|---|---|---|
| `--font-family` | Figtree | text |
| `--font-display` | Bricolage Grotesque | h1 to h3, the wordmark |
| `--font-mono` | IBM Plex Mono | figures (metric tiles, stat counters), usernames, keys, URLs |

## Shape

Radii: `sm` 0.375rem, `default` 0.75rem, `md` 1rem, `lg` 1.25rem (cards), `xl` 1.75rem (hero panels),
`full`. Buttons and pills are fully rounded. Cards have no border.

## Rules

- Amber is the person the screen is about, and the primary action. Rose is everyone else, plus
  spoilers and warnings. Lilac is tags and information. Errors are coral, never rose.
- Solid fills only: no gradients, the genre bar included.
- No eyebrow labels or decorative overlines above headings. A label that names a value (metric tile,
  form label) is fine.
- Buttons and pills fully rounded; secondary buttons are outlined.
- WCAG 2.1 AA on every text and background pair; a visible 2px amber focus ring.
- Tests read `data-*` hooks only, never classes.

## Light theme

Feature 032 (`specs/032-light-theme/`, direction in `specs/032-light-theme/design-direction.md`): Night Broadcast by
day, warm paper with the same violet-black ink, and amber split in two (deep for text, bright for fills). The dark
palette above is unchanged (the browser extension shares it). The light palette is Frunchy's own: `Theme.lightColors`
(a value for every name in `Theme.colors`), `Theme.lightEffects` and the Frunchy-only lists below, all in
`Views/Theme.fs`.

- **Mechanism, no script.** The server renders `<html data-theme="light|dark|system">` from the `frunchy.theme`
  cookie (`system` when missing or invalid). The dark tokens sit on `:root`; the light ones on
  `:root[data-theme="light"]` and, inside `@media (prefers-color-scheme: light)`, on `:root[data-theme="system"]`.
  `color-scheme`, `<meta name="color-scheme">` and `<meta name="theme-color">` (the surface of each palette; two
  metas with `media` for `system`) follow. The footer switch is three words in a pill,
  `GET /theme/{light|dark|system}?returnUrl=…`.
- **Tailwind.** Every colour utility resolves to its variable,
  `color-mix(in srgb, var(--color-X) calc(<alpha-value> * 100%), transparent)`, so `bg-primary/10` and every other
  opacity modifier follow the theme.
- **Rule: never a hex colour in a view.** Use a colour role (a Tailwind colour or `var(--color-…)`), or
  `color-mix(…)` of one. The only literals left are third-party marks (Google's "G") and the favicon, a data URI
  that cannot see the page's variables and keeps the dark tile.
- WCAG AA is tested for both palettes with the same pairs (`DesignSystemTests`): the role text colours on the five
  surfaces, outline, on-primary on primary-fill and on primary-fixed, on-error, on-error-container, and secondary on
  secondary-container.

### Fill tokens and the fill rule

Bright amber is not text on paper (1.67:1) and deep amber as every bar turns the site brown, so amber is two values.
Three Frunchy-only tokens, outside `Theme.colors` and the extension's tokens (`Theme.fillColors` for dark, derived
from `colors` so they cannot drift, `Theme.lightFillColors` for light; Tailwind `bg-primary-fill`,
`bg-primary-fill/[0.14]`…):

| token | dark (= its role) | light |
|---|---|---|
| primary-fill | #ffb547 | #f5a623 |
| secondary-fill | #ff6fa8 | #e85a94 |
| tertiary-fill | #c9bcff | #9d86ee |

**The rule:** a solid area or data mark (`bg-primary`, `.fr-fill`, `toneBar`, `levelMeter`, chart, months and DNA bars,
the Progress counts bar, the compare legend dots, `.fr-btn-primary`'s background, checkbox and radio accents, the
mark) uses the fill; a tint (pills, `fr-alert-*` backgrounds and borders, the spoiler box, selection) uses the fill at
the same alpha as before; text, links, state borders (`border-primary`, the selected tab rule), the focus ring, the
caret and icons keep the role token. `text-primary-container` and `text-primary-fixed` are never used (they fail on
light).

### Frunchy-only effects

`--poster-edge` (the hairline on posters: dark `inset 0 0 0 1px rgb(0 0 0 / 0.4)`, light
`inset 0 0 0 1px rgb(31 26 36 / 0.12)`) and `--mark-frame` (the mark's clapper and frame: dark
`var(--color-primary-fill)`, light `var(--color-primary-container)`), in both themes and not in the shared `effects`.
The light shadows are warm brown, never black.

### The light table

| token | dark | light |
|---|---|---|
| surface | #13111a | #efe8dd |
| surface-container-lowest | #0d0b12 | #e8e0d3 |
| surface-container-low | #1a1722 | #fdfaf5 |
| surface-container | #1e1a27 | #f6f1e9 |
| surface-container-high | #2a2435 | #e7dfd2 |
| surface-container-highest | #352e42 | #ddd3c4 |
| on-surface | #f1ece4 | #1f1a24 |
| on-surface-variant | #d2cabe | #4c4554 |
| outline | #9b9286 | #6b6155 |
| outline-variant | #4a4255 | #c8bba8 |
| primary | #ffb547 | #854700 |
| on-primary | #2a1700 | #2a1700 |
| primary-container | #e0942a | #d98a0b |
| primary-fixed | #ffd08a | #e8920f |
| secondary | #ff6fa8 | #9c1655 |
| secondary-container | #5c2342 | #f9dfe9 |
| tertiary | #c9bcff | #5636b8 |
| error | #ff9b85 | #a1230f |
| on-error | #4a0b00 | #ffffff |
| error-container | #8c1d0e | #fbe0d8 |
| on-error-container | #ffdad3 | #5c1007 |
