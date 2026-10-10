# Contract: the year page (`GET /u/{username}/year/{year}`)

A public, server-rendered HTML page with the same visibility as the dashboard. The username is
matched case-insensitively, as on the dashboard. The language comes from `Accept-Language`
(English default, Spanish). A year is a UTC calendar year, and an episode belongs to the year of
its latest watch (FR-004).

Routes (verbatim from the spec):
- `/u/{username}/year/{year}`
- `/u/{username}/year`

## Responses

| Case | Status | Body |
|---|---|---|
| `/u/{username}/year/{year}`, known user, year with history | 200 | the year page (full or "not enough data" state) |
| `/u/{username}/year/{year}`, known user, the current year without history | 200 | the year page in the "not enough data" state, count 0 |
| `/u/{username}/year/{year}`, known user, any other year without history (2000 up to the current year) | 302 | `Location: /u/{username}/year` |
| `/u/{username}/year`, known user with history | 302 | `Location: /u/{username}/year/{year}`, the most recent year with history |
| `/u/{username}/year`, known user without history | 302 | `Location: /u/{username}/year/{year}`, the current year |
| `{year}` is not a number, is before 2000, or is after the current year | 404 | the site's not-found message page |
| Unknown or deleted user (either route) | 404 | the site's "User not found" message page |

Notes:
- The current year comes from the registered `TimeProvider`, so tests fix it.
- The year segment is read as text and parsed in the handler. Only digits make a year.
- There's no JSON variant, and nothing is added to `/api/*`.
- Redirects never loop: `/u/{username}/year` always redirects to a year that answers 200.

## Sections, in order, and their test hooks

Tests read only `data-*` attributes and decoded text (`readHtml`), never classes. Values in
`data-value` and the other numeric attributes are invariant: dot decimals, no grouping,
percentages as numbers with 1 decimal, hours and days with 1 decimal, minutes as whole numbers.

| Section | Root hook | Inner hooks |
|---|---|---|
| Header | `data-year` with `data-value` = the year | `data-year-user` (on the h1; value = display name), a link to `/u/{username}` (`data-dashboard-link`), a link to `/u/{username}/dna` (`data-dna-link`) |
| Copy link | `data-copy-link` | `data-copy-done` (confirmation, `aria-live="polite"`) |
| Year picker | `data-year-picker` | one `data-year-option` per year with history, value = the year (`data-year-option="2026"`), each a link to `/u/{username}/year/{year}`, newest first; the shown year carries `aria-current="page"` |
| Rewatch note | `data-rewatch-note` | plain text saying a rewatch counts in the year of its latest watch |
| Totals | `data-year-totals` | `data-stat="hours"` / `"days"` / `"episodes"` / `"series"`, each with `data-value`; hours, days, episodes and series follow the dashboard's rules restricted to the year |
| Top series | `data-year-top-series` | `data-series` (Crunchyroll series id), `data-episodes` (its fully watched episodes that year), an `img` with alt = the title |
| Months | `data-year-months` with `data-busiest` = the busiest month number (1 to 12; ties go to the earlier month) | 12 elements `data-month` with value 1 to 12, in order, each with `data-minutes`; in the current year, months not yet reached also carry `data-future` and show as an outline |
| New versus continued | `data-year-series-split` | `data-new`, `data-continued` (series counts; they add up to the totals' series count) |
| Longest streak | `data-year-streak` | `data-value` (days), `data-from`, `data-to` (ISO `yyyy-MM-dd`, UTC) |
| Top genres | `data-year-genres` | 6 rows `data-genre` (name, or `others`) with `data-share`, `data-top-series` (none on `others`) |
| Top studios | `data-year-studios` | up to 3 rows `data-rank` = `1` / `2` / `3` with `data-name`, `data-share` |
| Format split | `data-year-formats` | rows `data-format` (MyAnimeList type name) with `data-share` |

**Totals**: the `hours` of every year with history add up to the dashboard's `hours`, within
0.1 hour per year (SC-002). Rewatches count only in the year of their latest watch.

**Months**: all 12 `data-month` elements are always present, with `data-minutes="0"` for a month
without watches. When the year has figures, `data-busiest` is always set.

**Streak**: the most consecutive UTC calendar days in the year with at least one fully watched
episode. A tie goes to the earlier run. The first and last day text is formatted on the server in the
viewer's culture, so the attributes `data-from` and `data-to` are the reliable values.

**Coverage**: the three MyAnimeList-based sections (genres, studios, formats) each carry a
`data-coverage` attribute with the coverage percentage of the year's watch time, as a number with
1 decimal, and show it as text. The three values are the same figure: matched minutes divided by
deduplicated minutes of the year. A dub and its original are counted once in them, and only matched
seasons count. Explicit genres (MyAnimeList ids 9, 12, 49) never appear.

**Shares**: genre shares (the 5 genres plus `others`) add up to 100.0. Studio and format shares are
shares of the year's matched minutes.

**Not enough data**:
- A MyAnimeList section that has no data for the year (no matched genre, studio or format)
  carries `data-not-enough` and shows its own message instead of values. The other sections stay.
- The whole page in the empty state (fewer than 15 fully watched episodes, FR-011) carries
  `data-year-empty` with `data-value` = the year's fully watched count, and shows only the header,
  the year picker, the rewatch note, the count out of 15, the explanation and the links to the
  dashboard and the DNA page. It has none of the figure sections above (no `data-year-totals`,
  `data-year-top-series`, `data-year-months`, `data-year-series-split`, `data-year-streak`,
  `data-year-genres`, `data-year-studios`, `data-year-formats`). The picker still lists every year
  with history and still works.
- A user with no history at all gets this state for the current year, with a picker without
  entries.
- The threshold is 15 fully watched episodes in the year: 15 shows the figures, 14 shows the
  empty state.

## Links added to other pages

- The dashboard (`/u/{username}`) gets a link to `/u/{username}/year` with `data-year-link`.
- The DNA page (`/u/{username}/dna`) gets a link to `/u/{username}/year` with `data-year-link`.
- The year page links back to the dashboard (`data-dashboard-link`) and to the DNA page
  (`data-dna-link`).
- The extension does not link to the page in this feature.

## Behaviour guarantees

- **No external calls**: the page reads only stored data, so it never calls MyAnimeList. It adds no
  table and no stored data.
- **Stable figures**: they're identical after `user-rebuild` and after re-sending submissions.
- **Stable order**: ties are ordered by name (studios, genres, formats), by title (top series) or by
  date (months and streak: the earlier wins).
- **Text**: every visible string comes from `Views/Texts.fs` in English and Spanish. Genre, studio
  and format names are MyAnimeList's and aren't translated. Month names are the viewer culture's
  own names.
- **Design**: it uses the shared design system and components, works from 360px wide in both
  languages, and has no ranking, comparison with other users or image export (FR-015, FR-016).
