# Implementation Plan: Taste Compatibility

**Branch**: `011-taste-compatibility` | **Date**: 2026-10-15 | **Spec**: [taste-compatibility.spec.md](taste-compatibility.spec.md)

## Summary

- **Where it shows**: a compatibility card on another person's dashboard, for signed-in viewers,
  and a compare page at `/u/{username}/compare`.
- **The computation**: computed per request in a shared `Infrastructure/Compatibility.fs`. Two SQL
  statements on the DNA page's rules (deduplicated minutes, matched genres and studios), then pure
  F# for the score.
- **The score**: shared series measured against the smaller history, plus cosine similarity of
  genre shares, averaged and shown as one of 5 levels in words.
- **Storage**: no new data.
- **Look**: built in Night Broadcast through `impeccable`, with amber for the viewer and rose for
  the other person.

## Technical Context

- **Language/Version**: F# on .NET 10.
- **Primary Dependencies**: Oxpecker and Npgsql; no new package.
- **Storage**: PostgreSQL, read only.
- **Testing**: xUnit v3 and Testcontainers, the 2026 sample and the feature 003 MyAnimeList
  fixtures; unit tests for the score.
- **Target Platform**: server-rendered HTML.
- **Project Type**: web service.
- **Performance Goals**: a compare page at 2,000 episodes per user in under 1 s.
- **Constraints**:
  - en and es texts, with plurals;
  - WCAG AA;
  - no percentage shown;
  - symmetric levels.
- **Scale/Scope**: 1 new route, 1 dashboard card, 1 shared module, ~30 texts per language.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Read-only; nothing derived is stored. |
| II. Boring tech | PASS. SQL plus F#, no package. |
| III. Vertical slices | PASS. The compare page is one slice. The dashboard card is in the dashboard slice. The shared computation sits in `Infrastructure/` because two slices use it (like `MalActions`). |
| IV. Ingestion | PASS. Not touched. |
| V. Tests | PASS. Unit tests for the score; integration tests on real Postgres with the sample and fixtures for every figure, access rule and state. |
| Constraints | PASS. Texts in `Views/Texts.fs`, CDN-only frontend, no schema change. |

## Project Structure

```text
Infrastructure/Compatibility.fs   # NEW: load (2 statements) + pure score/level/sections
Views/Texts.fs                    # card, page, levels, plurals, not-enough states (en + es)
Views/Components.fs               # levelMeter; shareBar gains a tone (primary | secondary)
Handlers/Users/Dashboard.fs       # the card for a signed-in non-owner
Handlers/Users/Compare.fs         # NEW: GET /u/{username}/compare
Program.fs, FrunchyBackend.fsproj
tests: CompatibilityScoreTests.fs (unit), CompatibilityTests.fs (NEW); DesignSystemTests.fs, SqlGuardrailTests (extended)
docs: CLAUDE.md (route), FRONTEND.md, README.md roadmap 011
```

## Review points (main session)

- **Privacy**: the card and page only show the viewer's own data to the viewer, plus the owner's
  public data. The viewer id comes from the session, never from the URL.
- **No hidden precision**: no score or percentage appears in markup or attributes.
- **Symmetry**: the computation doesn't depend on who views.
- **Cost**: a signed-out dashboard runs no compatibility query.

## Delegation

Sonnet agents implement in two steps: the core (module and score tests), then the UI (card, page
and integration tests). The main session reviews each diff, runs the suite and the final audit.

## Complexity Tracking

None.
