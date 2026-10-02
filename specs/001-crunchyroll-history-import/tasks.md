---

description: "Task list for feature 001: Crunchyroll watch-history import with real dashboard"
---

# Tasks: Crunchyroll Watch-History Import with Real Dashboard

**Input**: Design documents from `/specs/001-crunchyroll-history-import/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: REQUIRED by constitution Principle V. Every endpoint slice and the worker get
integration tests against a real PostgreSQL (Testcontainers), using the two fixtures
`JsonSamples/PostHistory.json` (2022) and `JsonSamples/crunchyroll-history-2026-sample.json`
(2026, 4 pages / 400 entries). Parsing gets unit tests. Within each story, write the tests first
and see them fail.

**Organization**: grouped by user story (US1 P1, US2 P2, US3 P3). Phase 6 is owned by the
**browser-extension session** (repo `../browser-extension`; find it with `ListAgents` by cwd).
The backend session never edits that repo: it sends contract changes and readiness signals by
SendMessage, and checks off Phase 6 tasks when that session reports them done.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1 / US2 / US3, from spec.md
- Paths are relative to the FrunchyBackend repo root unless they start with `../browser-extension/`
- **F# compile order**: every new `.fs` file must be added to `<Compile Include>` in
  `FrunchyBackend.fsproj` (or the test fsproj) in this order: Domains → Infrastructure → Views →
  Handlers → Operations → Workers → Program. Each task that creates a file includes adding it.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: packages, solution, test project, dev URL.

- [X] T001 Update `FrunchyBackend.fsproj`:
  - Npgsql `8.0.1` → latest `10.0.x` (advisory GHSA-x9vc-6hfv-hg8c, research R15);
  - remove the `FSharp.Data` and `Newtonsoft.Json` package references;
  - pin `Oxpecker.Htmx` to `4.0.*`;
  - add `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` to the
    PropertyGroup (research R17).

  Run `dotnet build`; the NU1903 warning must be gone. *(Done: Npgsql 10.0.3, 0 warnings. `Types.fs` was
  deleted with FSharp.Data. `Newtonsoft.Json` is removed in T031, where the old worker that uses
  it is rewritten.)*
- [X] T002 [P] Create `tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj` (`net10.0`,
  `IsPackable=false`):
  - packages: `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`,
    `Testcontainers.PostgreSql`, `Microsoft.AspNetCore.TestHost` (latest stable at
    implementation time);
  - `<ProjectReference Include="../../FrunchyBackend.fsproj" />`;
  - `<None Include="../../JsonSamples/PostHistory.json;../../JsonSamples/crunchyroll-history-2026-sample.json" CopyToOutputDirectory="PreserveNewest" LinkBase="JsonSamples" />`;
  - an initial empty `Smoke.fs` with one passing test so the project builds.

  *(Done: xUnit v3 4.x runs on Microsoft.Testing.Platform, so a root `global.json` with
  `"test": { "runner": "Microsoft.Testing.Platform" }` is required on the .NET 10 SDK.)*
- [X] T003 Create `FrunchyBackend.slnx` at the repo root, listing `FrunchyBackend.fsproj` and
  `tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj`. Verify `dotnet build` and
  `dotnet test` work from the root (depends on T001, T002).
- [X] T004 [P] Add an `http` profile to `Properties/launchSettings.json` with `applicationUrl`
  `http://localhost:5099` and `ASPNETCORE_ENVIRONMENT=Development`; keep the existing https
  profile (research R17).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: domain types, schema, localization, auth, composable app, test harness and user
creation. Every story needs these.

**⚠️ CRITICAL**: no user story work starts until this phase is complete.

- [X] T005 [P] Rewrite `Domains/Identity.fs`:
  - `Username` (validated `^[a-z0-9][a-z0-9_-]{2,29}$`, lowercased; `tryCreate`);
  - `SyncKey` (`generate()` = `"frk_" + base64url(RandomNumberGenerator 32 bytes)`, `hash key` =
    lowercase hex SHA-256);
  - `HistoryOwner = { AccountId: string; ProfileId: string option }`;
  - a `User` record matching the `users` table in data-model.md.
- [X] T006 [P] Rewrite `Domains/Tracking.fs`:
  - `ImportStatus` DU (`Pending | Processing | Completed | Failed`) with `toDb`/`fromDb`
    (lowercase text);
  - `EntryOutcome` DU (`Processed | Unresolved | Unsupported | Failed`) with `toDb`;
  - `ImportCounts` record;
  - `WatchEvent` record per data-model.md.
