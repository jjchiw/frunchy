# Implementation Plan: Crunchyroll Watch-History Import with Real Dashboard

**Branch**: `001-crunchyroll-history-import` | **Date**: 2026-10-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-crunchyroll-history-import/spec.md`

## Summary

The browser extension posts Crunchyroll watch-history pages, in its own export envelope
`{ pages: [ { url, body, capturedAt } ] }`, to `POST /api/imports` with a per-user sync key.

The endpoint:
- authenticates the key;
- checks the Crunchyroll history owner from the page URLs, binding the user on their first
  submission;
- stores every page verbatim in a `json` column;
- returns 202.

A `BackgroundService` uses Postgres as its job queue. It claims pending imports with
`FOR UPDATE SKIP LOCKED` and normalizes the entries into a shared Crunchyroll catalog (series,
seasons, episodes) plus one `watch_event` per user and episode. The upserts are order-independent
and freshness-guarded. Every entry is recorded as processed, unresolved, unsupported or failed.

The public dashboard at `/u/{username}` computes its figures with SQL at request time:
- series, episodes, hours and days;
- the 6 most recent episodes;
- last sync time and whether a sync is in progress.

It renders English or Spanish from typed text records, chosen from `Accept-Language`, and its
`<time>` elements are formatted in the viewer's own time zone by `Intl`.

Operator CLI subcommands create users and keys, reset bindings, rebuild and delete. Integration
tests run against a real Postgres (Testcontainers) using both Crunchyroll fixtures.

## Technical Context

**Language/Version**: F# on .NET 10 (`net10.0`)

**Primary Dependencies**:
- Oxpecker 2.1.1, Oxpecker.ViewEngine 2.1.x, Oxpecker.Htmx 4.0.x (not used by this feature);
- Npgsql 10.0.x, upgraded from 8.0.1 for advisory GHSA-x9vc-6hfv-hg8c;
- System.Text.Json, plus ASP.NET Core Request Localization.
- Removed: FSharp.Data, Newtonsoft.Json.

**Storage**: PostgreSQL 14+. Raw pages in `json` (verbatim), the rest relational. Schema comes
from the idempotent bootstrap; there's no migration framework.

**Testing**: xUnit v3 + Testcontainers.PostgreSql + Microsoft.AspNetCore.TestHost, in
`tests/FrunchyBackend.Tests`. Fixtures: `JsonSamples/PostHistory.json` (2022) and
`JsonSamples/crunchyroll-history-2026-sample.json` (2026, 4 pages / 400 entries).

**Target Platform**: Linux or macOS server (Kestrel). Dev URL `http://localhost:5099`.

**Project Type**: web service. Server-rendered HTML + JSON API + background worker + operator CLI,
all in one executable.

**Performance Goals**:
- 202 response in < 2 s per 100-entry page (SC-001);
- a 6,400-entry first sync reflected in < 10 min (SC-002, expected: seconds);
- dashboard in < 2 s at 10k watch events (SC-007).

**Constraints**:
- request body ≤ 30,000,000 bytes and 1–100 pages per submission, no per-page entry cap;
- every Crunchyroll paging field treated as opaque;
- UI text only from `Views/Texts.fs` (en default, es);
- no admin HTTP surface.

**Scale/Scope**: a handful of users (002 "Accounts" comes before a second user is invited). The
reference history is 6,363 entries; design for 10k+ per user.

## Constitution Check

Gates from `.specify/memory/constitution.md` (v1.1.0). Checked before research and **re-checked
after Phase 1 design**; both passes are green.

- [x] **I. Core asset**: pages are stored verbatim (`json`, never UPDATEd) and only deleted by the
      user-deletion cascade, the constitution's explicit exception. Every derived table
      (`watch_events`, entry issues, import counts, catalog contents) is rebuilt by `user-rebuild`
      from `cr_import_pages` (research R7, R13).
