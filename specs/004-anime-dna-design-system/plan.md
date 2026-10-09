# Implementation Plan: Anime DNA Profile and Shared Design System

**Branch**: `004-anime-dna-design-system` | **Date**: 2026-10-08 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/004-anime-dna-design-system/spec.md`

## Summary

The feature has two parts.

**1. A DNA page** (`GET /u/{username}/dna`), as one new vertical slice, `Handlers/Users/Dna.fs`.
- It computes everything at request time in one database round trip: a deduplicated
  "watched minutes" CTE feeds one statement per figure.
- The figures are studio, decade, genres plus Others, format, insignia and 90-day pace, plus the
  totals from the dashboard's own query.
- There's no new table. A prototype on the owner's real data takes 334 ms and gives sensible
  figures (research R4–R10).

**2. The whole web UI moves from DaisyUI to the "Neo-Scrobble Profile" design system.**
- **Tokens**: CSS custom properties named like the extension's `tokens.css`, mapped into the
  Tailwind CDN config, in `Views/Theme.fs`.
- **Components**: one shared set, in `Views/Components.fs`.
- **Layout**: a rewritten `Views/Layout.fs`.
- **Pages**: each page's view function is restyled. Routes, handlers, SQL and every `data-*` test
  hook stay put.
- **Process**: the work goes page by page through impeccable (shape → build → audit), using the
  designer's mockups as they arrive.

## Technical Context

- **Language/Version**: F# on .NET 10 (`net10.0`).
- **Primary Dependencies**:
  - **No new package**: Oxpecker 2.1.1, Oxpecker.ViewEngine 2.1.0, Oxpecker.Htmx 4.0.x,
    Oxpecker.Alpine 0.3.0 and Npgsql, as today.
  - **Client side, from CDNs**: Tailwind (as today), htmx 4, Alpine 3, and Google Fonts (Plus
    Jakarta Sans, a Material Symbols subset). DaisyUI is removed.
- **Storage**: PostgreSQL, read only. No schema change.
- **Testing**:
  - xUnit v3 on Microsoft.Testing.Platform, Testcontainers Postgres, and the 2026 sample plus the
    feature 003 MyAnimeList fixtures.
  - `TimeProvider` for pace.
  - A unit test that reads the sibling extension's `tokens.css`.
- **Target Platform**: Linux or macOS server; current evergreen browsers; phones from 360px.
- **Project Type**: a server-rendered web application.
- **Performance Goals**: the DNA page is ready in under 1 s for about 6,000 watched episodes
  (SC-002); the prototype took 334 ms.
- **Constraints**:
  - no frontend build step;
  - every text in `Views/Texts.fs` (en and es);
  - the extension's API contract is unchanged;
  - behaviour is unchanged outside the new slice.
- **Scale/Scope**:
  - 1 new page;
  - about 12 restyled pages and states;
  - 2 new shared view modules;
  - about 40 new text entries per language.

## Constitution Check

Gates from `.specify/memory/constitution.md` (v1.1.0). They were checked before research and
**re-checked after design**, and both are green. One wording amendment (PATCH, research R15) goes
with this feature.

- [x] **I. Core asset**:
  - raw imports and watch events are only read;
  - no derived table is added;
  - the DNA is a pure function of rebuildable data, and tests assert identical figures after
    `user-rebuild` and after re-sending (data-model, "Rebuild statement").
- [x] **II. Boring tech**:
  - no new package, service or cache;
  - figures come from SQL at request time;
  - a materialized table was considered and rejected for lack of measured pain (research R10).
- [x] **III. Vertical slices**:
  - the DNA page is one file, `Handlers/Users/Dna.fs` (view → SQL → handler → `GET` endpoint);
  - the shared UI used by every slice goes in `Views/` (`Theme.fs`, `Components.fs`, `Layout.fs`),
    which the constitution allows once two or more slices share it;
  - slices don't call each other's SQL. The DNA slice repeats the dashboard's totals query on
    purpose, and a test pins the two equal.
- [x] **IV. Idempotent async ingestion**: not touched. There's no ingestion and no external call:
  the page reads stored MyAnimeList data.
- [x] **V. Tests**:
  - `DnaTests` and `DnaPaceTests` are integration tests against real Postgres with `JsonSamples`
    and the MyAnimeList fixtures;
  - `DesignSystemTests` are unit tests for token parity, contrast and test hooks;
  - the whole existing suite stays green unchanged (the behaviour baseline).
- [x] **Constraints**:
  - every label is in `Views/Texts.fs` (en and es), and MyAnimeList names aren't translated;
  - the frontend is CDN-only with no build: the UI wording changes from "Tailwind/DaisyUI" to
    "Tailwind, fonts and icons", amendment 1.1.1;
  - no schema change, so `SCHEMA.md` gets only a note;
  - only APIs in the pinned packages are used (Alpine `xData`/`xOn`, existing ViewEngine elements).

## Project Structure

### Documentation (this feature)

```text
specs/004-anime-dna-design-system/
├── spec.md
├── plan.md                  # this file
├── research.md              # R1–R16
├── data-model.md            # computed entities, no schema change
├── quickstart.md            # validation, expected real figures, review steps, revisit list
├── contracts/
│   ├── dna-page.md          # route, sections, data-* hooks, states
│   └── design-system.md     # tokens (shared with the extension), components, hooks kept
├── checklists/requirements.md
└── tasks.md                 # /speckit-tasks
```

### Source Code (repository root)

```text
Views/Theme.fs                    # NEW: :root tokens (extension names), tailwind.config, component CSS (@apply)
Views/Components.fs               # NEW: card, metricTile, pill, genreBar, button, field, alert, emptyState, …
Views/Layout.fs                   # head (fonts, tokens), navigation, footer, wordmark; messagePage restyled
Views/Texts.fs                    # DNA texts, copy link, coverage, pace labels, empty states (en + es)
Handlers/Users/Dna.fs             # NEW slice: view → SQL (dna CTE + one statement per figure) → handler → GET
Handlers/Users/Dashboard.fs       # restyle the view only; add the DNA link (data-dna-link)
Handlers/Home.fs                  # restyle
Handlers/Auth/{SignIn,SignUp,Claim,Reconfirm}.fs        # restyle views only
Handlers/Account/{AccountPage,SyncKey,DeleteAccount}.fs # restyle views only
Program.fs                        # + Handlers.Users.Dna.endpoint; message pages use the new layout
FrunchyBackend.fsproj             # Theme.fs and Components.fs before Layout.fs; Dna.fs after Dashboard.fs
tests/FrunchyBackend.Tests/DnaTests.fs, DnaPaceTests.fs, DesignSystemTests.fs   # NEW
design/neo_scrobble_profile/DESIGN.md   # prose corrected to the YAML values (R16)
.specify/memory/constitution.md          # 1.1.1: UI wording (R15)
README.md, CLAUDE.md, SCHEMA.md          # DNA page, design system, roadmap 004
```

**Structure Decision**: this follows the repo's vertical-slice layout. The only new top-level
pieces are two shared view modules in `Views/`, compiled before `Layout.fs`, which uses them.

## Implementation order (for /speckit-tasks)

1. **Foundation**:
   - `Theme.fs`, `Components.fs` and the new `Layout.fs`;
   - `DesignSystemTests` (parity, contrast, no DaisyUI, hooks);
   - the existing suite must stay green before anything else moves.
2. **US1, the DNA page**:
   - texts first;
   - then the slice with its tests (figures, states, pace, rebuild and re-send stability);
   - then the dashboard link;
   - checked against the real figures in the quickstart.
3. **US2, the restyle**:
   - the dashboard, then the home page, the sign-in pages, the account pages and the message pages;
   - each page goes through an impeccable shape → build → audit pass, and pages without a mockup go
     on the revisit list.
4. **Polish**:
   - `DESIGN.md`, the constitution amendment, README, CLAUDE.md and SCHEMA.md;
   - the full `dotnet test` with 0 warnings;
   - the quickstart validation log;
   - telling the extension session that the website tokens are live.

## Complexity Tracking

No constitution violations to justify.