- [X] T007 [P] Rewrite `Domains/Catalog.fs` with `CrSeries`, `CrSeason`, `CrEpisode` records per
  data-model.md (`DurationMs: int option`, so `None` = unresolved). Delete `Domains/Analytics.fs`
  and `Types.fs`, and remove them from `FrunchyBackend.fsproj`.
- [X] T008 Rewrite `initializeSchema` in `Infrastructure/Database.fs` to create exactly the
  tables, constraints and indexes in data-model.md:
  - tables `users`, `cr_imports`, `cr_import_pages` with `body json`, `cr_import_entry_issues`,
    `cr_series`, `cr_seasons`, `cr_episodes`, `watch_events`;
  - all user-owned FKs `ON DELETE CASCADE`;
  - `ux_users_history_owner`;
  - indexes `(status, received_at)`, `(user_id, received_at DESC)`, `(user_id, watched_at DESC)`.

  Before creating anything, detect the legacy layout (`import_records` table exists or
  `users.email` column exists) and throw an exception whose message says to run
  `dropdb frunchydb && createdb frunchydb` (research R14). Keep `getConnection`.
- [X] T009 [P] Rewrite `SCHEMA.md` to document the new schema from data-model.md: tables, which
  data is personal and which is shared catalog, rebuild/delete semantics, and the legacy-reset
  note.
- [X] T010 [P] Create `Views/Texts.fs` (before `Views/Layout.fs` in the fsproj):
  - an F# record `Texts` for every interface string used by the layout, home and generic error
    pages (site name, footer, home title/body explaining extension sync, generic 404/500 texts);
  - values `en` and `es`;
  - `forCulture (c: CultureInfo) : Texts` returning `es` when `c.TwoLetterISOLanguageName = "es"`,
    otherwise `en` (research R11).

  Dashboard strings are added in T034.
- [X] T011 Rewrite `Views/Layout.fs` as `page (t: Texts) (lang: string) (title: string) (content: HtmlElement)`:
  - `<html lang=…>`, localized navbar (brand → `/` only; remove the `/dashboard` and `/profile`
    links) and footer;
  - keep the CDN scripts (Tailwind, DaisyUI, htmx 4.0.0, Alpine);
  - add an inline script that, on `DOMContentLoaded`, rewrites every `[data-fr-time]` element to
    `Intl.RelativeTimeFormat(lang, {numeric:"auto"})` + " · " +
    `Intl.DateTimeFormat(lang, {dateStyle:"medium", timeStyle:"short"})` from its `datetime`
    attribute (contracts/dashboard-ui.md, "Time elements"; research R12).

  Depends on T010.
- [X] T012 [P] Create `Infrastructure/ApiErrors.fs`:
  - `writeError (ctx) (status: int) (code: string) (message: string)`, writing
    `{ "error": code, "message": message }` with camelCase JSON;
  - constants for the codes in contracts/import-api.md "Errors".
- [X] T013 Create `Infrastructure/Auth.fs`:
  - `tryAuthenticate (ctx: HttpContext) : Task<User option>` reads `Authorization: Bearer <key>`,
    hashes it with `SyncKey.hash`, and looks up `users` by `sync_key_hash` (Npgsql, hand-written
    SQL);
  - `requireUser`, which returns the user or writes 401 `unauthorized` via `ApiErrors`.

  Depends on T005, T008, T012.
- [X] T014 Refactor `Program.fs` into a composition root (research R1):
  - `configureServices (services) (config)`: DbConfig from `ConnectionStrings:Database`,
    `AddRouting`, `AddOxpecker`, `AddHostedService<ImportProcessorService>`, plus a flag to skip
    the hosted service in tests;
  - `configureApp (app)`: `UseRequestLocalization` with supported cultures `en` (default) and `es`
    and **only** `AcceptLanguageHeaderRequestCultureProvider`, then `UseStaticFiles`,
    `UseRouting`, the error handler, `UseOxpecker(endpoints)`, and a localized not-found handler
    using `Texts`;
  - `endpoints` (only `Handlers.Home.endpoint` for now);
  - `buildApp (args) (customize: WebApplicationBuilder -> unit) : WebApplication`, used by both
    `main` and the tests;
  - `main`: if `args` has a first element that is a known CLI command, dispatch to `Operations`
    (wired in T019/T043) and exit with its code; otherwise run `initializeSchema`, then
    `app.RunAsync()`.

  Delete `Handlers/Dashboard.fs`, `Handlers/Profile.fs`, `Handlers/Imports/CreateImport.fs` and
  `Handlers/Imports/GetImportStatus.fs` (research R16), and update the fsproj compile list.
  Depends on T008, T011.
