# Data Model: Review MyAnimeList Matches on the Web

**No schema change.** The actions write the existing `cr_season_matches` and `mal_searches` rows
through the rules of feature 003. Security events reuse `security_events`, whose `event` column is
free text.

## Sources

| Table | Used for |
|---|---|
| `cr_season_matches` | status, method, reason, attempts, candidates (jsonb), mal_id, shared_from, decided_at, requested_at |
| `cr_seasons`, `cr_series` (LEFT JOIN), `cr_episodes` | titles, series id, the season's episodes |
| `watch_events`, `users` | users affected, fully watched episodes, the latest watcher's username |
| `mal_anime` | the title of a manual match |
| `mal_searches` | dropped for the series on retry and on back to automatic |
| `security_events` | one row per successful action, on the operator's account |

## Computed entities

### Review row

| Field | Rule |
|---|---|
| season id, series id | from the match and its season |
| series title, season title | `coalesce(cr_series.title, min(cr_episodes.series_title), series_id)`; the season title or "" |
| status, reason, attempts | as stored |
| candidates | the stored JSON: `malId`, `title`, `type`, `year` |
| users | the count of distinct users with a watch of an episode of the season with a known duration |
| fully watched | the count of those watches that are fully watched |
| watcher | the username of the most recent watch of the season, if any (the anime page link) |

**Order**: requested first, then the most recent watch (NULLs last), then the season id. 50 rows
are shown, and the total is counted.

### Manual decision

The season, its series and season titles, `mal_id` with the anime's title (or "not on
MyAnimeList"), and `decided_at`. Newest first.

## State transitions (one season)

| Action | Allowed from (`expect`) | To |
|---|---|---|
| set (web) | Open: ambiguous, no_match, failed; not manual | matched, method manual; its dubs follow |
| none (web) | Open | no_match, method manual, reason `manual-none`; its dubs follow |
| retry (web) | Open | its series' automatic rows go to pending (requested); manual rows are kept and their anime is refreshed |
| back to automatic (web) | Manual | the season and its dubs go to pending (requested); the series' searches are dropped |
| CLI (`mal-set`, `mal-none`, `mal-retry`, `mal-auto`) | Any | as above |

Any other state returns `NotOpen` and writes nothing.

## Rebuild statement (constitution I)

Manual rows are operator input, never derived, and survive `mal-rebuild` as in feature 003.
Everything else these actions touch is re-derived by the enrichment worker.
