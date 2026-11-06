# Contract: data and hooks (feature 031)

## Rules (per person, at request time)

- Rows: `watch_events` of the person joined to `cr_episodes` (known length) and `cr_season_matches` (`status =
  'matched'`), deduplicated like the DNA page (key `coalesce(shared_from, season_id) || '#' || episode_number`; a row
  without a number is its own key). An entry is `mal_id`.
- Per entry: `watched` = fully watched dedup keys; `total` = `mal_anime.episodes` when > 0, else none; `airing` =
  `mal_anime.status = 'currently_airing'`; `last_watch` = max `watched_at` over all its rows; `newest_aired` = the air
  date (`cr_episodes.aired_at`) of its newest fully watched episode by air date; `long_runner` = feature 027's rule
  for its series.
- Started: `watched >= 1`. Completed: `total` known and `watched >= total`. Watching: not completed and
  (`now - last_watch <= 30 days` or (`airing` and `now - newest_aired <= 21 days`)). Unfinished: the rest.
  Constants in one place (`Infrastructure/Progress.fs`): `WatchingDays = 30`, `AiringKeepUpDays = 21`,
  `CompletedListed = 12`.
- Next episode (Watching and Unfinished): in the Crunchyroll season of the entry's newest watch, the lowest
  `episode_number` greater than the highest fully watched number in that season; its Frunchy episode page when
  `cr_episodes` has that number in that season (with known length), else the Crunchyroll series URL
  (`https://www.crunchyroll.com/series/{seriesId}/{slug}`, as Series.fs builds it). None when the entry has no
  numbered episodes.
- Unmatched note: started Crunchyroll seasons (a fully watched episode) with no matched entry, counted.
- `now` comes from the `TimeProvider`.

## Hooks (`/u/{username}/progress`)

| Hook | On | Value |
|---|---|---|
| `data-progress` | the page's main section | `owner` or `visitor` |
| `data-progress-completed`, `data-progress-watching`, `data-progress-unfinished` | the three counts | the number |
| `data-progress-unmatched` | the unmatched note (absent at 0) | the number |
| `data-progress-empty` | the empty state | `""` |
| `data-progress-list` | each list (owner only) | `watching`, `unfinished` or `completed` |
| `data-progress-entry` | each listed entry | the MyAnimeList id |
| `data-entry-watched` | in it | episodes fully watched |
| `data-entry-total` | in it, when known | MyAnimeList episodes |
| `data-entry-last` | in it | `<time datetime=…>` of the newest watch (also `data-fr-time`) |
| `data-entry-long-runner` | in it, when a long runner | `""` |
| `data-next-episode` | the next-episode link, when there is one | the episode number; `href` is the episode page or Crunchyroll |
| `data-series-page-link` | the title | (existing hook) `href` `/anime/{seriesId}` |
| `data-progress-year`, `data-progress-year-shows` | each year group of Unfinished (only with more than 10 unfinished shows), a `<details>` | the year of the last watch (UTC), how many shows it holds (added with the design direction) |
| `data-progress-link` | the tab, on every profile page | `""` (with `data-tab="progress"`) |

A visitor's page must contain none of `data-progress-list`, `data-progress-entry`, `data-next-episode` and no title of
a started entry.