- [X] T015 Localize `Handlers/Home.fs`: the view takes `Texts` and explains Frunchy and how to
  sync from the extension, with no dashboard link and no user list. The handler resolves
  `Texts.forCulture CultureInfo.CurrentUICulture` and the lang. The endpoint is
  `GET [ route "/" handler ]`. Depends on T011, T014.
- [X] T016 Create `tests/FrunchyBackend.Tests/Infrastructure/PostgresFixture.fs`:
  - an xUnit v3 assembly/collection fixture starting one `postgres:16-alpine` Testcontainer;
  - `newDatabase()` creates a uniquely named database and runs `initializeSchema` on it;
  - `TestApp.create (connStr)` builds the app via `Program.buildApp` with
    `builder.WebHost.UseTestServer()`, the connection string overridden and the hosted worker
    disabled, and returns `(app, HttpClient)`;
  - `Db.scalar`/`Db.count table` helpers for assertions.

  Depends on T002, T014.
- [X] T017 [P] Create `tests/FrunchyBackend.Tests/Fixtures.fs`:
  - `load2026Sample()` returns the file text (already in envelope form);
  - `load2022Envelope()` wraps the `PostHistory.json` body as `{ "pages": [ { "url": "https://www.crunchyroll.com/content/v2/3b6dd336-f005-5b47-9b98-b26a174a2010/watch-history?locale=en-US&page=1&page_size=100", "body": <file JSON verbatim> } ] }`;
  - `syntheticPages (accountId) (pageCount) (entriesPerPage)` generates valid pages with unique
    episode ids, for the scale tests;
  - `envelopeOf (pages)`.
- [X] T018 Create `Operations/CreateUser.fs` (`user-create <username> <displayName>`, per
  contracts/operator-cli.md): validate the username (exit code 3 if invalid or taken), generate
  the key, insert `users` with `sync_key_hash`, print the key once plus the `/u/{username}` path.
  Also expose `createUser (db) (username) (displayName) : Task<Result<string,string>>` for the
  tests. Depends on T005, T008.
- [X] T019 Wire `user-create` into the CLI dispatch in `Program.fs`: the command bootstraps the
  schema first. Depends on T014, T018.
- [X] T020 [P] Foundational tests:
  - `tests/FrunchyBackend.Tests/IdentityTests.fs` (unit): username rule accept/reject cases,
    lowercasing, key format `frk_` + 43 chars, hash deterministic and different from key;
  - `tests/FrunchyBackend.Tests/DatabaseTests.fs` (integration): `initializeSchema` twice is a
    no-op; the legacy layout (create an `import_records` table first) throws with the reset
    instructions;
  - `tests/FrunchyBackend.Tests/HomeTests.fs`: `GET /` with `Accept-Language: es` → `lang="es"`
    and Spanish text; `en` and `fr` → English; `/dashboard` and `/profile` → 404.

  Depends on T015, T016, T018.

**Checkpoint**: `dotnet test` green. The app starts on `http://localhost:5099` with a localized
home page and `dotnet run -- user-create` prints a key. *(Verified 2026-10-02: 24/24 tests; the
CLI was checked on a scratch DB; the app refuses the legacy `frunchydb` with the reset message.
`UseStaticFiles` was dropped since there's no wwwroot. The tests decode HTML entities because
ViewEngine writes non-ASCII as `&#NNN;`.)*

---

## Phase 3: User Story 1 - Send my watch history and have it processed (Priority: P1) 🎯 MVP

**Goal**: authenticated, owner-bound submissions are stored verbatim and processed in the
background into the shared catalog plus one watch event per user and episode, with status,
issues and "who am I".

**Independent Test**: create a user, POST `PostHistory.json` (wrapped) and poll status, which
completes with 100 processed / 1 unresolved. POST it again: the `watch_events` row count is
unchanged. Wrong key → 401, other user's owner → 409, nothing stored.

### Tests for User Story 1 (write first, must fail)

- [ ] T021 [P] [US1] `tests/FrunchyBackend.Tests/CrunchyrollPageTests.fs` (unit, against
  `Infrastructure/CrunchyrollPage`):
  - 2022 page → 100 episode entries (96 fully, 4 partial) + 1 unresolved;
  - 2026 sample → 309 episode entries + 91 unresolved, 0 unsupported/failed;
  - movie entries ("JUJUTSU KAISEN 0" style, `series_id` ≠ `parent_id`) keep
    `seriesId = parent_id` and don't supply the series title;
  - the thumbnail picks width 320 from the 8 variants;
  - an entry with `panel.type = "movie_listing"` → Unsupported;
  - a panel without `duration_ms` → Failed with reason;
  - a non-object entry → Failed;
  - owner extraction from absolute and relative URLs and with opaque cursor queries;
  - pages with different account ids → error; a URL without `/content/v2/{id}/watch-history` →
    error.
