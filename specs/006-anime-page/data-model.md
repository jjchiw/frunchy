# Data Model: Anime Page

**No schema change.** Every figure is computed at request time from tables that already exist
(features 001 and 003). `SCHEMA.md` gets no new table. It only gets a note that the anime page reads
these tables.

## Sources

| Table | Used for |
|---|---|
| `users` | `username` → `id` |
| `watch_events` | `fully_watched`, `playhead_s` and `watched_at` per user and episode |
| `cr_episodes` | `duration_ms`, `episode_number`, `title`, `season_id`, `series_id`, `series_title`, `thumbnail_url`, plus the known-episode counts per season |
| `cr_series` | `title`, `slug` (LEFT JOIN: it can be missing, see feature 003 T044) |
| `cr_seasons` | `title`, `season_number`, `audio_locale` |
| `cr_season_matches` | `status = 'matched'`, `mal_id` and `method` (`dub` marks a dub season) |
| `mal_anime` | `title`, `url`, `type`, `season`, `year`, `score`, `episodes`, `status`, `image_url` |
| `mal_anime_tags` + `mal_tags` | `kind` = `genre` (up to 3, by position) and `kind` = `studio` (the first) |

## Computed entities

### Anime page (view model, never stored)

One user's view of one Crunchyroll series, in four parts.

| Part | Rule |
|---|---|
| Header | title is `coalesce(cr_series.title, min(episode series_title), series_id)`; poster is the `image_url` of the user's most-watched matched season (by minutes), else the newest watched episode's thumbnail, else none; the Crunchyroll link exists only when a slug is stored |
| Totals | fully watched episodes and hours (rounded to 0.1) follow the dashboard's rule, not deduplicated, restricted to the series; first and latest watch are `min` and `max` of `watched_at` over the watched episodes |
| Seasons | only seasons with at least one watched episode, ordered by `season_number` (NULLS LAST), then season id; episodes without a season form one last group, "other" |
| Episodes | the user's watched episodes of a season, ordered by `episode_number` (NULLS LAST), then title, then id |

Episodes with `duration_ms IS NULL` are excluded everywhere, so a series whose watches are all
unresolved has no page.

### Season row

| Field | Rule |
|---|---|
| `watched` | the user's fully watched episodes of the season |
| `known` | `cr_episodes` rows of the season with a known duration, from any user's history; it can grow as more history arrives |
| `audio` | the stored locale code, shown as a language name in the viewer's language; none on "other" |
| `dub` | the season's match has `method = 'dub'`; an unmatched dub shows only its language |
| MyAnimeList | the matched entry's details (research R5), or none, which shows "not on MyAnimeList yet" |

### Watched episode

| Field | Rule |
|---|---|
| `fully` | `fully_watched` of the user's only stored watch of the episode |
| `percent` | `100` if fully watched, else `least(playhead_s × 1000, duration_ms) ÷ duration_ms` as a whole number from 0 to 100 |
| `watched_at` | the latest watch held, rendered as a time element |

## States

| State | When |
|---|---|
| `NotFound` (user) | unknown or deleted username; "User not found", status 404 |
| `NotFound` (page) | a series id outside `^[A-Z0-9]{6,20}$` (the database is not touched), or a series with no watched episode of known length for the user; "Page not found", status 404 |
| `Ready` | otherwise; a series with no fully watched episode still has a page, with 0 fully watched and its partial episodes listed |

## Rebuild statement (constitution I)

Nothing new is stored, so nothing needs rebuilding. An anime page is a pure function of:
- the watch events, which `user-rebuild` rebuilds from raw imports;
- the MyAnimeList data, which `mal-rebuild` rebuilds from stored responses.

The tests assert identical figures after `user-rebuild` and after re-sending submissions (FR-012).
