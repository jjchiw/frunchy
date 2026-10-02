# Research: Crunchyroll Watch-History Import with Real Dashboard

**Feature**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md) · **Date**: 2026-10-02

Each decision lists what was chosen, why, and the alternatives that were rejected. Facts quoted
about Crunchyroll data come from the two fixtures (`JsonSamples/PostHistory.json`, 2022, and
`JsonSamples/crunchyroll-history-2026-sample.json`) and from the full local capture of
2026-10-02 (64 pages, 6,363 entries, gitignored).

## R1. Testing stack

- **Decision**: xUnit v3 test project `tests/FrunchyBackend.Tests`, with
  `Testcontainers.PostgreSql` for a real PostgreSQL and `Microsoft.AspNetCore.TestHost` for
  in-process HTTP. `Program.fs` exposes the app as composable functions (`configureServices`,
  `configureApp`, `endpoints`), so tests build the same app on a `TestServer` with the
  container's connection string. Worker logic is a plain function (`ImportProcessor.processNext`)
  that tests call directly. The `BackgroundService` is only a loop around it, so no test waits on
  timers. Package versions are the latest stable at implementation time, checked with
  `dotnet test`.
- **Rationale**: constitution V requires a real Postgres and real payloads. In-process TestServer
  plus direct worker calls keeps the tests deterministic and fast. Docker Desktop 29.8 is present
  on the dev machine.
- **Alternatives**: `WebApplicationFactory<Program>` (F# entry-point friction; its host-factory
  interception stops `main` right after `Build()`, which would skip our schema bootstrap); Expecto
  (fine, but weaker IDE and `dotnet test` integration); a shared local Postgres (non-hermetic).

## R2. Storing raw submissions "exactly as received"

- **Decision**: one row per page in `cr_import_pages`. The page `body` goes in a PostgreSQL
  **`json`** column holding the exact text of that page's JSON, taken from the request with
  `JsonElement.GetRawText()`. The page `url` and optional `capturedAt` are stored next to it.
  Submission-level metadata goes in `cr_imports`.
- **Rationale**: `json` stores the text verbatim (it only validates it). `jsonb` reorders keys,
  drops duplicate keys and normalizes numbers, which would break FR-005 and Principle I.
  Per-page rows also let the worker process and report by page.
- **Alternatives**: `jsonb` blob per submission (the current code; not verbatim); `text` (no
  validation); storing the whole envelope text (keeps it verbatim, but every read has to re-split
  it by page).

## R3. Submission envelope and size limits

- **Decision**: adopt the extension's existing export shape:
  `{ "exportedAt"?: ISO, "profileId"?: string, "pages": [ { "url": string, "body": <Crunchyroll
  page>, "capturedAt"?: ISO } ] }`. Live syncs send 1 page per request; a full export
  (64 pages ≈ 26 MB compact JSON) fits in a single request. Limits: request body ≤ 30,000,000 bytes
  (Kestrel's default `MaxRequestBodySize`), 1–100 pages per submission, **no cap on entries per
  page**. Crunchyroll pages are 100–101 entries today, about 62–495 KB compact each.
- **Rationale**: one shape covers both live sync and export upload, and the extension already
  produces it. The default body limit is enough for a real five-year history in one request.
- **Alternatives**: the old `{ user_id, data: "<json string>" }` (double-encoded and loses the URL);
  one page per request only (forbids export upload for no gain).

## R4. Sync key format, storage and transport

- **Decision**: key = `frk_` + base64url(32 random bytes from `RandomNumberGenerator`). Only
  `SHA-256(key)` (hex) is stored, in `users.sync_key_hash` with a unique index; lookup is by hash.
  Transport: `Authorization: Bearer <key>`. The operator CLI prints a key exactly once (create and
  regenerate). Regenerating replaces the hash, which revokes the old key immediately.
- **Rationale**: a 256-bit random secret doesn't need a slow password hash. SHA-256 lookups are
  constant-cost and leak nothing if the database leaks. Bearer is the standard header, and the
  extension can set it from its service worker (it confirmed this).
- **Alternatives**: bcrypt/argon2 (built for low-entropy passwords, and slow per request); a
  custom `X-Frunchy-Key` header (non-standard); keys in query strings (they end up in logs).

## R5. Crunchyroll history owner

- **Decision**: parse the account id from each page URL path,
  `/content/v2/{accountId}/watch-history` (absolute or relative URL, query ignored). All pages of a
  submission must share it. History owner = `(accountId, profileId?)`, where `profileId` is the
  optional envelope field. A user is bound on their first accepted submission
  (`users.cr_account_id`, `users.cr_profile_id`), with a unique index across users on
  `(cr_account_id, coalesce(cr_profile_id, ''))`.
- **Rationale**: the URL is the only owner signal that's always present (`meta.next_page` is empty
  on the last page). The same account id appears in the 2022 and 2026 captures, so it's stable for
  years.