- [ ] T022 [P] [US1] `tests/FrunchyBackend.Tests/CreateImportTests.fs` (integration):
  - 202 body shape (`importId`, `status:"pending"`, `statusUrl`, `entriesReceived:101`);
  - stored `cr_import_pages.body` text equals the submitted page JSON text exactly;
  - 401 for missing/unknown key;
  - 400 `invalid_submission` for: not JSON, no `pages`, `[]`, 101 pages, a page without `url`, a
    page without `body.data`;
  - 400 `history_owner_missing` for a bad URL and for mixed account ids;
  - the first 202 sets `users.cr_account_id`;
  - 409 `history_owner_mismatch` (same key, different account id);
  - 409 `history_owner_taken` (second user, same account id);
  - 413 `payload_too_large` for a body over 30,000,000 bytes;
  - for **every** rejection: `cr_imports` and `cr_import_pages` row counts unchanged and binding
    unchanged (SC-009, SC-012).
- [ ] T023 [P] [US1] `tests/FrunchyBackend.Tests/GetImportStatusTests.fs`: `pending` right after
  POST; after `ImportProcessor.processNext`, `completed` with counts
  `{received:101, processed:100, unresolved:1, unsupported:0, failed:0}` and one `unresolved`
  issue with page/index/episodeId/reason; the 2026 sample → `{400, 309, 91, 0, 0}`; another
  user's key → 404; unknown id → 404; bad key → 401.
- [ ] T024 [P] [US1] `tests/FrunchyBackend.Tests/WhoAmITests.fs`: valid key → `username`,
  `displayName`, absolute `dashboardUrl` ending `/u/{username}`, `lastSyncAt: null` and
  `historyOwnerBound:false` before any import, set after a 202; revoked or unknown key → 401.
- [ ] T025 [P] [US1] `tests/FrunchyBackend.Tests/ImportProcessorTests.fs` (calls `processNext`
  directly):
  - counts add up for both fixtures (SC-003);
  - submitting the 2022 envelope 3 times → identical `watch_events` table (SC-005);
  - the 2026 sample submitted as 4 separate imports in reverse order vs forward order, in two
    fresh databases → identical `watch_events` and `cr_episodes` contents (FR-011);
  - an episode first submitted without `panel`, then with `panel` in a later import → its
    `cr_episodes.duration_ms` is filled and the watch keeps the newest `watched_at` (FR-008);
  - newer `date_played` updates `watched_at`, and an older one doesn't (US1 #9);
  - a partial watch later sent as fully watched → `fully_watched` true, still 1 row;
  - an import stuck in `processing` with `processing_started_at` 11 minutes ago is reclaimed;
  - two pending imports of the same user → same result as sequential;
  - **scale**: 64 synthetic pages × 100 entries processed in under 60 s (SC-002 margin).

### Implementation for User Story 1

- [ ] T026 [US1] Create `Infrastructure/CrunchyrollPage.fs` (pure, no I/O), after
  `Infrastructure/Database.fs` in the fsproj:
  - `ParsedEntry` DU: `Episode of EpisodeFacts | Unresolved of UnresolvedFacts | Unsupported of
    id * reason | Failed of id option * reason`;
  - `parsePage (body: JsonElement) : Result<ParsedEntry list, string>`, implementing the
    classification table in data-model.md;
  - the series key is `parent_id`, and the series title is used only when
    `series_id = parent_id` (research R9);
  - the thumbnail is the `images.thumbnail[0]` variant with width closest to 320;
  - `tryHistoryOwner (url: string) : Result<string, string>` parses
    `/content/v2/{accountId}/watch-history` and ignores the query.

  Makes T021 pass.
- [ ] T027 [US1] **GATE: history-owner rule (research R5).** Collect the extension session's
  profile capture (task T051). Decide whether `profileId` is required, and record the decision in
  `specs/001-crunchyroll-history-import/research.md` (R5) and
  `specs/001-crunchyroll-history-import/contracts/import-api.md` ("History owner", plus the
  change log). Send the rule to the extension session with SendMessage.

  If the capture isn't available when T028 starts, proceed with the designed default (optional
  `profileId`, included in the owner when present), record that, and keep this task open.
