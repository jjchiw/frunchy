# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Frunchy is a "Last.fm for anime": it ingests a user's Crunchyroll watch history (sent by the sibling `../browser-extension` Svelte extension) and builds statistics and an "anime DNA" profile from it. This repo is the F# backend, which also server-renders the web UI. User-facing UI text is in Spanish.

The guiding principle (see `README.md`) is to keep it simple: **watch history is the core asset**, and the code deliberately avoids queues, event sourcing, CQRS, and frameworks like Wolverine/Hangfire until real usage justifies them. Don't add that kind of infrastructure unless asked.

## Commands

```bash
dotnet restore
dotnet build
dotnet run          # needs a reachable PostgreSQL; schema is created on startup
```

- .NET SDK 10 (`net10.0`). There is no test project and no linter configured.
- The connection string is `ConnectionStrings:Database` in `appsettings.json` (defaults to `postgres/postgres@localhost:5432/frunchydb`).
- `Properties/launchSettings.json` binds to `https://localhost:5001`. Use `dotnet run --urls http://localhost:5099` for a quick smoke test with curl.
- `.private/test.http` holds sample requests with real Crunchyroll payloads. Its `/post-history` route no longer exists; the current one is `POST /imports`, with body `{ user_id, data: "<json string>" }`.

## Architecture

**Load the `oxpecker` skill** (`.claude/skills/oxpecker/`) before touching routes, handlers, views, htmx or Alpine. It covers the API, the upstream examples, and which documented features the pinned package versions **don't** have (the upstream docs track a newer `develop` branch).

**Vertical slices: one file per endpoint in `Handlers/`.** Each slice is laid out top to bottom as: view (`HtmlElement` functions piped into `Views.Layout.page "<Title>"`; for JSON endpoints this section holds the request/response records instead) → `private` data-access functions with the slice's own SQL → `handler: EndpointHandler` → `let endpoint = GET [ route "/path" handler ]` with an explicit verb. `Program.fs` is only the composition root: DI, middleware, the error/404 handlers, and the `endpoints` list of `Handlers.<Slice>.endpoint`. Only cross-slice UI belongs in `Views/` (currently just `Layout.fs`). Use `Handlers/Dashboard.fs` (HTML) and `Handlers/Imports/CreateImport.fs` (JSON) as templates.

**F# compile order matters.** Files compile in the order listed in `FrunchyBackend.fsproj`. A new slice goes after `Views/Layout.fs` and before `Program.fs`, and anything it depends on must come earlier.

**Stack**: Oxpecker 2.1 on ASP.NET Core endpoint routing; Oxpecker.ViewEngine CE views (`div(class'="...") { ... }`); Oxpecker.Htmx **4.0** (the htmx 4 API, so `Views/Layout.fs` must load htmx 4.x); Oxpecker.Alpine. Tailwind, DaisyUI, htmx and Alpine come from CDNs, so there's no frontend build. Handlers get dependencies via `ctx.GetService<DbConfig>()`.

**Routes**:
- `GET /`, `/dashboard`, `/profile`: HTML pages. Dashboard and profile still render **hardcoded placeholder data** (marked TODO).
- `POST /imports` (`Handlers/Imports/CreateImport.fs`): inserts the raw payload into `cr_imports` (JSONB) plus a `Pending` row in `import_records`, then returns 202.
- `GET /imports/{%O:guid}` (`Handlers/Imports/GetImportStatus.fs`): reads the status from `import_records`. An invalid GUID gets a 404 from routing.

**Ingestion pipeline** (the core flow):
```
extension → POST /imports → cr_imports (raw, immutable audit trail; never delete)
                          → import_records (status: Pending)
ImportProcessorService (BackgroundService, polls every 30s, batch of 10 Pending)
   → Processing → parse payload["data"] → Completed(count) | Failed(error)
   → [TODO] match anime via Jikan/MAL, upsert `anime`, write `watch_events`
   → [TODO] update projection tables (user_genre_stats, anime_stats, user_dna)
```
`Workers/ImportProcessor.fs` currently only counts items and does not create watch events or anime rows yet. Raw imports are kept so they can be reprocessed when the normalization logic improves.

**Data access** is raw Npgsql with hand-written SQL and `AddWithValue` parameters. There is no ORM and no repository layer. `DbConfig` is registered as a DI singleton. The whole schema is a single `CREATE TABLE IF NOT EXISTS` script in `Infrastructure/Database.fs` (`initializeSchema`), which runs at startup. There are no migrations, so schema changes go into that script; `SCHEMA.md` documents the intended design. Note that `cr_imports.user_id` is a foreign key to `users`, so `POST /imports` fails unless that user row already exists.

**Domain types** (`Domains/*.fs`: Identity, Catalog, Tracking, Analytics) are plain records that mirror the tables, but the handlers and worker don't use them yet. They work with tuples and strings directly; for example, the import status is stored as the strings `'Pending'|'Processing'|'Completed'|'Failed'`, not as the `ImportStatus` DU. `Types.fs` models the Crunchyroll watch-history JSON shape (`PostHistoryTo`); see `JsonSamples/PostHistory.json` and `.private/test.http` for real payloads.

**Design references**: `design/` holds HTML/PNG mockups for the dashboard and the "anime DNA" profile, plus `design/neo_scrobble_profile/DESIGN.md` (color and typography tokens for the intended visual style).
