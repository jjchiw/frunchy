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
├── Dashboard.fs                 GET  /dashboard
├── Profile.fs                   GET  /profile
└── Imports/
    ├── CreateImport.fs          POST /imports
    └── GetImportStatus.fs       GET  /imports/{importId}
Views/Layout.fs                  shared page layout (the only cross-slice view)
Program.fs                       composition root: DI, middleware, list of slice endpoints
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
Extension
    ↓
POST /imports (202 Accepted)
    ↓
cr_imports table (raw, immutable)
    ↓
ImportProcessorService (Background)
    ├─ Parse JSON
    ├─ Normalize
    ├─ Lookup/Create Anime
    └─ Create watch_events
    ↓
watch_events table (core domain)
    ↓
Projections (Analytics)
```

## Database Schema

**Core Tables:**
- `users` - User identity
- `cr_imports` - Raw imports (never delete)
- `watch_events` - Normalized watch events (core asset)
- `anime` - Anime catalog with JSONB metadata
- `import_records` - Processing status

**Projection Tables:**
- `user_genre_stats` - Cached genre statistics
- `anime_stats` - Cached anime statistics
- `user_dna` - User anime profile snapshot

See `SCHEMA.md` for full details.

## API Endpoints

### Import Endpoint
```
POST /imports
Content-Type: application/json

{
  "user_id": "uuid",
  "data": "{...full crunchyroll export json...}"
}

Response: 202 Accepted
{
  "import_id": "uuid",
  "status": "Accepted",
  "message": "Import queued for processing"
}
```

### Check Import Status
```
GET /imports/{importId}

Response: 200 OK
{
  "import_id": "uuid",
  "status": "Pending|Processing|Completed|Failed",
  "processed_watch_events": 42,
  "error_message": null
}

404 Not Found if the id is unknown or not a valid GUID.
```

> `user_id` must already exist in the `users` table (foreign key); there is no sign-up flow yet.

### HTML Pages
- `GET /` - Home
- `GET /dashboard` - Stats and recent watches (placeholder data for now)
- `GET /profile` - Anime DNA profile (placeholder data for now)

## Tech Stack

- **Language:** F#
- **Web Framework:** [Oxpecker](https://lanayx.github.io/Oxpecker/src/Oxpecker/) (F# on ASP.NET Core endpoint routing)
- **Views:** Oxpecker.ViewEngine (server-rendered HTML), HTMX 4 + Alpine.js, Tailwind + DaisyUI via CDN (see `FRONTEND.md`)
- **Database:** PostgreSQL with Npgsql
- **Background Jobs:** .NET BackgroundService
- **External API:** Jikan (MyAnimeList data)

## Getting Started

### Prerequisites
- .NET 10.0+
- PostgreSQL 14+

### Setup

1. **Install dependencies:**
```bash
dotnet restore
```

2. **Configure database** in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Database": "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=frunchydb"
  }
}
```

3. **Run the application:**
```bash
dotnet run
```

The database schema will be automatically initialized on startup. The app listens on `https://localhost:5001` (see `Properties/launchSettings.json`); override with `dotnet run --urls http://localhost:5000`.

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
| 001 | **Crunchyroll history import + real dashboard**: raw import, normalization to watch events, import status, public per-user dashboard (`/u/{username}`), sync-key identity bound to the user's Crunchyroll account, English/Spanish UI | Specified ([spec](specs/001-crunchyroll-history-import/spec.md)) |
| 002 | **Accounts**: sign in through an external provider (Discord / Google / AniList, decided in its spec), self-service sign-up, sync key and account deletion. **Required before inviting a second user.** | Planned |
| 003 | **MAL enrichment**: match Crunchyroll seasons to MyAnimeList via Jikan (rate-limited, per distinct anime): genres, studios, cover art, scores | Planned |
| 004 | **Anime DNA profile**: genre distribution, favourite studio, dominant decade, viewing pace, insignia anime | Planned |

Already in place: domain models, schema bootstrap, the raw import endpoint and a background processor skeleton.

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

