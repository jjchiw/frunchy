# Frunchy Backend - Anime Watch History Platform

## Vision

**Build the anime identity of the user independently of Crunchyroll.**

A Last.fm-style platform for anime focused on automatic watch history, statistics, and community. The most valuable asset is the accumulated watch history - the anime DNA that no competitor can easily reconstruct once users start using the platform.

## Architecture

### Core Principle
**Watch History is the foundation**
```
Watch History
    ↓
Metadata (Anime Catalog)
    ↓
Analytics (Statistics & Projections)
    ↓
Social (Future: Community features)
```

### Code Organization: Vertical Slices

Each endpoint is a self-contained slice in `Handlers/`, laid out top to bottom as:
**view** (or the JSON request/response contract) → **data access** (the slice's own SQL) → **handler** → **endpoint** (route + verb).

```
Handlers/
├── Home.fs                      GET  /
├── Users/Dashboard.fs           GET  /u/{username}          public dashboard
└── Api/
    ├── CreateImport.fs          POST /api/imports           submit history pages
    ├── GetImportStatus.fs       GET  /api/imports/{id}      submission status
    └── WhoAmI.fs                GET  /api/me                who a sync key belongs to
Operations/                      operator CLI commands, one file each (user-create, user-rebuild, …)
Workers/ImportProcessor.fs       background worker: Postgres as the job queue
Infrastructure/                  schema bootstrap, Crunchyroll page parser, auth, API errors
Views/Texts.fs, Views/Layout.fs  typed en/es UI text and the shared layout
Program.fs                       composition root: DI, middleware, endpoints, CLI dispatch
```

Adding an endpoint = new file in `Handlers/`, add it to `FrunchyBackend.fsproj` (before `Program.fs`; F# compile order matters) and add its `endpoint` to the list in `Program.fs`.

### Domain Separation

#### 1. **Identity Domain** (`Domains/Identity.fs`)
- `User` - Core user identity
- `UserProfile` - Display name, bio, avatar
- `UserConfig` - Sync preferences

#### 2. **Tracking Domain** (`Domains/Tracking.fs`) - **THE CORE**
- `CrunchyrollImport` - Raw import from extension (NEVER DELETE - audit trail)
- `WatchEvent` - Normalized watch event (derived from import)
- `ImportRecord` - Processing status tracker

#### 3. **Catalog Domain** (`Domains/Catalog.fs`)
- `Anime` - Anime entry with JSONB metadata
- `Genre`, `Studio`, `Season` - Supporting data

#### 4. **Analytics Domain** (`Domains/Analytics.fs`) - Projections
- `UserGenreStats` - Genre consumption statistics
- `AnimeStats` - Per-anime consumption
- `UserDNA` - User's anime profile

### Data Flow

```
Extension (Crunchyroll history pages, verbatim)
    ↓
POST /api/imports  (Bearer sync key; history owner checked; 202 Accepted)
    ↓
cr_imports + cr_import_pages   (raw pages stored exactly as received, never modified)
    ↓
ImportProcessorService  (polls; claims with FOR UPDATE SKIP LOCKED)
    ├─ parse each entry: processed / unresolved / unsupported / failed
    ├─ upsert shared catalog: cr_series, cr_seasons, cr_episodes
    └─ upsert watch_events  (one per user + episode, newest watch wins)
    ↓
GET /u/{username}  (figures computed from watch_events at request time)
```

## Database Schema

- **Identity**: `users` (username, display name, SHA-256 of the sync key, bound Crunchyroll history owner)
- **Raw**: `cr_imports` (submissions + status/counts), `cr_import_pages` (verbatim `json` pages), `cr_import_entry_issues`
- **Catalog** (shared): `cr_series`, `cr_seasons`, `cr_episodes` (no `duration_ms` = unresolved)
- **Derived**: `watch_events`; the dashboard figures are computed from it, with no projection tables yet

See `SCHEMA.md` for full details.

## API Endpoints

The full contract, shared with the browser extension, is in
[`specs/001-crunchyroll-history-import/contracts/import-api.md`](specs/001-crunchyroll-history-import/contracts/import-api.md).
Every call needs `Authorization: Bearer <syncKey>`.

| Endpoint | What |
|---|---|
| `POST /api/imports` | `{ "pages": [ { "url", "body", "capturedAt" } ] }`, Crunchyroll pages verbatim (1–100 per request, ≤ 30 MB) → `202 { importId, status, statusUrl, entriesReceived }` |
| `GET /api/imports/{id}` | `{ status: pending\|processing\|completed\|failed, counts, issues[] }` (owner only) |
| `GET /api/me` | `{ username, displayName, dashboardUrl, lastSyncAt, historyOwnerBound }` |

Errors are `{ "error": "<code>", "message": "…" }` with codes `unauthorized` 401, `invalid_submission` /
`history_owner_missing` 400, `history_owner_mismatch` / `history_owner_taken` 409, `payload_too_large` 413,
`not_found` 404, `server_error` 500.

### HTML Pages
- `GET /`: home, explains how to sync
- `GET /u/{username}`: public dashboard (series, episodes, hours, days, recent episodes), English or Spanish
  from the browser's `Accept-Language`

## Tech Stack

- **Language:** F#
- **Web Framework:** [Oxpecker](https://lanayx.github.io/Oxpecker/src/Oxpecker/) (F# on ASP.NET Core endpoint routing)
- **Views:** Oxpecker.ViewEngine (server-rendered HTML), HTMX 4 + Alpine.js, Tailwind + DaisyUI via CDN (see `FRONTEND.md`)
- **Database:** PostgreSQL with Npgsql
- **Background Jobs:** .NET BackgroundService
- **External API:** Jikan (MyAnimeList data)

## Getting Started

### Prerequisites
- .NET SDK 10
- PostgreSQL 14+ (locally it runs in Docker: container `postgres-postgres-1`)
- Docker running, for the tests (Testcontainers)

### Setup

1. **Database**: `appsettings.json` → `ConnectionStrings:Database` (default
   `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=frunchydb`), or override with the
   `ConnectionStrings__Database` environment variable. The schema is created on startup.

   Databases from before feature 001 have a legacy placeholder layout, which the app refuses. Reset once
   (there's no local `psql`, so run it inside the Postgres container, or use DBeaver):
   ```bash
   docker exec postgres-postgres-1 dropdb -U postgres frunchydb
   docker exec postgres-postgres-1 createdb -U postgres frunchydb
   ```

2. **Create a user**, which prints their sync key once (paste it in the extension):
   ```bash
   dotnet run -- user-create animenerd "AnimeNerd"
   ```

3. **Run** on `http://localhost:5099` (the URL the extension uses in development):
   ```bash
   dotnet run --launch-profile http
   ```

4. **Test** from the repo root (`FrunchyBackend.slnx`):
   ```bash
   dotnet test
   ```

### Operator commands

`dotnet run -- <command>`: `user-create <username> <displayName>`, `user-regenerate-key <username>`,
`user-reset-binding <username>`, `user-rebuild <username>`, `user-delete <username> --yes`, `user-list`.
Details are in [`contracts/operator-cli.md`](specs/001-crunchyroll-history-import/contracts/operator-cli.md).

## What's NOT Included (MVP)

- ❌ Wolverine / Event Sourcing
- ❌ Message queues (Kafka, RabbitMQ)
- ❌ CQRS (formal pattern)
- ❌ Microservices
- ❌ Manual anime lists
- ❌ User ratings
- ❌ Social features (for now)

**Why?** Keep it boring and simple until we discover the interesting analytics that users actually want to see. That will drive the architecture evolution.

## Roadmap

Features are built through spec-kit (`specs/###-name/`), in this order:

| # | Feature | Status |
|---|---|---|
| 001 | **Crunchyroll history import + real dashboard**: raw import, normalization to watch events, import status, public per-user dashboard (`/u/{username}`), sync-key identity bound to the user's Crunchyroll account, English/Spanish UI | Implemented ([spec](specs/001-crunchyroll-history-import/spec.md)) |
| 002 | **Accounts**: sign in through an external provider (Discord / Google / AniList, decided in its spec), self-service sign-up, sync key and account deletion. **Required before inviting a second user.** | Planned |
| 003 | **MAL enrichment**: match Crunchyroll seasons to MyAnimeList via Jikan (rate-limited, per distinct anime): genres, studios, cover art, scores | Planned |
| 004 | **Anime DNA profile**: genre distribution, favourite studio, dominant decade, viewing pace, insignia anime | Planned |


## Key Design Decisions

| Decision | Reasoning |
|----------|-----------|
| Store raw imports | Audit trail. Allows reprocessing with new logic |
| JSONB for anime.metadata | Flexibility for varying anime data structures |
| Projection tables for stats | Pre-computed analytics for fast dashboard queries |
| Background service over message queue | No external dependencies for MVP |
| Separate watch_events table | Core domain asset, indexed for fast queries |
| Vertical slices per endpoint | View, SQL and route live together; features change in one file |

## Team Philosophy

"Design the system around the product, not the product around the system."

Start with boring, proven technology. Let usage patterns guide architecture evolution.

