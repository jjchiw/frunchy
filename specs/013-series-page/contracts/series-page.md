# Contract: the public series page

## Route

`GET /anime/{seriesId}`, public.
- **Found**: 200.
- **Not found**: 404 with the plain not-found page for a malformed id (no query), an unknown
  series, or a series with no episode of known length.

## Test hooks

| Element | Hook |
|---|---|
| Page | `data-series-page` = series id |
| Title | `data-series-title` |
| Crunchyroll link | `data-cr-series-link` (only with a slug) |
| People, all time | `data-people-count` = integer (absent below the minimum) |
| People, this week | `data-people-week` = integer (absent below the minimum) |
| A season | `data-season` = season id (`other` for seasonless), with `data-audio`, `data-dub`, and `data-known` = known episodes |
| Collapsed season | `data-season-collapsed` (seasons with more than 30 known episodes) |
| MyAnimeList | the anime page's hooks (`data-mal`, `data-mal-link`, `data-mal-compact`, `data-mal-missing`) |
| An episode | `data-episode` = episode id, `data-comments` = visible comment count, href `/anime/{seriesId}/episode/{episodeId}` |
| Links to the page | `data-series-page-link`, href `/anime/{seriesId}`: on chart rows, the episode page, the anime page |

## Changes to 012's contract

- **Chart row title**: it links to the series page (`data-series-page-link`) for everyone.
- **`data-chart-mine`**: moves to a separate "Your history" link, shown only for a signed-in viewer
  who watched the series.

## Usernames

- **New names** (sign-up, `user-create`): `^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$`, not reserved.
- **Lookups**: the old rule, `^[a-z0-9][a-z0-9_-]{2,29}$`.
