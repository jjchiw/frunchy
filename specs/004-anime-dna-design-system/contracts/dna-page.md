# Contract: the DNA page (`GET /u/{username}/dna`)

A public, server-rendered HTML page. The username is matched case-insensitively, as on the
dashboard. The language comes from `Accept-Language` (English default, Spanish).

## Responses

| Case | Status | Body |
|---|---|---|
| Known user | 200 | the DNA page (any state below) |
| Unknown or deleted user | 404 | the site's "User not found" message page |

There's no JSON variant, and nothing is added to `/api/*` (FR-027).

## Sections, in order, and their test hooks

Tests read only `data-*` attributes and decoded text (`readHtml`), never classes. Values in
`data-value` are invariant: dot decimals, no grouping, percentages as numbers with 1 decimal.

| Section | Root hook | Inner hooks |
|---|---|---|
| Header | `data-dna` | `data-dna-user` (display name), a link to `/u/{username}` (`data-dashboard-link`) |
| Copy link | `data-copy-link` | `data-copy-done` (confirmation, `aria-live="polite"`) |
| Totals | `data-dna-totals` | `data-stat="hours"` / `"days"` with `data-value`: equal to the dashboard's |
| Favourite studio | `data-dna-studio` | `data-rank="1"` / `"2"` with `data-name`, `data-share` |
| Dominant decade | `data-dna-decade` | `data-name` (e.g. `2020`), `data-share` |
| Viewing pace | `data-dna-pace` | `data-value` (episodes per day), `data-label` = `steady` / `bursts`, `data-episodes`, `data-weeks` |
| Insignia anime | `data-dna-insignia` | `data-series` (Crunchyroll series id), `data-episodes`, an `img` with alt = the title |
| Genre distribution | `data-dna-genres` | 6 rows `data-genre` (name, or `others`) with `data-share`, `data-top-series` (none on `others`) |
| Format split | `data-dna-formats` | rows `data-format` (MyAnimeList type name) with `data-share` |

**Coverage**: every MyAnimeList-based section (studio, decade, genres, formats) has a
`data-coverage` attribute with its coverage percentage, and shows it as text.

**Not enough data**:
- A section that has no data carries `data-not-enough` and shows its own message instead of values.
- The whole page in the empty state (fewer than 15 fully watched episodes, FR-014) carries
  `data-dna-empty` with `data-value` = the user's count, and shows only the header, the count out of
  15, the explanation and the dashboard link.

## Links

- The dashboard gets a link to `/u/{username}/dna` with `data-dna-link`.
- The DNA page links back to the dashboard (`data-dashboard-link`).

## Behaviour guarantees

- **No external calls**: the page reads only stored data, so it never calls MyAnimeList.
- **Stable figures**: they're identical after `user-rebuild` and after re-sending submissions.
- **Stable order**: ties are ordered by name (studios, decades, genres, formats) or by title
  (insignia).
- **Explicit genres** (MyAnimeList ids 9, 12, 49) never appear.
- **Text**: every visible string comes from `Views/Texts.fs`. Genre, studio and format names are
  MyAnimeList's and aren't translated.
