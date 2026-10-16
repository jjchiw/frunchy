# Implementation Plan: Charts and Trends

**Branch**: `012-charts-trends` | **Date**: 2026-10-16 | **Spec**: [charts-trends.spec.md](charts-trends.spec.md)

## Summary

- **Charts**: three public charts (week, season, all time) rank series by how many different
  people watched them in the window.
- **Privacy**: a minimum of 3 people by default, as a setting.
- **Week extras**: the week chart adds movement against the previous week and an 8-week trend line
  drawn as inline SVG.
- **Counts elsewhere**: anime and episode pages gain "Watched by N people on Frunchy".
- **Code**: a shared `Infrastructure/Charts.fs` does the counting, and one slice serves the pages.
- **Storage**: no new data, only an index on `watch_events (watched_at)`.

## Technical Context

- **Language/Version**: F# on .NET 10.
- **Primary Dependencies**: Oxpecker and Npgsql; no new package and no chart library.
- **Storage**: PostgreSQL, read only, plus one index.
- **Testing**: xUnit v3, Testcontainers and a controlled `TimeProvider`; synthetic bulk rows for
  speed.
- **Target Platform**: server-rendered HTML.
- **Project Type**: web service.
- **Performance Goals**: each chart in under 1 s at 2M watch events.
- **Constraints**:
  - the privacy minimum everywhere;
  - no person named;
  - WCAG AA;
  - en and es.
- **Scale/Scope**: 3 routes in 1 slice, 1 shared module, 2 page lines, 1 navigation link, 1 index.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Read only; nothing derived is stored. |
| II. Boring tech | PASS. SQL plus inline SVG, with no library, cache or worker. If all-time ever misses its budget, a precomputed table would need a written justification. |
| III. Vertical slices | PASS. One slice for the charts. The shared counting sits in `Infrastructure/` because three slices use it. |
| IV. Ingestion | PASS. Not touched. |
| V. Tests | PASS. Unit tests for seasons, the minimum, movement and blocks; integration tests on real Postgres for every figure, the minimum, links, states and speed. |
| Constraints | PASS. The index goes in `initializeSchema` and `SCHEMA.md`; en and es texts; CDN-only frontend. |

## Project Structure

```text
Infrastructure/Database.fs     # ix_watch_events_watched
Infrastructure/Charts.fs       # NEW: minPeople, seasonOf, chart, seriesPeople, episodePeople
Views/Texts.fs                 # charts page, tabs, season label, rows, movement, trend label, notes, counts, nav
Views/Layout.fs                # "Charts" nav link
Views/Theme.fs                 # icon cases if needed (subset test)
Handlers/Charts/Charts.fs      # NEW: GET /charts, /charts/season, /charts/all
Handlers/Users/Anime.fs        # people count line
Handlers/Episodes/Episode.fs   # people count line (group)
Program.fs, FrunchyBackend.fsproj, SCHEMA.md
tests: ChartsUnitTests.fs, ChartsTests.fs (NEW); DesignSystemTests.fs, SqlGuardrailTests (extended)
docs: CLAUDE.md, FRONTEND.md, README.md
```

## Review points (main session)

- **Privacy**: the minimum is applied in SQL (`HAVING`), not only in the view. No user identity
  reaches the chart view model except the viewer's own `mine` flag.
- **The setting's lower bound**: 2 is enforced in code.
- **Speed**: measured honestly, with no cache added quietly.

## Delegation

Sonnet agents in two steps: first the core (index, module, unit tests, texts), then the UI (page,
navigation, counts, integration tests). The main session reviews, runs the suite and audits.

## Complexity Tracking

| Addition | Why | Simpler alternative rejected because |
|---|---|---|
| A 10-minute in-memory copy of the all-time chart (`IMemoryCache`, built into ASP.NET Core) | All time aggregates every watch event: about 2.5 s at 2M events after the query rewrite (research R4, measured) | Per-request computation misses SC-003 by 2.5×. A precomputed table or a worker is more machinery than one cached list of 20 rows. |
