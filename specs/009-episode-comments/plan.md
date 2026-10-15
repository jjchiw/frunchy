# Implementation Plan: Episode Page and Comments

**Branch**: `009-episode-comments` | **Date**: 2026-10-15 | **Spec**: [episode-comments.spec.md](episode-comments.spec.md)

## Summary

- **Episode page**: a public page per catalog episode at `/anime/{seriesId}/episode/{episodeId}`,
  read from the shared catalog. Each episode row on the anime page links to it.
- **Comments**: one new `comments` table, already shaped like Megáfono 002's objects. The fediverse
  columns stay empty until the integration feature.
- **Dubs**: a comment stays on the episode it was written on. A dub and its original share a thread
  through the DNA page's key, computed per request (research R2).
- **Writes**:
  - authors create, edit and delete; delete leaves a tombstone, so ids are never reused;
  - operators hide and unhide, behind `requireOperator`, with security events.
- **No server calls**: no Megáfono call and no worker.

## Technical Context

**Language/Version**: F# on .NET 10

**Primary Dependencies**: Oxpecker, Oxpecker.ViewEngine, Npgsql (all pinned; no new package)

**Storage**: PostgreSQL. One new table, `comments`, in `initializeSchema`, documented in `SCHEMA.md`.

**Testing**: xUnit v3 on Microsoft.Testing.Platform, Testcontainers Postgres, the 2026 sample and
the feature 003 MyAnimeList fixtures

**Target Platform**: Linux server, server-rendered HTML

**Project Type**: web service with server-rendered UI

**Performance Goals**: an episode page with 1,000 comments under 1 s (SC-005)

**Constraints**: localized en/es texts; CDN-only frontend, no new script; native `<details>` for
spoilers

**Scale/Scope**: 6 routes in 4 slice files, 1 table, ~30 texts per language

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Raw imports untouched. `comments` is user content, not derived data, so there's nothing to rebuild; rebuilds and re-sent history never touch it (test). It hangs off `users` with `ON DELETE CASCADE`. |
| II. Boring tech | PASS. One table, hand-written SQL, no package, no worker. |
| III. Vertical slices | PASS. Four files in `Handlers/Episodes/`, each view → SQL → handler → endpoint. The season line and compact MyAnimeList block move to `Views/Components.fs`, now that two pages use them. |
| IV. Idempotent ingestion | PASS. Not touched. Comment creation is idempotent on its form-minted id (research R3). |
| V. Tests | PASS. Integration tests on real Postgres with the 2026 sample, for every slice (quickstart). |
| Constraints | PASS. Schema in `initializeSchema` + `SCHEMA.md`; every text in `Views/Texts.fs` (en + es); no new CDN or script; APIs in the pinned versions (`oxpecker` skill). |

Re-checked after Phase 1 design: no change.

## Project Structure

### Documentation (this feature)

```text
specs/009-episode-comments/
├── episode-comments.spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/episode-page.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
Infrastructure/Database.fs           # comments table + index (idempotent)
Domains/Identity.fs                  # SecurityEventType.CommentHidden / CommentUnhidden
Views/Texts.fs                       # episode page, comments, errors, events (en + es)
Views/Components.fs                  # seasonLine (title, audio, dub), malCompact; moved from Anime.fs
Handlers/Users/Anime.fs              # episode title links to the episode page; uses the moved components
Handlers/Episodes/Episode.fs         # NEW: GET page + POST create (re-render on 422)
Handlers/Episodes/EditComment.fs     # NEW: GET + POST edit (author only)
Handlers/Episodes/DeleteComment.fs   # NEW: POST delete (author only, tombstone)
Handlers/Episodes/HideComment.fs     # NEW: POST hide/unhide (requireOperator, events, log)
Handlers/Account/…                   # security activity labels for the two new events
Program.fs                           # endpoints, comment-write limiter
FrunchyBackend.fsproj                # new files in compile order (after Handlers/Users)
SCHEMA.md, CLAUDE.md (routes), FRONTEND.md, design/BRIEF.md §5.5 ("built"), docs/fediverse.md (step 1 done)
tests/FrunchyBackend.Tests/
  EpisodePageTests.fs, CommentsTests.fs, CommentThreadGroupTests.fs, CommentModerationTests.fs (NEW)
  DesignSystemTests.fs, SqlGuardrailTests.fs (extended)
```

**Structure Decision**: one feature folder, `Handlers/Episodes/`. Create lives with the page so a
422 can re-render it without calling another slice's private code (as `Review/Operators.fs` holds its
GET and POSTs).

## Security review points (main session)

- **Authorization first**: hide and unhide are wrapped in `requireOperator`, outermost. Edit and
  delete look up the author before reading the form. A non-author gets 404, never a hint.
- **Forms**: every POST uses `readValidatedForm`.
- **Limits**: `comment-write` is per IP. `review-write` stays operator-only, so outsiders never see
  a 429.
- **Content**: rendered as text only (ViewEngine escaping); no raw HTML path for `format = 'text'`.
  The `html` format isn't rendered by this feature at all.
- **Ids**: form ids are pattern-checked, and a conflict with another author's row stores nothing.
- **Privacy**: the page shows catalog data and comments, never who watched what.
- **SQL**: parameters only, covered by `SqlGuardrailTests`.

## Delegation

Sonnet subagents implement from `tasks.md`. The main session reviews each diff (the security points
first), checks it against this plan and the contract, and runs the tests. UI work goes through the
`impeccable` skill; routes and views through the `oxpecker` skill.

## Complexity Tracking

None.
