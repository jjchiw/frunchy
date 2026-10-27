# Contract: data and hooks (feature 027)

## Data

- `cr_episodes.aired_at timestamptz NULL`: from `panel.episode_metadata.episode_air_date` (an ISO date-time;
  unparseable or absent → NULL). The import's episode upsert sets it with the other details, under the existing
  `source_at` rule.
- Upgrade (in `Database.upgrades`, guarded by the column's absence in `information_schema.columns`): add the column,
  then one `UPDATE cr_episodes … FROM (SELECT DISTINCT ON (entry id) …)` over `cr_import_pages.body -> 'data'`
  (json_array_elements), taking `entry ->> 'id'` and `entry -> 'panel' -> 'episode_metadata' ->> 'episode_air_date'`
  of the newest `captured_at` (then the newest import, then the highest page index); only rows that parse as a
  timestamp. Literal SQL. `SCHEMA.md` documents the column. A `SchemaUpgradeTests` test builds the previous shape
  with a stored page, runs `initializeSchema` twice and checks the date and the no-op.
- Airing season: `Seasons.ofInstant` (or the charts' existing helper, moved to a shared place): (name, year), UTC quarter.

## Definitions (all per person, at request time, on the DNA dedup rules)

- Dedup group air date = min(aired_at) in the group.
- Watched season = distinct (mal_id, airing season or NULL) over fully watched dedup rows with a mal_id.
- Long runner (per Crunchyroll series) = any matched season's `mal_anime.episodes > 100`, or any `cr_episodes` of
  the series with `episode_number > 100`. The threshold is one named constant (100), the long-run minimum another (24).
- While airing = `watched_at - aired_at` between 0 and 7 days, on a fully watched row.

## Hooks (DNA page, `/u/{username}/dna`)

Existing hooks keep their names. Changed meaning: `data-share` on studio/genre/decade/format rows is now the share
of watched seasons (invariant formatting as today). New:

| Hook | On | Value |
|---|---|---|
| `data-studio-seasons`, `data-decade-seasons`, `data-genre-seasons`, `data-format-seasons` | each share row | the row's watched seasons (integer) |
| `data-coverage-seasons` | each taste card's coverage note | "matched/total" watched seasons, e.g. `41/45` |
| `data-dna-insignia` (existing section) | flagship | absent when every series is a long runner |
| `data-insignia-series` | flagship | its Crunchyroll series id |
| `data-dna-long-run` | the long-run card | its Crunchyroll series id |
| `data-long-run-episodes` | inside it | episodes fully watched |
| `data-long-run-span` | inside it, when known | `fall-1999/fall-2026` (first and last airing season) |
| `data-dna-seasons` | the "Your seasons" section | the number of airing seasons |
| `data-top-season` | the top season element | `fall-2026` |
| `data-season` | each listed season | `fall-2026` |
| `data-season-shows` | each listed season | its number of shows |
| `data-season-show` | each show inside a season | Crunchyroll series id |
| `data-show-episodes` | each show | episodes fully watched from that season |
| `data-while-airing` | each show watched while airing | `""` (absent otherwise) |
| `data-earlier-seasons` | the "earlier seasons" line | how many aren't listed |

Year page (`/u/{username}/year/{year}`): its existing studio/genre/decade/format hooks; `data-share` changes meaning
the same way, plus the same `data-*-seasons` row hooks and `data-coverage-seasons`.
