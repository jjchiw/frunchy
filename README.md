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
│   ├── Users/Dna.fs                 GET  /u/{username}/dna      Anime DNA (feature 004)
│   ├── Users/Year.fs                GET  /u/{username}/year[/{year}]  year in review (feature 005)
│   ├── Users/Anime.fs               GET  /u/{username}/anime/{seriesId}  one series (feature 006)
│   ├── Users/Compare.fs             GET  /u/{username}/compare  taste compatibility, signed in (feature 011)
├── Charts/Charts.fs             GET  /charts, /charts/season, /charts/all  what Frunchy is watching (feature 012)
├── Series/Series.fs             GET  /anime/{seriesId}                     public series page (feature 013)
├── Review/MalReview.fs          GET  /review/mal[/manual], POST /review/mal/{set,none,retry,auto}  operators only (feature 007)
├── Review/Operators.fs          GET  /review/operators, POST /review/operators/{add,remove}  root operator only (feature 008)
├── Episodes/                    GET  /anime/{seriesId}/episode/{episodeId}, /comments/{id}/edit, POST comments, edit, delete, hide, unhide  (feature 009)
├── Auth/                        /signin, /signin/{provider}, /signin/complete, /signup, /claim, /signin/reconfirm, /signout
├── Account/                     /account, /account/key, /account/display-name, /account/binding/reset,
│                                /account/link|unlink/{provider}, /account/delete   (signed-in owner only)
└── Api/
    ├── CreateImport.fs          POST /api/imports           submit history pages
    ├── GetImportStatus.fs       GET  /api/imports/{id}      submission status
    └── WhoAmI.fs                GET  /api/me                who a sync key belongs to
Operations/                      operator CLI commands, one file each (user-create, user-rebuild, …)
Workers/ImportProcessor.fs       background worker: Postgres as the job queue
Infrastructure/                  schema bootstrap, Crunchyroll page parser, sync-key auth, API errors,
                                 WebAuth (browser sessions), AuthConfig (providers), AccountActions (shared by CLI and web)
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
| `POST /api/imports` | `{ "profileId", "pages": [ { "url", "body", "capturedAt" } ] }`, Crunchyroll pages verbatim (1–100 per request, ≤ 30 MB) → `202 { importId, status, statusUrl, entriesReceived }` |
| `GET /api/imports/{id}` | `{ status: pending\|processing\|completed\|failed, counts, issues[] }` (owner only) |
| `GET /api/me` | `{ username, displayName, dashboardUrl, lastSyncAt, historyOwnerBound }` |

Errors are `{ "error": "<code>", "message": "…" }` with codes `unauthorized` 401, `invalid_submission` /
`history_owner_missing` 400, `history_owner_mismatch` / `history_owner_taken` 409, `payload_too_large` 413,
`not_found` 404, `server_error` 500.

### HTML Pages
- `GET /`: home, explains how to sync
- `GET /u/{username}`: public dashboard (series, episodes, hours, days, recent episodes), English or Spanish
- `GET /u/{username}/dna`: the Anime DNA (favourite studio, dominant decade, genres and formats by watch time, insignia anime, 90-day viewing pace, totals), from 15 fully watched episodes; every MyAnimeList figure states its coverage
- `GET /u/{username}/year/{year}`: the year in review (hours, episodes, series, new versus continued, month by month, most watched series, genres, top 3 studios, formats, longest streak) for one UTC calendar year, with a picker of the years that have history; `/u/{username}/year` goes to the newest one
- `GET /u/{username}/anime/{seriesId}`: one series as that person watched it (episodes, hours, first and latest watch, seasons with their MyAnimeList details, the episodes watched); every series title on the dashboard, DNA and year pages links to it
  from the browser's `Accept-Language`
