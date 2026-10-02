# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Frunchy is a "Last.fm for anime": it ingests a user's Crunchyroll watch history (sent by the sibling `../browser-extension` Svelte extension) and builds statistics, and later an "anime DNA" profile, from it. This repo is the F# backend, which also server-renders the web UI in English (default) or Spanish.

The guiding principle (see `README.md` and `.specify/memory/constitution.md`) is to keep it simple: **watch history is the core asset**, and the code deliberately avoids queues, event sourcing, CQRS, and frameworks like Wolverine/Hangfire until real usage justifies them. Don't add that kind of infrastructure unless asked. Features go through spec-kit (`specs/###-name/`: spec → plan → tasks → implement); feature 001 is in `specs/001-crunchyroll-history-import/`.

## Commands

```bash
dotnet build
dotnet test                                        # from the root (FrunchyBackend.slnx); needs Docker (Testcontainers)
dotnet test --filter-class "*DashboardTests*"      # one test class (Microsoft.Testing.Platform filters)
dotnet run --launch-profile http                   # http://localhost:5099, the dev URL the extension uses
dotnet run -- user-create <username> "<Name>"      # operator CLI; prints the sync key once
```

- .NET SDK 10. xUnit v3 runs on Microsoft.Testing.Platform, which needs the root `global.json`'s `"test": { "runner": "Microsoft.Testing.Platform" }`. No linter.
- Connection string: `ConnectionStrings:Database` in `appsettings.json` (`postgres/postgres@localhost:5432/frunchydb`), or the `ConnectionStrings__Database` env var. Local Postgres runs in Docker (container `postgres-postgres-1`) and there is no local `psql`, so use `docker exec postgres-postgres-1 createdb -U postgres <db>`. The app refuses the pre-001 legacy layout (`LegacySchemaException`); reset it as described in the README.
- Operator CLI: `user-create`, `user-regenerate-key`, `user-reset-binding`, `user-rebuild`, `user-delete --yes`, `user-list` (`specs/001-crunchyroll-history-import/contracts/operator-cli.md`).
- `JsonSamples/PostHistory.json` (2022, one raw page) and `JsonSamples/crunchyroll-history-2026-sample.json` (2026, 4 pages in the import envelope) are the test fixtures. Full personal captures (`crunchyroll-history-YYYY-MM-DD.json`) are gitignored and must never be committed. `.private/test.http` is stale (old `/post-history` route).

## The browser extension

`../browser-extension` is built by **its own Claude session**. Never edit that repo. `specs/001-crunchyroll-history-import/contracts/import-api.md` is the shared interface. Send it any change with SendMessage (find the session with ListAgents by its cwd), and include that session in the plan, tasks and implementation of any feature touching the contract.

## Architecture

**Load the `oxpecker` skill** (`.claude/skills/oxpecker/`) before touching routes, handlers, views, htmx or Alpine. It covers the API, the upstream examples, and which documented features the pinned package versions **don't** have (the upstream docs track a newer `develop` branch).

**Vertical slices: one file per endpoint in `Handlers/`.** Each slice is laid out top to bottom as: view (`HtmlElement` functions piped into `Views.Layout.page texts title`; for JSON endpoints this section holds the request/response records, whose field names *are* the camelCase wire names) → `private` data-access functions with the slice's own SQL → `handler` → `let endpoint = GET [ route "/path" handler ]` with an explicit verb. Templates: `Handlers/Users/Dashboard.fs` (HTML) and `Handlers/Api/CreateImport.fs` (JSON). Operator commands follow the same idea, one file each in `Operations/`. `Program.fs` is only the composition root: `configureServices`, `configureApp` (localization, error/404 handlers), `endpoints`, CLI dispatch, and `buildApp`, which the tests reuse on a TestServer.

**F# compile order matters.** Files compile in the order listed in `FrunchyBackend.fsproj`: Domains → Infrastructure → Views → Handlers → Operations → Workers → Program. Add each new file in its place.

**Routes**:
- `GET /` (home) and `GET /u/{username}`: the public dashboard. Figures are computed by SQL at request time from `watch_events`.
- `POST /api/imports`, `GET /api/imports/{id}`, `GET /api/me`: JSON for the extension. They're authenticated by `Authorization: Bearer <syncKey>` (`Infrastructure/Auth.fs`; only a SHA-256 of the key is stored) and return errors as `{ error, message }` (`Infrastructure/ApiErrors.fs`).

**Ingestion pipeline** (the core flow):
```
extension → POST /api/imports → validate envelope { pages: [ { url, body, capturedAt } ] }
          → bind/verify the Crunchyroll history owner (account id from the page URL path; one owner per user)
          → cr_imports (pending) + cr_import_pages (body stored verbatim as `json`, never modified) → 202
ImportProcessorService → processNext: claim the oldest pending import (FOR UPDATE SKIP LOCKED; stale claims > 10 min retried)
          → Infrastructure/CrunchyrollPage.parsePage classifies each entry: processed / unresolved (no panel) / unsupported / failed
          → upsert shared catalog cr_series / cr_seasons / cr_episodes (freshness-guarded by source_at)
          → upsert watch_events (PK user + episode; replaced only by a strictly newer (watched_at, fully_watched, playhead))
          → issue rows + counts → completed (or failed with error)
```
- **Order-independence and idempotency are invariants.** Re-sending or reordering submissions must give identical results, and `user-rebuild` must reproduce identical figures. Tests enforce both.
- **Series are keyed on the entry's `parent_id`**, so franchise movies count with their series.
- **Unresolved entries** create a stub `cr_episodes` row (no `duration_ms`). They're excluded from the figures until a later page brings the details.
- **Paging fields** (`page` cursor, `total`, `meta`) are opaque and never read.

**Data access** is raw Npgsql with hand-written SQL. There's no ORM or repository layer. `DbConfig` is a DI singleton (`ctx.GetService<DbConfig>()`). The schema is the idempotent `initializeSchema` in `Infrastructure/Database.fs`, with no migrations: schema changes go there and into `SCHEMA.md`. Everything personal hangs off `users` with `ON DELETE CASCADE`, and the catalog tables are shared. Raw pages are only ever deleted by `user-delete` (the constitution's single exception).

**Localization**: every user-facing string lives in `Views/Texts.fs` (a typed record with `en` and `es` values). The language comes from `Accept-Language` only. Views never contain literals. ViewEngine writes non-ASCII as `&#NNN;`, so tests decode with `readHtml`. Times are rendered as `<time datetime="…Z" data-fr-time>` with a UTC fallback, and the layout's `Intl` script localizes them in the viewer's time zone.

**Tests** (`tests/FrunchyBackend.Tests`): one Postgres Testcontainer per run with a fresh database per test (`PostgresFixture.newApp`). The real app runs on a TestServer with the hosted worker disabled, and tests call `ImportProcessor.processNext` directly (`TestHelpers.processAll`). Fixture builders live in `Fixtures.fs` (`syntheticPages`, `envelopeOf`, …).

**Design references**: `design/` holds HTML/PNG mockups (dashboard, anime DNA profile, extension popup) and `design/neo_scrobble_profile/DESIGN.md` (tokens). Mockup elements without a real data source (ratings, ranks, trends) are out of scope until a feature adds the data.
