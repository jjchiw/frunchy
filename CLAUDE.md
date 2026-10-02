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
- Operator CLI: `user-create`, `user-regenerate-key`, `user-reset-binding`, `user-rebuild`, `user-delete --yes`, `user-list`, `invite-create [--days N]`, `invite-list`, `invite-revoke <id>`, and for MyAnimeList `mal-status`, `mal-list`, `mal-show`, `mal-set`, `mal-none`, `mal-retry`, `mal-rebuild` (`contracts/operator-cli.md` in specs 001, 002 and 003).
- **Provider credentials** (Discord/Google) go **only** in Bella Baxter (our own secrets manager, loaded by `BellaBaxter.AspNet.Configuration` in `Program.addBellaSecrets` when Bella credentials are present; run with `bella sdk run -- dotnet run --launch-profile http`; keys use `__`, e.g. `Frunchy__Auth__Providers__Discord__ClientSecret`), `dotnet user-secrets` (dev; loaded in the Development environment) or environment variables. Never put them in `appsettings*.json`: a test fails if any committed appsettings file has a non-empty `ClientId`/`ClientSecret`, and the site refuses to start with an enabled provider missing them.
- `JsonSamples/PostHistory.json` (2022, one raw page) and `JsonSamples/crunchyroll-history-2026-sample.json` (2026, 4 pages in the import envelope) are the test fixtures. Full personal captures (`crunchyroll-history-YYYY-MM-DD.json`) are gitignored and must never be committed. `.private/test.http` is stale (old `/post-history` route).

## The browser extension

`../browser-extension` is built by **its own Claude session**. Never edit that repo. `specs/001-crunchyroll-history-import/contracts/import-api.md` is the shared interface. Send it any change with SendMessage (find the session with ListAgents by its cwd), and include that session in the plan, tasks and implementation of any feature touching the contract.

## Architecture

**Load the `oxpecker` skill** (`.claude/skills/oxpecker/`) before touching routes, handlers, views, htmx or Alpine. It covers the API, the upstream examples, and which documented features the pinned package versions **don't** have (the upstream docs track a newer `develop` branch).

**Vertical slices: one file per endpoint in `Handlers/`.** Each slice is laid out top to bottom as: view (`HtmlElement` functions piped into `Views.Layout.page texts title`; for JSON endpoints this section holds the request/response records, whose field names *are* the camelCase wire names) → `private` data-access functions with the slice's own SQL → `handler` → `let endpoint = GET [ route "/path" handler ]` with an explicit verb. Templates: `Handlers/Users/Dashboard.fs` (HTML) and `Handlers/Api/CreateImport.fs` (JSON). Operator commands follow the same idea, one file each in `Operations/`. `Program.fs` is only the composition root: `configureServices`, `configureApp` (localization, error/404 handlers), `endpoints`, CLI dispatch, and `buildApp`, which the tests reuse on a TestServer.

**F# compile order matters.** Files compile in the order listed in `FrunchyBackend.fsproj`: Domains → Infrastructure → Views → Handlers → Operations → Workers → Program. Add each new file in its place.

**Routes**:
- `GET /` (home) and `GET /u/{username}`: the public dashboard. Figures are computed by SQL at request time from `watch_events`.
- Browser sign-in (feature 002, `Handlers/Auth/`): `/signin`, `/signin/{provider}`, `/signin/complete`, `/signup`, `/claim`, `/signin/reconfirm`, `POST /signout`. Private owner pages (`Handlers/Account/`): `/account`, `/account/key`, `/account/display-name`, `/account/binding/reset`, `/account/link|unlink/{provider}`, `/account/delete`.
- `POST /api/imports`, `GET /api/imports/{id}`, `GET /api/me`: JSON for the extension. They're authenticated by `Authorization: Bearer <syncKey>` (`Infrastructure/Auth.fs`; only a SHA-256 of the key is stored) and return errors as `{ error, message }` (`Infrastructure/ApiErrors.fs`).