- `GET /review/mal` and `/review/mal/manual`, with `POST /review/mal/set|none|retry|auto` (feature 007): operators review the MyAnimeList matches the matcher couldn't decide (pick a candidate, type an id or link, mark the season as not listed, ask for a retry) and send manual decisions back to automatic. Operators are the root operator (the username in `Frunchy:RootOperator`) plus the users the root added; everyone else, signed in or not, gets the plain 404 page. The account page says "You're an operator" and links to it for operators.
- `GET /review/operators`, with `POST /review/operators/add|remove` (feature 008): the root operator makes existing users operators and takes them back, from the web. Everyone else, operators included, gets the plain 404 page. Changes need a sign-in within the last 10 minutes and are limited to 10 a minute; each one is a security event on the root's account and on the affected user's account. The account page links to it for the root.
- `GET /anime/{seriesId}/episode/{episodeId}`, with `POST …/comments`, `GET/POST /comments/{id}/edit`, `POST /comments/{id}/delete` and `POST /comments/{id}/hide|unhide` (feature 009): the public page of one episode with its comment thread. Any signed-in user can comment (spoilers start ticked and stay closed until opened), edit and delete their own comments; operators hide and unhide any comment (hiding applies on Frunchy only). Comment writes are limited to 10 a minute per IP.
- Accounts (feature 002): `/signin`, `/signup`, `/claim` and the private `/account` pages. See
  [Accounts](#accounts) and [`contracts/web-account.md`](specs/002-social-login-accounts/contracts/web-account.md).

## Accounts

- **Sign in** with Discord or Google. Frunchy never stores passwords, emails or avatars, only "provider +
  that provider's stable user id".
- **Sign-up is invite-only**: the operator runs `dotnet run -- invite-create`, which prints a one-time code
  valid for 7 days. The first sign-in asks for the code, a username (the dashboard address, fixed) and a
  display name.
- **Existing accounts** created by the operator are claimed once: sign in, choose "I already have an
  account" and paste the account's current sync key. This works only while the account has no sign-in
  method yet.
- **`/account`**: rename the display name, "Connect extension" (a new sync key shown once; the old one
  stops working), reset the Crunchyroll binding, link or unlink Discord/Google (never the last one), see
  the last 10 syncs and the last 20 security events, and delete the account. A new key and deletion need
  a sign-in within the last 10 minutes; otherwise you re-confirm with your provider.
- The extension keeps using the sync key exactly as before (import contract v1.2).

## Tech Stack

- **Language:** F#
- **Web Framework:** [Oxpecker](https://lanayx.github.io/Oxpecker/src/Oxpecker/) (F# on ASP.NET Core endpoint routing)
- **Views:** Oxpecker.ViewEngine (server-rendered HTML), HTMX 4 + Alpine.js, Tailwind via CDN on the shared design system: tokens in `Views/Theme.fs` (the same names and values as the browser extension), components in `Views/Components.fs` (see `FRONTEND.md`)
- **Database:** PostgreSQL with Npgsql
- **Background Jobs:** .NET BackgroundService
- **External API:** MyAnimeList API v2 (official, client id in Bella), or Jikan as an alternative

## Getting Started

### Prerequisites
- .NET SDK 10
- PostgreSQL 14+ (locally it runs in Docker: container `postgres-postgres-1`)
- Docker running, for the tests (Testcontainers)

### Setup

1. **Database**: `appsettings.json` → `ConnectionStrings:Database` (default
   `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=frunchydb`), or override with the
   `ConnectionStrings__Database` environment variable. The schema is created on startup.

   A database created by an older version is not migrated: drop it and let the app create it again
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

### Sign-in providers (optional locally; the tests use a fake provider)

1. Register a free OAuth app. **Discord**: Developer Portal → OAuth2, redirect
   `http://localhost:5099/signin-discord`. **Google**: Cloud Console → Credentials → OAuth client ID (Web),
   redirect `http://localhost:5099/signin-google`. **Umbral** (our self-hosted OpenID Connect provider, run
   from its own repo on `http://localhost:5173`): admin UI → Apps, redirect `http://localhost:5099/signin-umbral`;
   its `Authority` is already in `appsettings.json`.
2. Store the credentials **outside the repo**. Never put them in `appsettings*.json` (a test enforces this).
   - **Bella Baxter** (our own secrets manager, recommended). In the Bella project `Frunchy`, environment
     `development`, add the secrets with `__` as the section separator:
     `Frunchy__Auth__Providers__Discord__ClientId`, `Frunchy__Auth__Providers__Discord__ClientSecret` and
     `Frunchy__Auth__Providers__Discord__Enabled` = `true`. Then run through the Bella CLI, which hands the
     app its Bella credentials (the app fetches the secrets itself, via `BellaBaxter.AspNet.Configuration`):
     ```bash
     bella sdk run -- dotnet run --launch-profile http
     ```
     Alternatively, store a Bella API key once (`dotnet user-secrets set "BellaBaxter:ApiKey" "bax-…"`) and
     use plain `dotnet run`. Bella values override appsettings and user secrets; environment variables still
     win. Secrets are polled every minute, and a rotated provider credential is used on the next sign-in
     without a restart (enabling or disabling a provider still needs one). Without Bella credentials, Bella
     isn't used at all.
   - **User secrets**:
     ```bash
     dotnet user-secrets set "Frunchy:Auth:Providers:Discord:ClientId" "<id>"
     dotnet user-secrets set "Frunchy:Auth:Providers:Discord:ClientSecret" "<secret>"
     dotnet user-secrets set "Frunchy:Auth:Providers:Discord:Enabled" "true"
     ```
   In production use Bella or environment variables (`Frunchy__Auth__Providers__Discord__ClientSecret`) and
   set `Frunchy:DataProtection:KeysPath` to a persistent directory so sessions survive restarts. The site
   refuses to start if a provider is enabled without its credentials.
3. `dotnet run --launch-profile http` (Development, so user secrets are loaded) and open `/signin`.

### Operator commands

`dotnet run -- <command>`:
- **Users**: `user-create <username> <displayName>`, `user-regenerate-key <username>`,
  `user-reset-binding <username>`, `user-rebuild <username>`, `user-delete <username> --yes`, `user-list`.
- **Invites** (feature 002): `invite-create [--days N]`, `invite-list`, `invite-revoke <id>`.
- **Operators** (feature 008): `operator-add <username>`, `operator-remove <username>`, `operator-list`. They work without a root configured, so they are the way back in if the root can't sign in.
- **MyAnimeList enrichment** (feature 003): `mal-status`, `mal-list [--status S] [--limit N]`,
  `mal-show <crSeriesId>`, `mal-set <crSeasonId> <malId>`, `mal-none <crSeasonId>`,
  `mal-retry <crSeasonId | crSeriesId>`, `mal-auto <crSeasonId>` (feature 007: back to automatic), `mal-rebuild` (no requests).

Details are in [`001 contracts/operator-cli.md`](specs/001-crunchyroll-history-import/contracts/operator-cli.md),
[`002 contracts/operator-cli.md`](specs/002-social-login-accounts/contracts/operator-cli.md)
[`003 contracts/operator-cli.md`](specs/003-mal-enrichment/contracts/operator-cli.md)
and [`008 contracts/operators.md`](specs/008-root-operator/contracts/operators.md).

**Root operator** (feature 008): set `Frunchy:RootOperator` to one username (`Frunchy__RootOperator` in Bella or the environment; read on every request, case-insensitive, empty or missing means no root). The root is configuration only, never a row, and can't be added or removed. Operators the root adds live in the `operators` table. `Frunchy:Operators` (feature 007) is retired and grants nothing.

### MyAnimeList enrichment (feature 003)

A background worker matches every Crunchyroll season to MyAnimeList through
MyAnimeList's official API v2 and stores the details (genres, studios, format, year, score) in
shared catalog tables. Imports never wait for it, and page views never call MyAnimeList. The public
dashboard then shows score and genres on the recent cards, plus a "Top genres" section.

- **Client id**: register an app at https://myanimelist.net/apiconfig (type `web`, redirect
  `http://localhost:5099/`; only the Client ID is used). Store it in Bella as
  `Frunchy__Mal__ClientId`, or with `dotnet user-secrets set "Frunchy:Mal:ClientId" "…"`, never in
  appsettings. Without it the site runs and enrichment stays off, with a warning in the log.
  `Frunchy:Mal:Provider=jikan` switches to Jikan, which needs no credentials.

- **Pace and retries**: at most 1 request per second, retries included. Temporary errors (429
  honouring `Retry-After`, 5xx, timeouts) are retried 3 times. After that the season waits
  1 min, 2, 4… up to 6 h. After 5 failed lookups in a row the worker pauses 5 minutes, then sends one
  probe. Nothing is lost on restart: the queue is Postgres.
- **Uncertain matches** are recorded as `ambiguous` (with candidates) or `no_match`, never guessed.
  Fix them with `mal-list` and `mal-set` / `mal-none`, or on the web at `/review/mal` (operators, see
  `Frunchy:RootOperator`); manual decisions are never overwritten, and `mal-auto` hands one back to the matcher.
- **Settings** (`appsettings.json`, no secrets): `Frunchy:EnrichmentWorkerEnabled`,
  `Frunchy:Mal:{Provider, BaseUrl, RequestsPerSecond, RetryBaseDelay, AttemptTimeout}` (plus the
  `ClientId` secret),
  `Frunchy:Enrichment:{PauseAfterFailures, PauseFor, RefreshAfterDays, RefreshAiringAfterDays}`,
  `Frunchy:Charts:MinPeople` (feature 012, optional: the fewest people a chart row or "Watched by" count needs; default 3, never below 2).

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
| 002 | **Accounts**: sign in through an external provider (Discord / Google / AniList, decided in its spec), self-service sign-up, sync key and account deletion. **Required before inviting a second user.** | Implemented ([spec](specs/002-social-login-accounts/spec.md)) |
| 003 | **MAL enrichment**: match Crunchyroll seasons to MyAnimeList through its official API (paced, retried, per distinct anime): genres, studios, format, scores; score, genres and "Top genres" on the dashboard; operator review commands | Implemented ([spec](specs/003-mal-enrichment/spec.md)) |
| 004 | **Anime DNA profile + shared design system**: genre distribution, favourite studio, dominant decade, viewing pace, insignia anime; the whole site on the "Neo-Scrobble Profile" design system shared with the extension | Implemented ([spec](specs/004-anime-dna-design-system/spec.md)) |
| 005 | **Year in review**: one calendar year of a profile (totals, months, most watched series, genres, studios, formats, new versus continued, longest streak), linked from the dashboard and the DNA page | Implemented ([spec](specs/005-year-in-review/year-in-review.spec.md)) |
| 006 | **Anime page**: one series as a profile watched it (progress, seasons with MyAnimeList details, episodes), linked from every series title | Implemented ([spec](specs/006-anime-page/anime-page.spec.md)) |
| 007 | **MyAnimeList review**: operators fix and undo MyAnimeList matches on the web (`/review/mal`), through the same actions as the CLI; everyone else gets a 404 | Implemented ([spec](specs/007-mal-review/mal-review.spec.md)) |
| 008 | **Root operator**: one root operator (`Frunchy:RootOperator`) adds and removes operators on the web (`/review/operators`) or with `operator-add` / `operator-remove`; operators live in a table, security events on both accounts | Implemented ([spec](specs/008-root-operator/root-operator.spec.md)) |
| 009 | **Episode page and comments**: a public page per catalog episode (`/anime/{seriesId}/episode/{episodeId}`) with its details, Crunchyroll and MyAnimeList links and a comment thread; signed-in users comment (spoilers behind a toggle), edit and delete their own, operators hide and unhide; the table is shaped for the fediverse integration in [`docs/fediverse.md`](docs/fediverse.md) | Implemented ([spec](specs/009-episode-comments/episode-comments.spec.md)) |
| 010 | **Night Broadcast design system**: the whole site restyled (warm dark surfaces, amber for you, rose for others, Bricolage Grotesque / Figtree / IBM Plex Mono), same token names, shared with the extension through a new contract | Implemented ([spec](specs/010-night-broadcast/night-broadcast.spec.md)); the extension follows in its own session |
| 011 | **Taste compatibility**: a signed-in viewer sees how their taste matches someone else's, as a level in words (never a percentage), on a card on that dashboard and on `/u/{username}/compare` (genre shares side by side, shared series, shared studios); both need 15 fully watched episodes | Implemented ([spec](specs/011-taste-compatibility/taste-compatibility.spec.md)) |
| 012 | **Charts and trends**: the most-watched series this week, this season and of all time (`/charts`), ranked by people, with movement and an 8-week trend on the week chart, and "Watched by N people on Frunchy" on anime and episode pages; nothing shows below `Frunchy:Charts:MinPeople` people (default 3) | Implemented ([spec](specs/012-charts-trends/charts-trends.spec.md)) |
| 013 | **Series page**: a public page per series (`/anime/{seriesId}`) with its seasons, MyAnimeList details, known episodes with their comment counts and "Watched by N people"; chart rows, the episode page and the anime page link to it; new usernames can no longer end in a dash (Mastodon refuses them) | Implemented ([spec](specs/013-series-page/series-page.spec.md)) |


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

