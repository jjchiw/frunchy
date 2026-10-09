# Research: Year in Review

Prototyped on the owner's real database (`frunchydb`, user `jjchiw`, 2026-10-09). The history
spans 2021–2026, about 1,000 fully watched episodes a year.

## R1. Which watches belong to a year

- **Decision**: a watch belongs to the UTC calendar year of its `watched_at`, the latest watch of
  that episode. Days and months use `watched_at AT TIME ZONE 'UTC'`, so the database session's
  time zone can't shift them.
- **Rationale**: FR-004. `watch_events` keeps one row per user and episode, so no earlier date
  exists to use.
- **Alternatives**: the viewer's time zone. The server doesn't know it, and the figures would
  change from one viewer to the next.

## R2. Totals agree with the dashboard

- **Decision**: hours, days, fully watched episodes and series use the dashboard's rule, without
  deduplication, over the year's watches. Each year's hours are rounded to 0.1. The test allows
  0.1 hour per year when adding the years up (SC-002).
- **Rationale**: the dashboard and DNA totals use the same un-deduplicated rule, so the three pages
  agree.

## R3. MyAnimeList figures follow the DNA page

- **Decision**: genres (top 5 plus "Others"), studios and formats reuse the DNA page's
  deduplicated minutes. The dedup key is the original season plus the episode number. Explicit
  genres drop out through `kind = 'genre'`; there's no id filter. The page shows the **top 3
  studios**, where the DNA page shows 2. One coverage figure for the year (matched ÷ deduplicated
  minutes) appears on each of the three sections.
- **Most-watched series**: the DNA insignia query, restricted to the year. It uses deduplicated
  minutes, the poster of its most-watched matched season or else a thumbnail, and its fully
  watched count after dedup.

## R4. New versus continued series

- **Decision**: a series counts in the year when it has a fully watched episode that year. It's
  new when the earliest `watched_at` across all of its held episodes falls in the year, and
  continued otherwise.
- **Measured**: in 2025, 56 new and 35 continued, out of 91 series.
- **Limit**: a series rewatched entirely in a later year counts as new there. FR-009 says so, and
  the page's rewatch note covers it.

## R5. Months and streak

- **Months**: 12 buckets of minutes. The busiest month is the one with the most minutes; a tie goes
  to the earlier month. Labels are `t.Culture.DateTimeFormat` month names, first letter
  capitalised for Spanish. No new texts: they're the culture's own names.
- **Streak**: the distinct UTC days with a fully watched episode, grouped into runs with the
  `d - row_number()` trick. The longest run wins, and a tie goes to the earlier run. The first and
  last days are formatted on the server with `t.Culture` (`d MMM`). The client-side `timeElement`
  would shift a UTC date into the viewer's zone.
- **Measured**: 2025's streak is 38 days (Feb 16 – Mar 25). The busiest month is January
  (47.8 h).

## R6. One statement, timing

- **Decision**: the user lookup, then one batched statement shaped like the DNA page's
  (`kind, name, ms, n, …` rows). A second small statement lists the years with history.
- **Measured**: 705 ms for the prototype in `psql`, inside SC-003's 1 s. It needs the `firsts` CTE
  over the user's whole history, which is the expensive part. The task re-measures it on the slice.

## R7. Routes and states

- **`/u/{username}/year`**: redirects (302) to the most recent year with history, or to the current
  year when there's none.
- **`/u/{username}/year/{year}`**: the year is taken as text and parsed in the handler.
  - A non-numeric year, one before 2000, or one after the current year gets the not-found message
    page with status 404. So does an unknown user, with the "User not found" text.
  - A year with no history that isn't the current year redirects to `/u/{username}/year`.
  - The current year with no history shows the year's empty state.
- **Clock**: `TimeProvider` gives the current year, so the tests can fix it.

## R8. Shared helpers move to Components

- **Decision**: `section`, `sectionHeading`, `coverageNote`, `notEnough`, `shareLine`, `inv` and
  `pctText` move from `Dna.fs` into `Views/Components.fs`. They keep their behaviour, and the DNA
  tests prove it. A new `yearPicker` renders year links as pills, with `aria-current="page"` on the
  year shown.
- **Rationale**: constitution III. Shared UI goes in `Views/` once two slices use it.

## R9. Texts and icons

- **Texts**: the year page gets its own texts: title, heading, rewatch note, section titles, streak
  and new-versus-continued phrasing, the empty state with its count out of 15, and the link labels.
  The DNA empty-state texts name the DNA page, so they aren't reused. The section-level "Not enough
  MyAnimeList data yet" and the coverage sentence are reused.
- **Icons**: `CalendarMonth` and `LocalFireDepartment` join `Theme.Icon`, so the font subset follows
  automatically.