- [x] **II. Boring tech**: Postgres job queue (`status` column + `SKIP LOCKED`), one
      `BackgroundService`, hand-written SQL with Npgsql. No broker, ORM, scheduler or event
      store. Sized to the 6,400-entry reference (research R6).
- [x] **III. Vertical slices**: one file per endpoint in `Handlers/` (`Home`, `Users/Dashboard`,
      `Api/CreateImport`, `Api/GetImportStatus`, `Api/WhoAmI`). Operator commands are one file
      each in `Operations/` (the CLI counterpart of `Handlers/`). `Program.fs` only wires services,
      middleware and endpoints, and dispatches CLI commands. The worker lives in `Workers/`.
- [x] **IV. Idempotent async ingestion**: the endpoint persists and returns 202; normalization
      happens in the worker. Dedup key = (user, Crunchyroll episode id) with order-independent
      upserts. No external APIs in this feature. Every entry gets an outcome and a reason.
- [x] **V. Tests**: integration tests per slice and for the worker against a real Postgres with
      both fixtures; unit tests for `CrunchyrollPage` parsing; `dotnet test` from the root
      (`FrunchyBackend.slnx`).
- [x] **Constraints**: localized UI (`Views/Texts.fs`; en default, es), CDN-only frontend (the
      inline `Intl` script needs no build), schema in `Infrastructure/Database.fs` with `SCHEMA.md`
      updated, all APIs present in the pinned Oxpecker versions (routing, `routef`, `GetService`,
      `WriteJson`, `WriteHtmlView` and the ViewEngine CEs are all in 2.1.x). Npgsql upgrade stated
      in R15.

No violations, so Complexity Tracking stays empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-crunchyroll-history-import/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R1–R18
├── data-model.md        # Phase 1: tables, state machine, figure definitions
├── quickstart.md        # Phase 1: validation guide
├── contracts/
│   ├── import-api.md    # shared with the browser extension
│   ├── dashboard-ui.md  # public pages, language, <time> contract
│   └── operator-cli.md  # operator commands
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
FrunchyBackend.slnx                      # NEW: app + tests, so `dotnet test` works from the root
FrunchyBackend.fsproj                    # compile order updated; DefaultItemExcludes tests/**; packages (R15)
Program.fs                               # composition root: configureServices / configureApp / endpoints; CLI dispatch
Properties/launchSettings.json           # + "http" profile on http://localhost:5099
Domains/
├── Identity.fs                          # User, Username, SyncKey, HistoryOwner
├── Tracking.fs                          # ImportStatus, EntryOutcome, ImportCounts, WatchEvent
└── Catalog.fs                           # CrSeries, CrSeason, CrEpisode   (Analytics.fs removed)
Infrastructure/
├── Database.fs                          # bootstrap: new schema + legacy-layout detection (R14)
├── CrunchyrollPage.fs                   # NEW, pure: page JSON → parsed entries + outcomes; owner from URL
└── Auth.fs                              # NEW: Bearer key → user lookup (SHA-256), shared by the API slices
Views/
├── Texts.fs                             # NEW: typed en/es text records + forCulture
└── Layout.fs                            # localized layout, <html lang>, Intl time script, no placeholder nav
Handlers/
├── Home.fs                              # GET /             (localized)
├── Users/Dashboard.fs                   # GET /u/{username} (view → SQL → handler → endpoint)
└── Api/
    ├── CreateImport.fs                  # POST /api/imports
    ├── GetImportStatus.fs               # GET  /api/imports/{id}
    └── WhoAmI.fs                        # GET  /api/me
