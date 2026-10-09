# Data Model: Year in Review

**No schema change.** Every figure is computed at request time from tables that already exist
(features 001, 003 and 004). `SCHEMA.md` gets no new table. It only gets a note that the year page
reads these tables.

## Sources

| Table | Used for |
|---|---|
| `users` | `username` → `id` |
| `watch_events` | `fully_watched`, `playhead_s` and `watched_at` per user and episode |
| `cr_episodes` | `duration_ms`, `episode_number`, `season_id`, `series_id`, `series_title`, `thumbnail_url` |
| `cr_series` | `title` (LEFT JOIN: it can be missing, see feature 003 T044) |
| `cr_season_matches` | `status = 'matched'`, `mal_id` and `shared_from` (the original season of a dub) |
| `mal_anime` | `type`, `image_url` |
| `mal_anime_tags` + `mal_tags` | `kind` = `genre` and `kind` = `studio` |

## Computed entities

### Watch year

The UTC calendar year of an episode's latest watch: `extract(year from watched_at AT TIME ZONE 'UTC')`
(research R1). Days and months use the same conversion, so the session time zone never shifts them.
The year's watches are the `watch_events` rows whose watch year is the requested year.

### Year minutes (`yr` CTE)

The DNA page's deduplicated rows (feature 004, `dna` CTE), restricted to the year's watches:

| Field | Rule |
|---|---|
| `key` | `coalesce(shared_from, season_id) ‖ episode_number`, or the episode id when there's no number |
| `ms` | `duration_ms` if fully watched, else `least(playhead_s × 1000, duration_ms)` |
| `fully_watched`, `watched_at` | from the row kept (the one with the largest `ms`, ties to the episode id) |
| `series_id`, `mal_id` | the Crunchyroll series, and the matched entry or NULL |

Episodes with `duration_ms IS NULL` are excluded.

### Year review (view model, never stored)

| Figure | Definition | Coverage shown |
|---|---|---|
| Totals | hours, days, fully watched episodes and series: **the dashboard's query, not deduplicated**, over the year's watches (R2); hours rounded to 0.1 | none |
| Most-watched series | series with the most year minutes; title, poster (its most-watched matched season's image, else a thumbnail) and fully watched count that year (R3) | none |
| Months | 12 buckets of year minutes; busiest month is the largest, a tie goes to the earlier month | none |
| Top genres | `kind = 'genre'`; top 5 plus Others; genre minutes ÷ the sum of genre minutes, shares add up to 100% | matched ÷ all year minutes |
| Top studios | `kind = 'studio'`; top 3 by minutes, then name; minutes ÷ matched minutes | matched ÷ all year minutes |
| Format split | `type` minutes ÷ matched minutes, largest first | matched ÷ all year minutes |
| New and continued | a series with a fully watched episode in the year is **new** when the earliest `watched_at` across all its held episodes falls in the year, else **continued** (R4) | none |
| Longest streak | longest run of consecutive UTC days with a fully watched episode, grouped with `d - row_number()`; ties go to the earlier run; first and last day (R5) | none |

### Year list

The distinct watch years of the user's `watch_events`, newest first. It feeds the picker, the
redirect of `/u/{username}/year` (its first entry) and the redirect of a year with no history.

### States

| State | When |
|---|---|
| `NotFound` | unknown or deleted username ("User not found"), or a year that isn't a number, is before 2000 or is after the current year (R7); status 404 |
| `Redirect` | a year with no history other than the current year goes to `/u/{username}/year`; the address without a year goes to the newest year with history, or the current year when there is none |
| `Empty` | fewer than 15 fully watched episodes in the year: the page-level "not enough data yet", with the count; the picker still shows |
| Per figure `NotEnough` | a MyAnimeList-based figure with 0 matched minutes |
| `Ready` | otherwise; each figure is either a value or `NotEnough` |

The current year comes from `TimeProvider`, so the tests can fix it.

## Rebuild statement (constitution I)

Nothing new is stored, so nothing needs rebuilding. A year review is a pure function of:
- the watch events, which `user-rebuild` rebuilds from raw imports;
- the MyAnimeList data, which `mal-rebuild` rebuilds from stored responses.

The tests assert identical figures after `user-rebuild` and after re-sending submissions (FR-014).