- **OPEN (blocks the binding tasks only)**: whether the path id is per account or per profile.
  Community docs call it `account_id`, and profile switching happens through the auth token
  (`grant_type=refresh_token_profile_id`), so the profile probably isn't in the URL. The extension
  session was asked for a capture under two profiles plus the ids from `/accounts/v1/me` and
  `/accounts/v1/me/multiprofile`. Outcomes:
  - if the path id changes per profile, `profileId` stays unused and the owner is the path id;
  - otherwise the extension sends `profileId` (from wherever the capture shows it's readable) and
    the backend requires it.

  Either way the stored shape is the same. Tasks put a gate before the binding implementation.

  **DECIDED 2026-10-02 (T027)**: a second-profile capture of the same account
  (`JsonSamples/other_profile_crunchyroll-history-2026-10-02.json`, gitignored: 1 page, 20 entries,
  3 of them also in the main profile's history) uses the **same** path id `3b6dd336-…`, and its
  payload has no profile field anywhere. The URL id is account-level, so **`profileId` is
  required** on every submission (400 `history_owner_missing` without it). The owner is
  `(accountId, profileId)`. Without this, a second profile's sync would be merged into the first
  user's history, and the 3 shared episodes would be resolved by "newest wins", corrupting both.
  The extension supplies the active profile id; where it reads it is its own concern (T051 part c).

  **Profile id source (T051 c, reported by the extension session)**: Crunchyroll's own
  `POST /auth/v1/token` response (on page load, refreshed every 300 s) has top-level `profile_id`
  and `account_id`. That is the primary source, since it reflects the active profile.
  `GET /accounts/v1/me/multiprofile` → `profiles[].profile_id` with `is_selected: true` is the
  secondary source. `GET /accounts/v1/me` has only `account_id`. The extension copies just those
  two ids out of the token response and never stores or forwards `access_token`/`refresh_token`. The
  ids are UUIDs and stable across sessions. The **primary profile's id equals the account id**
  (`3b6dd336-…`), which is why the URL id looked like a profile id; a second profile has its own
  UUID. For the backend this means the primary profile's owner is `(3b6dd336-…, 3b6dd336-…)`, and
  nothing else changes.

  ~~Status 2026-10-02 (T027): no capture yet. The implemented default: `profileId` is
  **optional**. When present it's part of the owner key `(accountId, profileId)`; when absent the
  owner is the account. Binding compares both exactly, so a user bound without a profile gets
  `history_owner_mismatch` if `profileId` later starts being sent. That case needs one
  `user-reset-binding`. Revisit when T051's capture arrives.~~ (superseded by the decision above)

## R6. Background processing (Postgres as the job queue)

- **Decision**: the status lives on `cr_imports` (`pending → processing → completed | failed`).
  The worker loop polls every 2 s when idle and claims the oldest pending import with
  `SELECT … FOR UPDATE SKIP LOCKED`. It marks it `processing` (short transaction), then processes
  all its pages in one transaction: catalog upserts, watch-event upserts, entry issues, counts,
  `completed`. An unexpected error rolls that transaction back and marks the import `failed` with
  the error message. Imports stuck in `processing` for more than 10 minutes (crash) go back to
  `pending` on the next poll.
- **Rationale**: constitution II/IV. The real scale (6,400 entries per first sync, a few syncs per
  day) is tiny; one worker with batched statements (`NpgsqlBatch` per page) processes a 64-page
  history in seconds, well inside SC-002's 10 minutes.
- **Alternatives**: an in-memory `Channel` signal (an optimization nobody needs); Hangfire,
  Wolverine or a broker (forbidden without measured pain); one transaction per entry (slow,
  partial states).

## R7. Order independence and idempotency

- **Decision**:
  - `watch_events` has primary key `(user_id, cr_episode_id)`. The upsert keeps the row with the
    greatest `(watched_at, fully_watched, playhead_s)` using
    `ON CONFLICT … DO UPDATE … WHERE (excluded.…) > (watch_events.…)` (a row comparison).
  - Catalog rows (`cr_series`, `cr_seasons`, `cr_episodes`) record `source_at` = page `capturedAt`,
    falling back to the import's `received_at`, and are only overwritten by data with
    `source_at >=` the stored one.
- **Rationale**: re-sending is a no-op (SC-005). Any arrival order converges on the same state
  (FR-011). Rebuild reproduces the same result (SC-006).
- **Alternatives**: last-writer-wins by arrival (order-dependent); an append-only event log plus
  projection (rewatch support isn't wanted, see clarification Q2).

## R8. Unresolved entries and automatic resolution

- **Decision**: an entry without `panel` still creates a `cr_episodes` row with only `id` and
  `series_id` (from `parent_id`), and a `watch_events` row with the watch facts it does have (date,
  playhead, fully-watched). It's recorded as an `unresolved` issue for that import. An episode
  counts as **resolved** when `duration_ms` is known. All dashboard figures only consider resolved
  episodes. When any later submission brings the panel for that episode, the catalog upsert fills
  in the details and the episode starts counting with the newest watch state already in place
  (FR-008).
- **Rationale**: about 4.6% of real entries have no panel (290 of 6,363, across 19 series).
  Resolution needs no special code path.
- **Classification**: no `panel` → `unresolved`; `panel.type` ≠ `episode` → `unsupported` (no watch
  event); panel present but `duration_ms`, `id` or `date_played` missing or invalid → `failed` with
  reason; otherwise `processed`.

## R9. Series identity

- **Decision**: the series key is the entry's **`parent_id`**. In 10 of 6,073 real entries
  `episode_metadata.series_id` differs from `parent_id`. They are franchise **movies** ("JUJUTSU
  KAISEN 0", "Bungo Stray Dogs - DEAD APPLE", "SPY x FAMILY CODE: White") whose metadata
  `series_id` is the movie's own id. `cr_series.title` is only taken from entries where
  `series_id = parent_id`. The recent-episodes list shows the entry's own `series_title`.
- **Rationale**: `parent_id` is present on every entry, unresolved ones included, and counts
  movies under their franchise. The fixture figures (29 and 67 series) are the same under both
  keys; the full history gives 327 series (`parent_id`) vs 332 (`series_id`).

## R10. Figures and rounding

- **Decision**:
  - episodes = resolved and `fully_watched`;
  - hours = Σ(fully ? `duration_ms` : min(`playhead_s`·1000, `duration_ms`)) / 3.6e6, which also
    caps playheads that run past the duration (150 real cases, ≤ 1.02×);
  - days = hours / 24;
  - all shown with 1 decimal, rounded half away from zero;
  - series = distinct `series_id` among resolved, fully watched episodes;
  - recent = the 6 most recent resolved watch events, partial ones included and marked
    "In progress" / "En curso".

  Figures are computed by SQL at request time, with no projection table.
- **Rationale**: matches SC-004 (29/96/39.3 and 67/298/118.2). With an index on
  `watch_events(user_id, watched_at desc)`, aggregating 10k rows takes milliseconds (SC-007), so a
  cached projection would be complexity without benefit. The 2022 Independent Test expects the
  partial "The Vampire Dies in No Time" ep. 2 as the most recent item, which is why partial watches
  appear in the list.

## R11. Localization

- **Decision**: ASP.NET Core `UseRequestLocalization` with supported cultures `en` (default) and
  `es`, and **only** the `AcceptLanguageHeaderRequestCultureProvider`, so there's no query or
  cookie override until a switcher exists. UI text lives in a typed F# record `Texts` (English and
  Spanish values) in `Views/Texts.fs`. Views take it as a parameter, resolved per request with
  `Texts.forCulture CultureInfo.CurrentUICulture`. `<html lang>` follows the culture.
- **Rationale**: this is the pattern from `.private/i18n/sample.txt`, translated to Oxpecker
  syntax (the sample's snippets are Giraffe-style and don't compile with Oxpecker). The F#
  compiler guarantees that every text exists in both languages (SC-011). Per-request resolution
  localizes full pages and future htmx fragments the same way.
- **Alternatives**: `.resx` with `IStringLocalizer` (stringly-typed keys, no compile-time
  completeness); client-side dictionaries (go against the server-rendered approach).

## R12. Viewer-local times

- **Decision**: the server renders
  `<time datetime="2026-09-30T22:46:02Z" data-fr-time="relative">30 Sep 2026 22:46 UTC</time>`,
  where the fallback text is formatted in the page culture with an explicit "UTC". A small inline
  script in `Views/Layout.fs` replaces the text with
  `Intl.RelativeTimeFormat(document.documentElement.lang, { numeric: "auto" })` plus
  `Intl.DateTimeFormat(lang, { dateStyle: "medium", timeStyle: "short" })`, using the viewer's
  clock and time zone. No Alpine is needed.
- **Rationale**: FR-013a. The page carries the exact instant and stays unambiguous without
  JavaScript. `Intl` is built into all current browsers, so there's nothing to download (no
  frontend build, as the constitution requires).

## R13. Operator actions

- **Decision**: CLI subcommands of the same executable, `dotnet run -- <command> …`:
  `user-create <username> <displayName>`, `user-regenerate-key <username>`,
  `user-reset-binding <username>`, `user-rebuild <username>`, `user-delete <username> --yes`, and
  `user-list`. `Program.fs` only dispatches on `args[0]`; each command is a slice file in
  `Operations/` (the CLI counterpart of `Handlers/`).
- **Rationale**: operator-only by construction, since it needs shell access to the server. There
  are no admin HTTP endpoints, admin credentials or attack surface. Rebuild sets the user's imports
  back to `pending` and lets the normal worker reprocess them.
- **Rebuild details**: in one transaction, delete the user's `watch_events` and their imports'
  `cr_import_entry_issues`, reset those imports to `pending` with counts zeroed. The catalog is
  shared and never deleted; R7 makes reprocessing converge on the same figures.
- **Delete details**: `DELETE FROM users` cascades to imports, pages, issues and watch events. The
  worker holds `FOR UPDATE` on the import it's processing, so a delete waits for it, then removes
  everything, and nothing reappears (US3 scenario 4). Catalog rows hold no personal data and stay.

## R14. Schema transition

- **Decision**: the bootstrap script in `Infrastructure/Database.fs` gets the new tables. The
  legacy layout (users with `email`, `cr_imports.payload jsonb`, `import_records`, MAL catalog and
  analytics projections) has never held real data. The bootstrap **detects** it (e.g.
  `import_records` exists or `users.email` exists) and stops with a clear message to reset the dev
  database (`dropdb frunchydb && createdb frunchydb`), rather than dropping anything itself. The
  MAL and projection tables are removed from the bootstrap and come back with features 003/004.
  `Domains/*.fs` are rewritten for the new model; `Domains/Analytics.fs` and `Types.fs` are
  removed.
- **Rationale**: no migration framework yet (constitution constraint), and nothing real to
  migrate. Failing fast avoids silent half-migrated states.

## R15. Package changes (deliberate, per constitution constraint)

- **Npgsql** 8.0.1 → 10.0.x: fixes advisory GHSA-x9vc-6hfv-hg8c, flagged on every build.
- **Remove** `FSharp.Data` (only used by the dead `Types.fs`) and `Newtonsoft.Json`, replaced by
  `System.Text.Json`, which Oxpecker already uses.
- **Unchanged**: Oxpecker 2.1.1, Oxpecker.ViewEngine 2.1.x, Oxpecker.Htmx 4.0.x, Oxpecker.Alpine
  0.3.x. This feature needs no htmx or Alpine APIs.

## R16. Routes and pages

- **Decision**:
  - JSON API under `/api`: `POST /api/imports`, `GET /api/imports/{id}`, `GET /api/me`.
  - HTML: `GET /` (localized home explaining how to sync, with no user list) and
    `GET /u/{username}` (public dashboard).
  - The placeholder `/dashboard` and `/profile` routes are **removed**. They show invented numbers
    and hardcoded Spanish, against FR-015/FR-017; the profile page comes back with feature 004.
  - Username rule: `^[a-z0-9][a-z0-9_-]{2,29}$`, stored lowercase, looked up case-insensitively.
- **Rationale**: `/api` keeps JSON and HTML apart and gives the extension a stable prefix. Profiles
  are public by URL but not listed anywhere.

## R17. Dev environment and project layout

- **Decision**:
  - Add an `http` launch profile on `http://localhost:5099`, the supported dev URL for the
    extension (no dev-cert trust needed); keep `https://localhost:5001`.
  - Add `FrunchyBackend.slnx` at the repo root listing the app and the test project, so
    `dotnet test` works from the root.
  - Add `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` to
    `FrunchyBackend.fsproj`, so the Web SDK's default globs don't pick up the nested test
    project's files.
- **Rationale**: the extension session flagged the dev-cert problem. A nested project under a Web
  SDK project needs this exclusion.

## R18. API error model

- **Decision**: JSON errors `{ "error": "<code>", "message": "<English text>" }`, with:

  | Code | HTTP status |
  |---|---|
  | `unauthorized` | 401 |
  | `invalid_submission` | 400 |
  | `history_owner_missing` | 400 |
  | `history_owner_mismatch` | 409 |
  | `history_owner_taken` | 409 |
  | `payload_too_large` | 413 |
  | `not_found` | 404 |

  Another user's import also returns `not_found`, so nothing is disclosed (FR-001b). Field names
  are camelCase (System.Text.Json web defaults).
- **Rationale**: the extension maps codes to its own localized messages and never retries 4xx.
  This replaces the old snake_case, text/plain responses.