Operations/                              # NEW: operator CLI slices
├── CreateUser.fs  ├── RegenerateKey.fs  ├── ResetBinding.fs
├── RebuildUser.fs ├── DeleteUser.fs     └── ListUsers.fs
Workers/
└── ImportProcessor.fs                   # processNext (pure-ish, testable) + BackgroundService loop
tests/FrunchyBackend.Tests/
├── FrunchyBackend.Tests.fsproj
├── Infrastructure/PostgresFixture.fs    # Testcontainers collection fixture + TestServer app builder
├── Fixtures.fs                          # loads both JsonSamples; wraps PostHistory.json into the envelope
├── CrunchyrollPageTests.fs              # unit
├── CreateImportTests.fs  GetImportStatusTests.fs  WhoAmITests.fs
├── ImportProcessorTests.fs              # counts, idempotency, order, resolution, expected figures, scale
├── DashboardTests.fs                    # figures, recent, empty, 404, en/es
└── OperationsTests.fs                   # rebuild identical, delete, regenerate, reset
SCHEMA.md                                # rewritten for the new schema
```

Removed: `Handlers/Dashboard.fs`, `Handlers/Profile.fs` (placeholders), `Handlers/Imports/*`
(replaced by `Handlers/Api/*`), `Domains/Analytics.fs`, `Types.fs`.

**Structure Decision**: one project plus one test project, following the existing layout.
Endpoints are grouped by audience: `Handlers/Api/` for the extension's JSON, `Handlers/Users/` for
public pages. The fsproj compile order is Domains → Infrastructure → Views → Handlers →
Operations → Workers → Program.

## Extension coordination

The browser extension lives in `../browser-extension` and is worked on by its own Claude session
(**`browser-extension-c7`** today; find it with `ListAgents`, matching its cwd). The backend
session never edits that repo. **`contracts/import-api.md` is the shared interface**; any change
to it is sent to that session.

| What the extension builds | Against | Can start | Final |
|---|---|---|---|
| Options page: backend base URL (default `http://localhost:5099`) + sync key; "validate" button | `GET /api/me` (200 / 401) | now (contract v1) | now |
| Relay each intercepted page as `{ url, body, capturedAt }`, one page per `POST /api/imports`, `Authorization: Bearer` | `POST /api/imports` | now | now, except `profileId` |
| Retry queue (`chrome.storage.local`): retry 5xx and network errors with backoff, never 4xx; drop on 202 | status table + error codes | now | now |
| Map error codes to its own localized popup messages (`history_owner_mismatch`, `history_owner_taken`, `unauthorized`, …) | Errors section | now | now |
| Popup: user name and "Abrir Dashboard Web" from `GET /api/me`; last sync; optional status polling | `GET /api/me`, `GET /api/imports/{id}` | now | now |
| Stop marker: newest `(id, date_played)` sent; stop paging once a page contains it | (extension-internal) | now | n/a |
| Optional: "upload export file" sending the export JSON as-is | `POST /api/imports` (≤ 30 MB) | now | now |
| **Profile capture**: history URL under two profiles plus ids from `/accounts/v1/me` and `/accounts/v1/me/multiprofile`; where the active profile is readable | research R5 | requested 2026-10-02 | **decides `profileId`** |

**Timeline**

1. When this plan is committed, the backend sends contract v1 to the extension session, with
   `profileId` marked pending.
2. When the capture arrives, the backend decides R5 and sends the `profileId` rule. This is a gate
   task before the binding implementation.
3. At the end of the US1 backend phase (endpoints and worker green), the backend tells the
   extension that `http://localhost:5099` is ready for integration. Joint check: a real sync of
   the user's history, with the dashboard showing about 327 series, 5,921 episodes and ≈ 2,367.6 h.

**Tasks**: `tasks.md` has an **"Extension (browser-extension repo)"** phase. Its tasks are owned and
checked off by the extension session and reported back by message; the backend session only
tracks them.

## Phase 0 → Phase 1 outputs

- Research: [research.md](research.md). All unknowns resolved except R5's `profileId` source, which
  is isolated behind an optional field and a gate task.
- Design: [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md).
- Agent context: `CLAUDE.md` updated to point at this plan, the new layout and commands.

## Complexity Tracking

> No Constitution Check violations, so nothing to justify.
