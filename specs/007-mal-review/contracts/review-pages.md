# Contract: the MyAnimeList review pages

## Access

Every route below follows the same rules:
- **Operators**: a signed-in user whose username is in `Frunchy:Operators` gets the page or the
  action.
- **Everyone else**: signed out, signed in but not an operator, or nobody configured, they get
  **404** with the site's "Page not found" page. That holds for GET and POST alike, before any
  forgery check, and nothing changes.

## Routes

| Method | Route | Form fields | Result |
|---|---|---|---|
| GET | `/review/mal` | query `status` = `ambiguous` / `no_match` / `failed` (absent = all three) | the review list |
| GET | `/review/mal/manual` | none | the manual decisions |
| POST | `/review/mal/set` | `season`, `mal` (an id or a `myanimelist.net/anime/{id}` link) | 302 to `/review/mal?msg=<key>&season=<id>` |
| POST | `/review/mal/none` | `season` | 302 to `/review/mal?msg=<key>&season=<id>` |
| POST | `/review/mal/retry` | `season` | 302 to `/review/mal?msg=<key>&season=<id>` |
| POST | `/review/mal/auto` | `season` | 302 to `/review/mal/manual?msg=<key>&season=<id>` |

Every POST carries the antiforgery token and is limited by the `review-write` policy (30 per
minute per IP; 429 when exceeded).

**Notice keys** (`msg`): `set`, `none`, `retry`, `auto` (success), and `unknown-season`,
`unknown-anime`, `unavailable`, `not-open`, `bad-input` (errors, nothing changed).

## Test hooks

| Element | Hook |
|---|---|
| Page root | `data-review` = `open` or `manual` |
| Status filter links | `data-review-filter` = the status (`all` for the default) |
| Notice | `data-review-notice` = the key |
| A row | `data-review-season` = season id, with `data-status`, `data-users`, `data-fully` |
| Series link (when a watcher exists) | `data-series-link` = series id, href `/u/{watcher}/anime/{seriesId}` |
| A candidate | `data-candidate` = MyAnimeList id, with its own "Use this" form posting `mal` = the id |
| Per-row forms | `data-action` = `set`, `none`, `retry`, `auto` |
| "N more" note | `data-review-more` = the number not shown |
| Empty state | `data-review-empty` |
| Manual row | `data-review-season` = season id, with `data-mal` = id or `none` |
| Account page link (operators only) | `data-review-link`, href `/review/mal` |

## CLI parity

- **Same code**: `mal-set`, `mal-none` and `mal-retry` call the same `Infrastructure/MalActions`
  functions as the web, with `expect = Any`. Their output and exit codes are unchanged.
- **New command**: `mal-auto <crSeasonId>` sends a manual decision back to automatic. Exit 0 on
  success, 2 for an unknown season, 3 when the season isn't manual.
