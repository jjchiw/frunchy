# Implementation Plan: Anime Page

**Branch**: `006-anime-page` | **Date**: 2026-10-10 | **Spec**: [anime-page.spec.md](anime-page.spec.md)

## Summary

The anime page is a new vertical slice, `Handlers/Users/Anime.fs`, built like the year page:
- **Data**: at request time, the user lookup, then one statement over the user's watches of one
  series: totals with the dashboard's rule, seasons with their known-episode counts and
  MyAnimeList details, and the watched episodes.
- **Links**: the dashboard, the DNA page and the year page link series titles to it. That means
  carrying the series id on the dashboard's recent cards, and letting `shareLine` take a linked
  title.

There's no new table and no new package. A prototype for One Piece (266 episodes, 7 seasons) took
89 ms in `psql`.

## Project Structure

```text
Handlers/Users/Anime.fs          # NEW slice: view → SQL → handler → GET /u/{username}/anime/{seriesId}
Handlers/Users/Dashboard.fs      # recent cards carry series_id; card title and header's newest title link
Handlers/Users/Dna.fs            # insignia title and genre top anime link; genre "Others" no longer fakes a top
Handlers/Users/Year.fs           # top series title and genre top anime link; same "Others" fix
Views/Components.fs              # shareLine detail becomes an element; + seriesLink
Views/Texts.fs                   # anime page texts (en + es); DnaTopAnime split into a "Top:" prefix
Program.fs, FrunchyBackend.fsproj
tests/FrunchyBackend.Tests/AnimeTests.fs, AnimeLinkTests.fs   # NEW
tests/FrunchyBackend.Tests/DesignSystemTests.fs               # + the anime page
design/BRIEF.md, README.md, CLAUDE.md, FRONTEND.md, SCHEMA.md
```

**Structure Decision**: one slice with one route, following the year page. The link helper that
three slices use goes in `Views/Components.fs` (constitution III).

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Read only; nothing stored; figures identical after `user-rebuild` and a re-send (tested). |
| II. Boring tech | PASS. SQL at request time, no cache, no package. |
| III. Vertical slices | PASS. One file per endpoint; the shared link helper lives in `Views/`. |
| IV. Idempotent ingestion | PASS. Not touched. |
| V. Tests | PASS. `AnimeTests`, `AnimeLinkTests` (integration, real Postgres, 2026 sample + feature 003 fixtures); the design-system page check. |
| Constraints | PASS. Texts in `Views/Texts.fs` (en + es), CDN-only, no schema change (`SCHEMA.md` note), only pinned package APIs. |

Re-checked after design: unchanged.

## Delegation

Per the owner's instructions, implementation tasks go to Sonnet subagents. The main session reviews
each diff against this plan and the contract, and runs the tests, before anything is merged.
