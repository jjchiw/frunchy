# Implementation Plan: Year in Review

**Branch**: `005-year-in-review` | **Date**: 2026-10-09 | **Spec**: [year-in-review.spec.md](year-in-review.spec.md)

## Summary

The year page is a new vertical slice, `Handlers/Users/Year.fs`, built like the DNA page:
- **Data**: one SQL statement at request time over the user's watches in one UTC calendar year,
  reusing feature 004's rules. Minutes are weighted, a dub and its original count once for the
  MyAnimeList figures, and totals are not deduplicated so they agree with the dashboard. The
  new figures are per-month minutes, the longest daily streak, and new versus continued series.
- **Shared view helpers**: `section`, `coverageNote`, `notEnough`, `shareLine` and the number
  formatters move from `Dna.fs` into `Views/Components.fs` so both pages use one copy.
- **Links**: the dashboard and the DNA page link to the year page.

There's no new table and no new package. A prototype on the owner's 2025 data took 705 ms in
`psql` (research R6).

## Project Structure

```text
Handlers/Users/Year.fs            # NEW slice: view → SQL → handler → GET /u/{username}/year and /year/{year}
Handlers/Users/Dna.fs             # helpers moved to Components; header gains the year link
Handlers/Users/Dashboard.fs       # header gains the year link
Views/Components.fs               # + section, sectionHeading, coverageNote, notEnough, shareLine, inv, pctText, yearPicker
Views/Theme.fs                    # + icons CalendarMonth, LocalFireDepartment
Views/Texts.fs                    # year texts (en + es)
Program.fs                        # yield! Handlers.Users.Year.endpoint
FrunchyBackend.fsproj             # Year.fs after Dna.fs
tests/FrunchyBackend.Tests/YearTests.fs        # NEW
tests/FrunchyBackend.Tests/DesignSystemTests.fs # + the year page
design/BRIEF.md, README.md, CLAUDE.md, FRONTEND.md, SCHEMA.md
```

**Structure Decision**: one slice with two routes, following the DNA page. The helpers that two
slices now use move to `Views/` (constitution III).

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Read only; no derived table; figures are a pure function of rebuildable data, and a test asserts identical figures after `user-rebuild` and a re-send. |
| II. Boring tech | PASS. SQL at request time, no cache, no package. |
| III. Vertical slices | PASS. One file per endpoint group in `Handlers/Users/`; helpers shared by two slices move to `Views/Components.fs`. |
| IV. Idempotent ingestion | PASS. Not touched. |
| V. Tests | PASS. `YearTests` (integration, real Postgres, 2026 sample + feature 003 fixtures, fixed clock) and the design-system page check. |
| Constraints | PASS. Texts in `Views/Texts.fs` (en + es), CDN-only, no schema change (`SCHEMA.md` gets a note), only pinned package APIs. |

Re-checked after design: unchanged.
