# Contract: taste compatibility

## Routes

| Verb | Route | Who | Result |
|---|---|---|---|
| GET | `/u/{username}` | anyone | the dashboard; it carries the card only for a signed-in viewer who isn't the owner |
| GET | `/u/{username}/compare` | signed in | the compare page (200) |

The compare route's other outcomes:
- **Signed out**: 302 to `/signin?returnUrl=%2Fu%2F{username}%2Fcompare`.
- **The viewer's own username**: 302 to `/u/{username}`.
- **An unknown, deleted or invalid username**: 404, the "user not found" page.

## Test hooks

| Element | Hook |
|---|---|
| Card | `data-compat-card` = owner username, `data-level` = 1–5 or `""` when not enough |
| Page | `data-compare` = owner username, `data-level` = 1–5 or `""` |
| Meter | `data-level-meter` = 1–5, `role="img"` with `aria-label` |
| Level words | `data-level-name` |
| Not enough history | `data-compat-not-enough` = `viewer`, `owner` or `both` |
| Shared series count | `data-shared-count` = integer |
| A shared series | `data-shared-series` = series id, with `data-viewer-episodes` and `data-owner-episodes` (page) |
| More shared series | `data-shared-more` = integer |
| A shared genre (card) | `data-shared-genre` = genre name |
| A genre row (page) | `data-genre-compare` = genre name, `data-viewer-share` and `data-owner-share` (one decimal, invariant) |
| Coverage | `data-viewer-coverage`, `data-owner-coverage` (one decimal, invariant) |
| Their series | `data-their-series` = series id, with `data-mal-score` when matched |
| A shared studio | `data-shared-studio` = studio name, `data-studio-series` = integer |
| Link to the page | `data-compare-link`, href `/u/{username}/compare` |
| No series in common | `data-shared-none` |
| Summary sentence | `data-compare-summary` |

## Levels

| Score | Level | Name (en / es) |
|---|---|---|
| < 0.2 | 1 | Low / Baja |
| < 0.4 | 2 | Some / Algo |
| < 0.6 | 3 | Good / Buena |
| < 0.8 | 4 | High / Alta |
| ≥ 0.8 | 5 | Very high / Muy alta |

The level is shown as "{name} compatibility" ("Compatibilidad {name}"). No percentage or raw score
appears anywhere, not even in an attribute.
