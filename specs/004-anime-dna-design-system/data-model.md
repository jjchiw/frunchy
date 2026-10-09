# Data Model: Anime DNA Profile and Shared Design System

**No schema change.** Every figure is computed at request time from tables that already exist
(features 001 and 003). `SCHEMA.md` gets no new table. It only gets a note that the DNA page
reads these tables.

## Sources

| Table | Used for |
|---|---|
| `users` | `username` → `id`, `display_name` |
| `watch_events` | `fully_watched`, `playhead_s` and `watched_at` per user and episode |
| `cr_episodes` | `duration_ms`, `episode_number`, `season_id`, `series_id`, `series_title`, `thumbnail_url` |
| `cr_series` | `title` (LEFT JOIN: it can be missing, see feature 003 T044) |
| `cr_season_matches` | `status = 'matched'`, `mal_id` and `shared_from` (the original season of a dub) |
| `mal_anime` | `type`, `year`, `aired_from`, `image_url` |
| `mal_anime_tags` + `mal_tags` | `kind` = `genre` (minus explicit ids 9, 12, 49) and `kind` = `studio` |

## Computed entities

### Watched minutes (`dna` CTE)

One row per user and **deduplicated** episode (research R4, R5):

| Field | Rule |
|---|---|
| `key` | `coalesce(shared_from, season_id) ‖ episode_number`, or the episode id when there's no number |
| `ms` | `duration_ms` if fully watched, else `least(playhead_s × 1000, duration_ms)` |
| `fully_watched`, `watched_at` | from the row kept |
| `series_id` | the Crunchyroll series |
| `mal_id` | the matched entry, or NULL when the season isn't matched |

- **Kept row**: the one with the largest `ms`, ties to the episode id.
- **Filter**: episodes with `duration_ms IS NULL` are excluded.

### DNA profile (view model, never stored)

| Figure | Definition | Coverage shown |
|---|---|---|
| Totals | hours and days: **the dashboard's query, not deduplicated** (R5) | none |
| Favourite studio, runner-up | studio minutes ÷ matched minutes; top 2 by minutes, then name | matched ÷ all minutes |
| Dominant decade | decade of `year`, or else of `aired_from`; minutes ÷ minutes with a known year | year-known ÷ all minutes |
| Genre distribution | top 5 genres + Others; genre minutes ÷ the sum of genre minutes; each genre's top series | matched ÷ all minutes |
| Format split | `type` minutes ÷ matched minutes, largest first | matched ÷ all minutes |
| Insignia anime | series with the most minutes; title, poster and fully watched count (R9) | none (no MyAnimeList needed) |
| Viewing pace | fully watched in `[now − 90 d, now]` ÷ 90; steady if ≥ 9 of 13 weeks; < 10 → not enough | none |

### States

| State | When |
|---|---|
| `NotFound` | unknown or deleted username → "User not found", 404 |
| `Empty` | fewer than 15 fully watched episodes with a duration → the page-level "not enough data yet", with the count |
| Per figure `NotEnough` | a MyAnimeList-based figure with 0 matched minutes; pace with < 10 episodes |
| `Ready` | otherwise; each figure is either a value or `NotEnough` |

## Rebuild statement (constitution I)

Nothing new is stored, so nothing needs rebuilding. The DNA is a pure function of:
- the watch events, which `user-rebuild` rebuilds from raw imports;
- the MyAnimeList data, which `mal-rebuild` rebuilds from stored responses.

The tests assert identical figures after `user-rebuild` and after re-sending submissions (FR-016).

## Design tokens (not data, listed for completeness)

These are the CSS custom properties in `Views/Theme.fs`, named like the extension's `tokens.css`.
See `contracts/design-system.md`.
