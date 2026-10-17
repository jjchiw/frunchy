# Implementation Plan: Public Series Page

**Branch**: `013-series-page` | **Date**: 2026-10-17 | **Spec**: [series-page.spec.md](series-page.spec.md)

## Summary

- **The page**: `/anime/{seriesId}` is a public page per catalog series. It has a header (title,
  poster, Crunchyroll link, people all time and this week above the charts' minimum) and seasons in
  Crunchyroll's order, with their MyAnimeList details and known episodes. Each episode links to its
  episode page with its comment count, and long seasons collapse.
- **Links in**: chart rows, episode pages and anime pages link to it.
- **Usernames**: new usernames can't end in `-`; lookups keep today's rule.
- **Storage**: none new.

## Technical Context

- **Language/Version**: F# on .NET 10.
- **Primary Dependencies**: Oxpecker and Npgsql; no new package.
- **Storage**: PostgreSQL, read only.
- **Testing**: xUnit v3 and Testcontainers, the 2026 sample and the 003 fixtures; synthetic episodes
  for speed.
- **Target Platform**: server-rendered HTML.
- **Project Type**: web service.
- **Performance Goals**: a 1,100-episode series page in under 1 s.
- **Constraints**:
  - no person named;
  - the charts' minimum gates the counts;
  - WCAG AA;
  - en and es.
- **Scale/Scope**: 1 route, 3 link changes, 1 component moved, 1 username rule.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Read only. |
| II. Boring tech | PASS. SQL plus a native `<details>`; no script. |
| III. Vertical slices | PASS. One new slice. `malDetails` moves to `Views/Components.fs` now that two slices use it. |
| IV. Ingestion | PASS. Not touched. |
| V. Tests | PASS. Integration tests on real Postgres for every field, count, link and username rule; the 012 tests change only where the chart-row link moved. |
| Constraints | PASS. No schema change; en and es texts; CDN-only frontend. |

## Project Structure

```text
Domains/Identity.fs           # Username.tryCreateNew
Handlers/Auth/SignUp.fs, Operations/CreateUser.fs   # use tryCreateNew
Views/Texts.fs                # series page texts; UsernameHint (en/es) mentions the ending; "Your history"; series-page link
Views/Components.fs           # malDetails (moved from Anime.fs)
Infrastructure/Charts.fs      # people since a date (this week) for one series
Handlers/Series/Series.fs     # NEW: GET /anime/{seriesId}
Handlers/Charts/Charts.fs     # row title → series page; "Your history" link
Handlers/Episodes/Episode.fs  # series title → series page
Handlers/Users/Anime.fs       # link to the series page; uses Components.malDetails
Program.fs, FrunchyBackend.fsproj
tests: SeriesPageTests.fs, SeriesLinkTests.fs (NEW); username tests (extended); ChartsTests (row link moved); DesignSystemTests
docs: CLAUDE.md, FRONTEND.md, README.md, docs/fediverse.md (series profile URL, usernames done)
```

## Review points (main session)

- **Hooks**: the anime page's output and hooks are unchanged after `malDetails` moves.
- **Usernames**: lookups stay permissive; only the creation paths tighten.
- **Privacy**: no person named, and counts are gated.

## Delegation

One Sonnet agent for the page and links, one for the usernames and polish. The main session reviews
and audits.

## Complexity Tracking

None.