**Ingestion pipeline** (the core flow):
```
extension → POST /api/imports → validate envelope { profileId, pages: [ { url, body, capturedAt } ] }
          → bind/verify the Crunchyroll history owner = (account id from the page URL path, required profileId);
            the URL id is shared by every profile of an account, so profileId keeps profiles apart
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

**Browser auth (feature 002)** uses ASP.NET Core's own handlers: Google (first-party) and Discord (`AspNet.Security.OAuth.Discord`). No identity server. `Infrastructure/WebAuth.fs` holds two cookie schemes:
- `frunchy.external`: 10 minutes, carries `(provider, provider user id)` from the provider callback to `/signin/complete`, which acts in one of 3 modes (signin, link, reconfirm, passed in the challenge properties);
- `frunchy.session`: 30 days sliding, with an `auth_time` claim. Generating a key and deleting the account use `requireFresh`, which needs a sign-in within 10 minutes; otherwise `/signin/reconfirm` sends the user back to the same provider.

More rules:
- **Forms**: every state-changing POST uses `WebAuth.readValidatedForm`, which checks the antiforgery verdict of `UseAntiforgery` first.
- **Rate limits**: per IP, via `RequireRateLimiting` (`signin`, `signup`, `claim`, `account-write`).
- **Sign-up** is invite-only (`invites`, consumed in the same transaction that creates the user).
- **Claiming**: operator-created accounts are claimed with their sync key, only while they have no `user_identities` row.
- **One identity per provider per account.**
- **Security events**: every account action records one in `security_events` (via `Infrastructure/SecurityEvents.record`, in the action's transaction).
- **Shared actions**: `Infrastructure/AccountActions.fs` (regenerate key, reset binding, delete) is shared by the CLI and the web, so they behave identically.

**Data access** is raw Npgsql with hand-written SQL. There's no ORM or repository layer. `DbConfig` is a DI singleton (`ctx.GetService<DbConfig>()`). The schema is the idempotent `initializeSchema` in `Infrastructure/Database.fs`, with no migrations: schema changes go there and into `SCHEMA.md`. Everything personal hangs off `users` with `ON DELETE CASCADE`, and the catalog tables are shared. Raw pages are only ever deleted by `user-delete` (the constitution's single exception).

**Localization**: every user-facing string lives in `Views/Texts.fs` (a typed record with `en` and `es` values). The language comes from `Accept-Language` only. Views never contain literals. ViewEngine writes non-ASCII as `&#NNN;`, so tests decode with `readHtml`. Times are rendered as `<time datetime="…Z" data-fr-time>` with a UTC fallback, and the layout's `Intl` script localizes them in the viewer's time zone.

**MyAnimeList enrichment (feature 003)**: `Workers/Enrichment.fs` matches every `cr_seasons` row to a MyAnimeList entry through Jikan, without touching the import path (a sweep discovers new seasons).
- **Queue**: Postgres. `cr_season_matches` holds one row per season (status, attempts, `next_attempt_at`, claims with `SKIP LOCKED`); `mal_anime.refresh_*` is the refresh queue (30 days, 7 while airing).
- **Client**: `Infrastructure/JikanClient.fs`, a named `HttpClient` "jikan" with a `Microsoft.Extensions.Http.Resilience` pipeline (total timeout → 3 retries honouring `Retry-After` → 1 req/s token bucket → attempt timeout). Register client configuration with an explicit `Action<HttpClient>`: a bare F# lambda resolves to the typed-client overload and is silently ignored.
- **Matching**: `Infrastructure/MalMatching.fs`, pure, deterministic title tiers. The series title finds MyAnimeList's first season (the anchor); other seasons and movies follow its sequel/related links; dubs share their original; no TV sequel at all means every season is the same entry (One Piece, Eyeshield 21). Uncertain → `ambiguous`/`no_match`, never guessed.
- **Data**: Jikan responses are stored verbatim (`mal_anime.raw`, `mal_searches.raw`), so `mal-rebuild` re-derives everything with zero requests. Rows with `method = 'manual'` are never written by automatic code (`MalCatalog`'s guard).
- **Dashboard**: score, 3 genres and a MyAnimeList link on matched recent cards, plus "Top genres" (fully watched episodes, the "Episodes watched" rule).

**Tests** (`tests/FrunchyBackend.Tests`): one Postgres Testcontainer per run (`max_connections=500`) with a fresh database per test (`PostgresFixture.newApp`).
- **Jikan is never called**: `Infrastructure/FakeJikan.fs` replaces the "jikan" client's primary handler (route table, request log, scripted 429/504/delays) with instant pacing. `JsonSamples/jikan/` holds recorded real responses (`record.py`, `manifest.json`); `MalFixtures.fs` builds Jikan-shaped bodies and a scripted catalog for some 2026-sample series. `TestApp.Jikan` is each app's fake; `enrichAll` runs the worker until idle.
- **Fake providers**: `test` and `test2` (`Infrastructure/FakeProvider.fs`) are registered only by the tests. `GET /signin/test?sub=abc` acts like a provider round trip, and `?fail=1` simulates a cancellation.
- **`Browser`** (`Infrastructure/Browser.fs`) keeps cookies, follows redirects with `Get`, and posts forms with their antiforgery token (`PostForm`).
- **Helpers**: `AuthHelpers.signedUp` / `connectExtension` give a signed-in user with a key.
- **Time**: the 10-minute rule is tested with a `TimeProvider` registered via `createAppWith`. The real app runs on a TestServer with the hosted worker disabled, and tests call `ImportProcessor.processNext` directly (`TestHelpers.processAll`). Fixture builders live in `Fixtures.fs` (`syntheticPages`, `envelopeOf`, …).

**Design references**: `design/` holds HTML/PNG mockups (dashboard, anime DNA profile, extension popup) and `design/neo_scrobble_profile/DESIGN.md` (tokens). Mockup elements without a real data source (ratings, ranks, trends) are out of scope until a feature adds the data.