- [ ] T028 [US1] Create `Handlers/Api/CreateImport.fs`. Top to bottom:
  - **contract**: request parsing with `JsonDocument` (keep each page's `GetRawText()`), and the
    202 response record;
  - **SQL**: `private` binding check/set and the insert of `cr_imports` (`pending`,
    `page_count`, `entries_received`, `exported_at`) plus one `cr_import_pages` row per page
    (`NpgsqlDbType.Json` with the raw text, `url`, `captured_at`), all in **one transaction**;
  - **handler**: `Auth.requireUser` → body-size check (`Content-Length` or bytes read
    > 30,000,000 → 413) → validation rules 1–5 in data-model.md using `CrunchyrollPage.tryHistoryOwner`
    and the T027 rule → binding (`history_owner_mismatch` / `history_owner_taken`, with the unique
    index violation mapped to `taken`) → insert → 202;
  - **endpoint**: `POST [ route "/api/imports" handler ]`.

  Register it in `Program.fs` endpoints. Makes T022 pass.
- [ ] T029 [P] [US1] Create `Handlers/Api/GetImportStatus.fs`: the contract record per
  contracts/import-api.md; `private` SQL loading the import **only where `user_id` = the
  authenticated user**, plus its issues ordered by `page_index, entry_index`; the handler returns
  404 `not_found` when there's no row; endpoint
  `GET [ routef "/api/imports/{%O:guid}" handler ]`. Register it in `Program.fs`. Makes T023 pass
  (together with T031).
- [ ] T030 [P] [US1] Create `Handlers/Api/WhoAmI.fs`: the contract record; `private` SQL for
  `max(received_at)`; `dashboardUrl` built from `ctx.Request.Scheme` + `Host` + `/u/{username}`;
  endpoint `GET [ route "/api/me" handler ]`. Register it in `Program.fs`. Makes T024 pass.
- [ ] T031 [US1] Rewrite `Workers/ImportProcessor.fs`:
  - `claimNext` first resets stale `processing` imports (> 10 min) to `pending`, then
    `SELECT … WHERE status='pending' ORDER BY received_at LIMIT 1 FOR UPDATE SKIP LOCKED`, sets
    `processing` + `processing_started_at`, and commits;
  - `processImport` runs in one transaction holding `FOR UPDATE` on the import row:
    - for each page in `page_index` order, parse with `CrunchyrollPage.parsePage` and write via
      `NpgsqlBatch`: `cr_series`/`cr_seasons`/`cr_episodes` upserts guarded by
      `source_at >= stored` (`source_at` = page `captured_at` ?? import `received_at`);
    - unresolved entries create a stub `cr_episodes` (id, series_id) without touching existing
      details;
    - `watch_events` upsert with
      `WHERE (excluded.watched_at, excluded.fully_watched, excluded.playhead_s) > (watch_events.watched_at, watch_events.fully_watched, watch_events.playhead_s)`;
    - one `cr_import_entry_issues` row per non-processed entry;
    - finally set the counts, `completed` and `completed_at`;
  - on an unexpected exception: roll back, set `failed` + `error` in a new transaction, and log;
  - `processNext (db) : Task<bool>` (true if it processed something);
  - `ImportProcessorService`: a loop calling `processNext`, sleeping 2 s only when it returns
    false, honouring cancellation.

  Makes T023 and T025 pass.
- [ ] T032 [US1] Run the US1 suites (`dotnet test --filter` on the US1 test classes) plus
  `quickstart.md` section 2's curl steps against `dotnet run --launch-profile http`, checking the
  status counts `{400, 309, 91, 0, 0}` for the 2026 sample. Then send the extension session the
  readiness message: "backend ready on http://localhost:5099; create a key with
  `dotnet run -- user-create …`; please run task T052".

**Checkpoint**: US1 is fully functional and independently testable through the API alone.

---

## Phase 4: User Story 2 - See my real numbers on the dashboard (Priority: P2)

**Goal**: public `/u/{username}` with real figures, recent episodes, last sync and in-progress
notice, empty state and 404, in English or Spanish with viewer-local times.

**Independent Test**: process the 2022 fixture for a user and GET `/u/{username}`: 29 / 96 /
39.3 / 1.6, with "The Vampire Dies in No Time" ep. 2 first (marked in progress). A user without
history gets the empty state; an unknown username gets 404.

### Tests for User Story 2 (write first, must fail)

- [ ] T033 [P] [US2] `tests/FrunchyBackend.Tests/DashboardTests.fs` (parse the HTML and assert on
  `data-stat` / `data-recent-item` hooks from contracts/dashboard-ui.md):
  - **figures**: the 2022 fixture → series 29, episodes 96, hours `39.3` (en) / `39,3` (es),
    days 1.6; the 2026 sample → 67 / 298 / 118.2, and the first recent item is "Clevatess",
    Episode 13;
  - **recent list**: at most 6, newest first; a special shows "Special" / "Especial"; the
    in-progress badge is on partial watches; every `<time>` has an ISO `datetime` and fallback
    text containing "UTC";
  - **empty state, notices and 404**: empty state for a user with no resolved watches, with no
    `data-stat` present; the "sync in progress" notice when a pending import exists; the public
    notice is present; an unknown username → 404 with localized text;
  - **isolation and language**: two users each see only their own figures; `Accept-Language: es`
    → `lang="es"` and Spanish strings, `fr` → English; the HTML never contains the sync key, an
    import id or an issue reason (FR-011b);
  - **performance**: 10,000 synthetic watch events → the response arrives in < 2 s (SC-007).

### Implementation for User Story 2

- [ ] T034 [US2] Add the dashboard strings to `Views/Texts.fs` (both `en` and `es`):
  - stat labels: series, episodes, hours, days;
  - section titles: recent episodes, last sync;
  - notices: public profile, sync in progress;
  - empty-state title and instructions;
  - episode labels: `episodeLabel: int -> string` and the special label; duration
    `minutes: int -> string`; the in-progress badge;
  - "user not found" title/body;
  - number formatting via the culture.
- [ ] T035 [US2] Create `Handlers/Users/Dashboard.fs`. Top to bottom:
  - **views**: header (display name, public notice, last-sync `<time data-fr-time>`, in-progress
    notice); stats with `data-stat` hooks; recent list with `data-recent-item`, thumbnail
    (`loading="lazy"`), episode label, minutes, badge and `<time>`; empty state; a "user not
    found" page, all via `Views.Layout.page`;
  - **SQL** (`private`, data-model.md "Dashboard figures"): the user lookup by lowercase
    username; one aggregate query for series/episodes/hours; a recent query (`LIMIT 6`, resolved
    only); last sync and in-progress flag;
  - **handler**: hours and days rounded to 1 decimal with `MidpointRounding.AwayFromZero`; 404
    page when the username is unknown;
  - **endpoint**: `GET [ routef "/u/{%s}" handler ]`.

  Register it in `Program.fs`. Makes T033 pass.
- [ ] T036 [US2] Manual check per `quickstart.md` section 2. Open `/u/animenerd` in a real
  browser in English and Spanish and confirm the `Intl` script turns `<time>` into relative +
  local time (e.g. "hace 2 horas · …").

**Checkpoint**: US1 and US2 both work independently.

---

## Phase 5: User Story 3 - Rebuild or erase a user's history (Priority: P3)

**Goal**: operator CLI to regenerate keys, reset bindings, rebuild from raw pages with identical
results, and delete a user and everything they own.

**Independent Test**: process the 2026 sample, capture the dashboard figures and recent list, run
`user-rebuild` + `processNext`, and compare: identical. `user-delete` → 404 dashboard, 401 key,
other user intact.

### Tests for User Story 3 (write first, must fail)

- [ ] T037 [P] [US3] `tests/FrunchyBackend.Tests/OperationsTests.fs`:
  - **rebuild**: dashboard figures and recent list identical before and after, and
    `cr_import_pages` bodies byte-identical before and after (SC-006);
  - **regenerate key**: the old key → 401, the new one → 200 on `/api/me`;
  - **reset binding**: a different account id is accepted afterwards and the history is kept;
  - **delete**: removes every row of the user in `users`, `cr_imports`, `cr_import_pages`,
    `cr_import_entry_issues` and `watch_events`, while a second user's rows and figures are
    unchanged (SC-010); deleting a user with a `pending` import, then calling `processNext` →
    nothing processed and no rows for that user; `user-delete` without `--yes` refuses;
  - **errors and listing**: an unknown username → exit code 2; an invalid or taken username on
    create → 3; `user-list` prints the bound flag and the last sync.

### Implementation for User Story 3

- [ ] T038 [P] [US3] Create `Operations/RegenerateKey.fs` (`user-regenerate-key <username>`): new
  key, replace `sync_key_hash` and `sync_key_created_at`, print the key once.
- [ ] T039 [P] [US3] Create `Operations/ResetBinding.fs` (`user-reset-binding <username>`): set
  `cr_account_id` and `cr_profile_id` to NULL.
- [ ] T040 [P] [US3] Create `Operations/RebuildUser.fs` (`user-rebuild <username>`): in one
  transaction, delete the user's `watch_events` and their imports' `cr_import_entry_issues`, then
  set their imports to `pending` with counts, `error`, `processing_started_at` and `completed_at`
  cleared; print the number of imports queued (research R13).
- [ ] T041 [P] [US3] Create `Operations/DeleteUser.fs` (`user-delete <username> --yes`): refuse
  without `--yes`; `DELETE FROM users WHERE id = …` (the cascade removes everything, and blocks
  behind the worker's `FOR UPDATE`); print the removed counts gathered before deleting.
- [ ] T042 [P] [US3] Create `Operations/ListUsers.fs` (`user-list`): username, display name,
  bound yes/no, import count, last sync.
- [ ] T043 [US3] Wire `user-regenerate-key`, `user-reset-binding`, `user-rebuild`, `user-delete`
  and `user-list` into the CLI dispatch in `Program.fs` and the fsproj compile list, with the exit
  codes from contracts/operator-cli.md. Makes T037 pass.

**Checkpoint**: all three stories work independently. `dotnet test` is green.

---

## Phase 6: Extension (browser-extension repo), owned by the browser-extension session

**Purpose**: the client side of contracts/import-api.md. These tasks are built, tested and
committed **by the browser-extension session** in `../browser-extension`. The backend session
sends it contract changes and readiness signals, and checks a task off here only when that
session reports it done. File paths are suggestions; the extension session owns its layout.

**Status reported by the extension session (2026-10-02)**: T044–T049 are built and
harness-tested with mocked fetch, but **not committed and not run against a live backend**, so
they stay unchecked until they're committed and pass T052. T050 not started (optional; "Sync
stored pages" covers resending from local storage). T051 waits on a second Crunchyroll profile.
T052 waits on T032.

- [ ] T044 [P] [US1] (extension) Options page with the backend base URL (default
  `http://localhost:5099`) and the sync key stored in `chrome.storage`, plus a "validate" action
  calling `GET /api/me` (200 → show the display name; 401 → "invalid key"). Files:
  `../browser-extension/src/components/Options.svelte`, `../browser-extension/src/options/index.ts`.
- [ ] T045 [P] [US1] (extension) Relay each intercepted watch-history response as
  `{ url: this._url, body, capturedAt }` from content script → background, and POST it from the
  service worker to `/api/imports` with `Authorization: Bearer`, one page per request. Add the
  backend origin to `host_permissions`. Files: `../browser-extension/src/content/index.ts`,
  `../browser-extension/src/background/index.ts`, `../browser-extension/manifest.json`.
- [ ] T046 [US1] (extension) Retry queue in `chrome.storage.local`: queue pages until a 202;
  retry 5xx and network errors with backoff; never retry 4xx (surface them instead). File:
  `../browser-extension/src/background/index.ts`.
- [ ] T047 [P] [US1] (extension) Map the API error codes (`unauthorized`, `invalid_submission`,
  `history_owner_missing`, `history_owner_mismatch`, `history_owner_taken`, `payload_too_large`)
  to the extension's own user-facing messages, shown in the popup.
- [ ] T048 [P] [US1] (extension) Popup: user name and "Abrir Dashboard Web" (`dashboardUrl`) from
  `GET /api/me`, last sync time, pages waiting to upload, and optional status polling of
  `GET /api/imports/{id}` until `completed`/`failed`. File: `../browser-extension/src/popup/index.ts`.
- [ ] T049 [US1] (extension) Stop marker: store the newest `(id, date_played)` sent; on later
  syncs, stop paging once a page contains it (the decoded cursor `{c,t}` may be used too).
- [ ] T050 [P] [US1] (extension, optional) "Upload export file": POST an existing export JSON
  as-is to `/api/imports` (≤ 30,000,000 bytes).
- [ ] T051 [US1] (extension) **Profile capture for T027**, with its user's OK, on their own
  account:
  - the watch-history request URL under profile A and, after switching, under profile B;
  - the id field names and values from `GET /accounts/v1/me` and
    `GET /accounts/v1/me/multiprofile` (ids only, no full bodies);
  - where the active profile is readable without the token (a response field, a cookie,
    localStorage), or the JWT claim name only.

  Never send or log tokens or emails. Report the result to the backend session.
- [ ] T052 [US1] (joint, after T032) End-to-end: the extension syncs the real history into the
  local backend on `:5099`. Check the counts reported by `GET /api/imports/{id}` and that
  `/u/{username}` shows about 327 series, 5,921 episodes and ≈ 2,367.6 h for the reference
  history (quickstart.md section 4). Both sessions report the result.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T053 [P] Update `README.md`:
  - API section → `/api/imports`, `/api/imports/{id}`, `/api/me`, linking
    `specs/001-crunchyroll-history-import/contracts/import-api.md`;
  - Getting Started → the dev DB reset, `dotnet run --launch-profile http`, operator CLI commands
    and `dotnet test` (needs Docker);
  - Roadmap → 001 "Implemented".
- [ ] T054 [P] Update `FRONTEND.md`: routes (`/`, `/u/{username}`), `Views/Texts.fs` (typed
  en/es; how to add a string or a language), the `<time data-fr-time>` + `Intl` contract, and
  the removed placeholder pages.
- [ ] T055 [P] Update `CLAUDE.md`: turn "Active feature" into the current architecture (new
  routes, schema, worker semantics, CLI, tests, `dotnet test` from the root), and remove the
  statements made stale by this feature (`/dashboard`, `/imports`, `ImportProcessor` "only
  counts").
- [ ] T056 Check that no user-facing string literal is left in `Handlers/**/*.fs` or
  `Views/Layout.fs` outside `Views/Texts.fs`, and fix any found (FR-017, SC-011).
- [ ] T057 Run the full `quickstart.md` validation (sections 1–3) on a fresh database, and record
  any deviation in `specs/001-crunchyroll-history-import/quickstart.md`.
- [ ] T058 If the contract changed during implementation, update
  `specs/001-crunchyroll-history-import/contracts/import-api.md` (change log) and send the diff
  to the extension session.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: T001 → T003; T002 and T004 run in parallel with T001.
- **Foundational (Phase 2)**: needs Phase 1, and **blocks every story**. Order: T005–T007,
  T009, T010, T012 in parallel → T008 → T011 → T013, T014 → T015, T016, T018 → T019 → T020.
- **US1 (Phase 3)**: needs Phase 2. Tests T021–T025 first (parallel), then T026 → **T027 gate**
  → T028; T029 and T030 in parallel with T028; T031 after T026; T032 last.
- **US2 (Phase 4)**: needs Phase 2. Its tests seed data through `processNext`, so in practice it
  needs T026 and T031 from US1. T033 → T034 → T035 → T036.
- **US3 (Phase 5)**: needs Phase 2. Its tests use US1 (import/process) and US2 (figures) to prove
  identity. T037 → T038–T042 in parallel → T043.
- **Extension (Phase 6)**: T044–T050 can start **now** against contract v1. T051 feeds the T027
  gate. T052 needs T032 (backend ready) and T044–T046.
- **Polish (Phase 7)**: after the desired stories are done.

### User story dependencies

- **US1 (P1)**: independent after Foundational, and is the MVP.
- **US2 (P2)**: reads the tables US1 writes; testable independently once the worker exists
  (T031).
- **US3 (P3)**: operates on US1 data; verifies identity through US2 figures.

### Within each story

Tests are written first and must fail. Pure parsing (T026) comes before the slices that use it.
Slices are registered in `Program.fs` as they land. The story checkpoint is validated before
moving on.

## Parallel Examples

```text
# Phase 2, after Setup:
T005 Domains/Identity.fs   T006 Domains/Tracking.fs   T007 Domains/Catalog.fs
T009 SCHEMA.md             T010 Views/Texts.fs        T012 Infrastructure/ApiErrors.fs

# US1 tests, all at once:
T021 CrunchyrollPageTests  T022 CreateImportTests  T023 GetImportStatusTests
T024 WhoAmITests           T025 ImportProcessorTests

# US1 slices once T026 is in:
T029 Handlers/Api/GetImportStatus.fs   T030 Handlers/Api/WhoAmI.fs   (T028 separately: it waits on the T027 gate)

# US3 operations:
T038 RegenerateKey  T039 ResetBinding  T040 RebuildUser  T041 DeleteUser  T042 ListUsers

# Across sessions: the extension session works T044–T051 while the backend does Phases 2–3.
```

## Implementation Strategy

### MVP first (US1)

1. Phase 1 + Phase 2 → `dotnet test` green, localized home, `user-create` works.
2. Phase 3 (US1) → the API accepts, binds and processes, with status and whoami (T032 notifies
   the extension).
3. **Stop and validate**: the US1 suites plus the curl checks in quickstart section 2.

### Incremental delivery

- **+ US2**: the dashboard, the first time you see your real numbers. Joint run T052 with the
  extension.
- **+ US3**: rebuild and delete, before any second user is invited (feature 002 "Accounts"
  comes before that anyway).
- **Polish**: docs, string audit, a full quickstart run.

### Cross-session flow

The backend session works Phases 1–5 and 7. The extension session works Phase 6 in parallel from
contract v1. Hand-offs happen by SendMessage at T027 (`profileId` rule), T032 (backend ready),
T052 (joint end-to-end) and T058 (contract changes).

## Notes

- [P] = different files, no dependency on incomplete tasks. Same-file tasks (`Program.fs`,
  `Views/Texts.fs`, `FrunchyBackend.fsproj`) are never marked [P] against each other.
- Commit after each task or logical group on branch `001-crunchyroll-history-import`.
- Never edit `../browser-extension`; never commit `JsonSamples/crunchyroll-history-2026-10-02.json`.
