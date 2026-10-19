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
