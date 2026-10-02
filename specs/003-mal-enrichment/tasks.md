---

description: "Task list for feature 003: MyAnimeList enrichment"
---

# Tasks: MyAnimeList Enrichment

**Input**: Design documents from `/specs/003-mal-enrichment/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: REQUIRED by constitution Principle V:
- integration tests against a real PostgreSQL (Testcontainers), using the 2026 Crunchyroll sample
  and **recorded real Jikan responses** served by `FakeJikan`;
- unit tests for normalization and matching.

The tests never call the real Jikan. `dotnet test` must pass before the feature is complete.

**Organization**: tasks are grouped by user story, so each story can be implemented and tested on
its own.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 to US4 from spec.md

---

## Phase 1: Setup

- [X] T001 Update `FrunchyBackend.fsproj`:
  - add `<PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.10.0" />`;
  - add the new files in compile order (plan "Structure Decision"): `Infrastructure/JikanModel.fs`,
    `MalMatching.fs`, `MalCatalog.fs` and `JikanClient.fs` after `WebAuth.fs`; `Operations/Mal*.fs`
    after `InviteRevoke.fs`; `Workers/Enrichment.fs` after `ImportProcessor.fs`.

  Create each file as an empty module so `dotnet build` passes with 0 warnings.
- [X] T002 [P] Add the defaults of research R11 to `appsettings.json`:
  - `Frunchy:EnrichmentWorkerEnabled` = true;
  - `Frunchy:Jikan`: `BaseUrl`, `RequestsPerSecond` 1, `RetryBaseDelay` `00:00:02`,
    `AttemptTimeout` `00:00:15`;
  - `Frunchy:Enrichment`: `PauseAfterFailures` 5, `PauseFor` `00:05:00`, `RefreshAfterDays` 30,
    `RefreshAiringAfterDays` 7.

  No secrets: Jikan needs none.
- [ ] T003 [P] Record the Jikan fixtures, a one-time network task, in
  `tests/FrunchyBackend.Tests/JsonSamples/jikan/`.
  - **`record.py`** (Python, implemented instead of a shell script): paced at 1 request per 1.2 s,
    retries 429/5xx, crawls each 2026-sample series (search, then `/full` of the top results and
    their chains), trims bodies to the fields Frunchy reads, and maps keys to files in
    `manifest.json`.
  - **Responses to record**:
    - `search-frieren` (`anime?q=Frieren: Beyond Journey's End&limit=10&sfw`);
    - `full-52991` (Frieren S1) and `full-59978` (S2);
    - `search-jujutsu-kaisen`, `full-40748` (S1), `full-51009` (S2), `full-48561` (the movie 0)
      and the S3 entry found through S2's Sequel relation;
    - `search-roll-over-and-die` plus the matched `full-…` (tier 2, subtitle cut);
    - an ambiguous title with several qualifying first seasons, chosen when recording (e.g. a remake
      title);
    - `search-no-results` (a nonsense title);
    - one `full-…` of an airing show (for refresh);
    - a 404 by id (`full-1` is not guaranteed; pick an id that returns 404 and save its error body).
  - **Stand-in bodies**: if Jikan is down while recording, keep the files the probe saved and mark
    the rest TODO in the script. Tests may use hand-written bodies in the recorded shape until it's
    re-run.

  Fill in the exact ids in the script header once recorded.

  **Status (2026-10-02)**: `record.py` is written, but MyAnimeList was unreachable through Jikan all
  evening (every search and uncached `/full` returned 504). Only Frieren's `/full`, recorded during
  design, is in the fixtures. The other tests use Jikan-shaped bodies from
  `tests/FrunchyBackend.Tests/MalFixtures.fs`. Re-run `record.py` when Jikan answers searches again.

---

## Phase 2: Foundational (blocks every story)

- [X] T004 Extend `initializeSchema` in `Infrastructure/Database.fs` with the 6 tables of
  data-model.md and their constraints:
  - `mal_anime`, `mal_tags`, `mal_anime_tags`, `mal_relations`, `mal_searches`,
    `cr_season_matches`;
  - the two CHECKs on `cr_season_matches`;
  - the FKs, with `ON DELETE CASCADE` where stated, and no FK on `mal_relations.related_id`;
  - indexes on `cr_season_matches(status, next_attempt_at)` and on `mal_anime(checked_at)`;
  - `CREATE INDEX IF NOT EXISTS cr_episodes_season_idx ON cr_episodes (season_id)`.

  No table references `users`. Running it twice is a no-op (an existing test in `DatabaseTests`
  covers idempotency; extend it to the new tables).
- [X] T005 [P] Update `SCHEMA.md` with the six tables, the new index, the shared-catalog note (not
  deleted with a user) and the rebuild statement from data-model.md.
- [X] T006 [P] Create `Infrastructure/JikanModel.fs`:
  - **records**: `JikanTitle`, `JikanTag { Id; Name }`, `JikanRelation { Relation; Entries:
    (id * type * name) list }`, `JikanAnime` (all fields of data-model `mal_anime`, plus
    genres/explicitGenres/themes/demographics/studios and relations), `JikanError { Status; Type;
    Message }`;
  - **parsers** with `System.Text.Json` (`JsonDocument`, never failing on unknown fields):
    - `parseSearch : string -> Result<JikanAnime list, string>`;
    - `parseFull : string -> Result<JikanAnime, string>`;
    - `parseError : string -> JikanError option`;
  - **rules**: score `0` or `null` → `None`; the main title is `titles[type=Default]`; English,
    Japanese and synonyms come from `titles`; the image is `webp` first, then `jpg`; `dataHash` is
    the SHA-256 hex of the `data` element's raw text.
- [X] T007 [P] Unit tests in `tests/FrunchyBackend.Tests/JikanModelTests.fs`, against the T003
  fixtures:
  - Frieren's full parses (titles, type TV, aired 2023-09-29, fall 2023, score 9.26, genres in order
    Adventure / Award Winning / Drama / Fantasy, demographic Shounen, Sequel → 59978, only
    `anime` relations kept);
  - the search parses into candidates;
  - a score of 0 → `None`;
  - the error body parses;
  - unknown extra fields are ignored;
  - a broken body → `Error`.
- [X] T008 Create `Infrastructure/JikanClient.fs` (research R4, contracts/jikan-usage.md):
  - **`register (config) (services)`**: a named `HttpClient` "jikan" with `BaseAddress` and a
    `User-Agent` of `Frunchy-Backend/{version}`, plus `AddResilienceHandler("jikan", …)` with, from
    outermost:
    1. a total timeout of 60 s;
    2. a retry: `MaxRetryAttempts` 3, exponential backoff with jitter, `Delay` from
       `RetryBaseDelay`, `ShouldRetryAfterHeader = true`, handling 408, 429, 500, 502, 503 and 504,
       `HttpRequestException` and `TimeoutRejectedException`;
    3. a token-bucket rate limiter (`RequestsPerSecond` tokens per second, bucket size 1, queue
       limit 100);
    4. a timeout of `AttemptTimeout`.
  - **Response type**: `JikanResponse = Ok of body * etag option | Unchanged | NotFound | Definite of
    string | Temporary of string`, classified per the contract table. An unreadable 200 body counts
    as `Temporary`.
  - **Calls**: `search (q)` and `full (malId) (etag option)`.
  - **`IJikanSource`**, with `Search` and `Full` returning the parsed result:
    - `Live (client, db, clock)` stores every response (`MalCatalog`) before returning it;
    - `Stored (db)` reads only `mal_searches` and `mal_anime.raw`, returning `Unknown` for anything
      not held (research R8).
- [X] T009 [P] Create `tests/FrunchyBackend.Tests/Infrastructure/FakeJikan.fs`, an `HttpMessageHandler`:
  - **routes** from `(path, query)` to a fixture file, a status code with an error body, a delay or
    an exception, plus `Then` sequences (e.g. 504, 504, then 200);
  - **`Requests`**: a thread-safe log;
  - **a helper** to register it as the "jikan" primary handler on a `WebApplicationBuilder`, with
    instant settings (`RequestsPerSecond` 1000, `RetryBaseDelay` 1 ms, `AttemptTimeout` 2 s).
- [X] T010 Create `Infrastructure/MalCatalog.fs` (SQL only, data-model.md):
  - `upsertAnime (conn, tx) (anime) (raw) (etag) (now)`: the columns, then tags and relations
    deleted and re-inserted; `data_hash` set; `fetched_at` and `checked_at` = now;
  - `touchChecked`, `markMissing`, `upsertSearch`, `tryAnimeRaw`, `trySearchRaw`;
  - season matches: `setMatch`, `setAmbiguous`, `setNoMatch`, `setTemporaryFailure (attempts,
    next)`, `releaseClaim`;
  - **guard**: every automatic write has `WHERE method IS DISTINCT FROM 'manual'`, so manual rows
    are never overwritten (FR-006);
  - `seasonsOfSeries`, `animeHeld`.
- [X] T011 Update `Program.fs` and the tests' fixture:
  - call `JikanClient.register` in `configureServices`;
  - `EnrichmentWorkerEnabledKey "Frunchy:EnrichmentWorkerEnabled"`;
  - refactor the CLI map to take a `CliEnv { Db: DbConfig; Services: IServiceProvider }` that
    `runCli` builds from the same configuration (`ServiceCollection` + `JikanClient.register`). The
    existing commands just use `env.Db`, and their behaviour is unchanged;
  - `PostgresFixture.createAppWith` sets `Frunchy:EnrichmentWorkerEnabled=false` and installs a
    `FakeJikan` (default: every route 404) that the test can reach from `TestApp`.
- [X] T012 [P] Client-level tests in `tests/FrunchyBackend.Tests/ResilienceTests.fs`, through
  `FakeJikan`:
  - 504, 504, then 200 → `Ok` after 3 attempts;
  - 429 with `Retry-After: 1` → the waits are honoured (measured);
  - four 503s → `Temporary`, after exactly 4 attempts;
  - 404 → `NotFound` with no retry;
  - 400 → `Definite`;
  - an unreadable 200 → `Temporary`;
  - **pacing**: at `RequestsPerSecond` 2, 6 calls take ≥ 2.5 s (FR-010).

**Checkpoint**: the schema, the client, its parsing, its resilience and the test double all work.
The stories can start.

---

## Phase 3: User Story 1 - The catalog learns what each anime is (Priority: P1) 🎯 MVP

**Goal**: every Crunchyroll season, movies and dubs included, gets matched (or recorded as
ambiguous or no match) in the background, paced and resilient. The details are stored verbatim.
Imports aren't touched.

**Independent Test**:
1. Import the 2026 sample (`TestHelpers.processAll`).
2. Run `Enrichment.processNext` until it returns false, with `FakeJikan` serving the fixtures.
3. Check that each season is in a definite state, and that `mal_anime`, `mal_anime_tags` and
   `mal_relations` hold the matched entries with `raw`.
4. Check that the dashboard figures are unchanged.
5. Run again: zero new requests.

### Tests for User Story 1 (write first, must fail)

- [X] T013 [P] [US1] Unit tests in `tests/FrunchyBackend.Tests/MalMatchingTests.fs` (research R2).
  - **Normalization**: case, accents, punctuation, `(English Dub)`, "The Movie", a leading "the".
  - **Season markers**: "2nd Season" = "Season 2" = "Part 2"; roman "II"; generic titles ("Season
    01" is generic; "JUJUTSU KAISEN Season 3" is generic with marker 3).
  - **Tiers**: tier 1 exact on English, Default or a synonym; tier 2 with the subtitle cut ("ROLL
    OVER AND DIE" vs "Roll Over and Die: I Will Fight…").
  - **Formats**: a movie listing matches only a Movie; a non-movie matches TV, ONA, OVA or Special.
  - **First season**: one qualifier → matched; several where the others are in the earliest one's
    sequel chain → the earliest; otherwise ambiguous, with at most 5 candidates.
  - **Linked seasons**: Frieren S2 by title against 59978; JUJUTSU KAISEN Season 3 by marker 3 = the
    3rd TV in the chain, even though `season_number` is 5; JUJUTSU KAISEN 0 → the movie entry.
  - **Dubs**: "Season 01 (English Dub)" follows the original with the same title or number; a dub
    with no original is matched like an original.
  - **No guessing**: with no qualifier → no match; and the decision is deterministic (same inputs,
    same output).
- [X] T014 [P] [US1] Integration tests in `tests/FrunchyBackend.Tests/EnrichmentTests.fs`.
  - **Discovery (FR-002)**: after an import, `cr_season_matches` has one `pending` row per season;
    `ImportProcessor` itself is unchanged and its suites still pass; running the discovery sweep
    twice inserts nothing new.
  - **Queue order (FR-002a)**: two users' seasons → the most recently watched first; an operator
    `requested_at` goes first; rows with a future `next_attempt_at` are skipped.
  - **Matching end to end** with the fixtures: Frieren S1 and S2, the JUJUTSU KAISEN seasons and
    movie, and a dub (`method dub`, `shared_from`, no request made for it). `raw` is stored verbatim,
    and the tags are in position order.
  - **Ambiguous and no match**: an ambiguous title stores its candidates; "no match" is final (not
    claimed again).
  - **Title change**: a later import renaming a season re-queues an automatic `no_match`, but not a
    `matched` row.
  - **Re-run**: `processNext` over the enriched catalog makes zero requests (SC-005).
  - **Unchanged figures**: the dashboard's four figures are identical before and after (SC-009).
  - **User deletion**: `user-delete` leaves every `mal_*` row and `cr_season_matches` (FR-009).
  - **Concurrency**: two `processNext` calls in parallel never claim the same season (`SKIP LOCKED`).
  - **Stale claims**: a claim older than 10 minutes is reclaimed.
- [X] T015 [P] [US1] Durable-resilience tests, added to `ResilienceTests.fs` at worker level with a
  `TimeProvider`:
  - a lookup whose calls keep returning 504 → `pending`, attempts 1, `next_attempt_at` ≈ now + 1 min;
    then 2 and 4 min; the status becomes `failed` from attempt 3; the delay is capped at 6 h
    (FR-014);
  - another season is still processed in between (FR-017);
  - **pause**: 5 failed lookups in a row → the worker pauses for `PauseFor`, sending no requests
    while the clock is short of it; then exactly one probe; a failed probe → another pause; a
    successful one → normal processing (FR-015);
  - a 404 on a candidate's `full` drops that candidate, without a failure.

### Implementation for User Story 1

- [X] T016 [P] [US1] Implement `Infrastructure/MalMatching.fs` as pure functions (research R2):
  - `normalize`, `seasonMarker`, `isGeneric`, `isDub`, `stripDub`, `isMovieListing`;
  - `titleTier (crTitle) (anime) : int option`;
  - `decideFirstSeason (seriesTitle) (seasonTitle) (candidates) (sequelChainOf) :
    Decision`;
  - `decideLinked (season) (linkedEntries) (chain) : Decision`;
  - `originalFor (dub) (seasons)`;
  - `Decision = Matched of malId * method | Ambiguous of Candidate list | NoMatch of reason`.

  No I/O: anything that would need data takes it as an argument.
- [X] T017 [US1] Implement discovery and the claim in `Workers/Enrichment.fs`:
  - `sweep db`: the `INSERT … WHERE NOT EXISTS` of the pending rows, and the re-queueing on title
    change (automatic `no_match` and `ambiguous` only);
  - `claimNext db now`: reset stale claims (> 10 min), then `FOR UPDATE SKIP LOCKED` in the FR-002a
    order, using `max(watched_at)` per season over `watch_events ⋈ cr_episodes`.
- [X] T018 [US1] Implement `resolveSeason (source: IJikanSource) db clock seasonId` in
  `Workers/Enrichment.fs`:
  - **dub**: follow the original (match the original first if it's still pending, in the same job);
  - **first season**: search with the series title (plus the season title when not generic), then
    `full` of the chosen or earliest candidate, walking its sequel chain as needed;
  - **other seasons**: make sure the first season is resolved, walk relations breadth-first through
    `full` (cap 20 per series, reusing held `mal_anime`), `decideLinked`, then a fallback search by
    season title;
  - **write** the decision with `MalCatalog` in one transaction, setting the dubs that follow it.

  A `Temporary` anywhere → `setTemporaryFailure` with `min(6 h, 1 min × 2^(n−1)) × jitter`, and the
  error goes up to the caller for the pause counter.
- [X] T019 [US1] Implement in `Workers/Enrichment.fs`:
  - `processNext db source clock : Task<ProcessResult>` (`Idle | Done | TemporaryFailure`): the
    sweep, then claim and resolve one season;
  - `EnrichmentService`, a `BackgroundService` looping on `processNext`. It sleeps 5 s when idle and
    applies the pause (consecutive `TemporaryFailure` ≥ `PauseAfterFailures` → `Task.Delay(PauseFor,
    clock)`, then one probe), logging start, pause and resume.

  Register it in `Program.fs` unless `Frunchy:EnrichmentWorkerEnabled=false`. Make
  `TestHelpers.enrichAll` call `processNext` until `Idle`.

**Checkpoint**: US1 is complete. The catalog enriches itself, and the US1 tests pass with the 001
and 002 suites unchanged.

---

## Phase 4: User Story 2 - My dashboard shows what kind of anime I watch (Priority: P2)

**Goal**: score, genres and a MyAnimeList link on matched recently-watched cards, plus the "Top
genres" section (contracts/dashboard.md).

**Independent Test**: import the sample, enrich it with one season forced to `no_match`, and GET
`/u/{username}` in en and es:
- matched cards carry `data-mal-score`, `data-mal-genres` and `data-mal-link`;
- the unmatched card renders as before;
- `data-top-genres` counts equal a SQL hand count.

### Tests for User Story 2 (write first, must fail)

- [X] T020 [P] [US2] Integration tests in `tests/FrunchyBackend.Tests/DashboardMalTests.fs`:
  - **matched cards**: the score has 2 decimals and is omitted when NULL; at most 3 genres, joined
    with ` / `, in position order; the link points to `mal_anime.url` with `target=_blank`;
  - **unmatched cards**: an unmatched or `pending` season's card HTML equals what the same page
    renders before enrichment;
  - **"Top genres"**: at most 5 rows ordered by count, then name; counts and shares equal a hand
    query over the fully watched episodes in matched seasons; the coverage % is correct; the
    section is absent when nothing is matched (SC-007);
  - **unchanged figures**: the four figures are identical with and without enrichment;
  - **languages**: Spanish labels with `Accept-Language: es`, and genre names untranslated;
  - **no requests**: rendering the page makes zero `FakeJikan` requests (FR-011).

### Implementation for User Story 2

- [X] T021 [P] [US2] Add to `Views/Texts.fs` (en and es): `TopGenresTitle`, `TopGenresEpisodes n`,
  `TopGenresCoverage p`, `MyAnimeListLink` and `ScoreLabel`.
- [X] T022 [US2] Extend the recently-watched query and card in `Handlers/Users/Dashboard.fs`:
  - left join `cr_season_matches` (matched) and `mal_anime`, plus a lateral subquery for the top 3
    genres;
  - render the score, genres and link only when they're present (contract hooks `data-mal-*`).
- [X] T023 [US2] Add the "Top genres" section to `Handlers/Users/Dashboard.fs`:
  - its own private query (fully watched, known duration, matched season, `kind = genre`, top 5,
    plus the coverage count);
  - rendered after the figures with bars, `data-top-genres`, `data-genre`, `data-genre-episodes`,
    `data-genre-share` and `data-genres-coverage`;
  - hidden when there are no matched episodes.

**Checkpoint**: US2 is complete, and the dashboard shows the enrichment.

---

## Phase 5: User Story 3 - The operator reviews and fixes matches (Priority: P2)

**Goal**: the `mal-*` commands of contracts/operator-cli.md. Manual decisions are never overwritten,
and the rebuild sends zero requests.

**Independent Test**:
1. A fixture title with two equally good candidates → `ambiguous`.
2. `mal-list` shows it with its candidates.
3. `mal-set <season> <id>` → matched, `method manual`.
4. `processNext`, `mal-retry` on the series and `mal-rebuild` all leave it as set.

### Tests for User Story 3 (write first, must fail)

- [X] T024 [P] [US3] Integration tests in `tests/FrunchyBackend.Tests/MalOperationsTests.fs`, calling
  the commands through `Program`'s CLI map with a `CliEnv` over the test database and `FakeJikan`:
  - **`mal-status`**: counts per status and method, anime held, refreshes due;
  - **`mal-list`**: the queue order and the `--status` filter, with the candidates printed for
    ambiguous rows;
  - **`mal-show <series>`**: its seasons; exit 2 for an unknown series;
  - **`mal-set`**:
    - with an anime already held → no request;
    - with one not held → one `full` request;
    - an id that's a 404 → exit 3 and the row unchanged;
    - a 504 that persists → exit 4;
    - the dubs follow the change;
  - **`mal-none`**: `no_match`, method `manual`;
  - **`mal-retry`**:
    - on an automatic season → `pending`, attempts reset, goes to the front;
    - on a manual one → the match is kept and the anime queued for refresh;
    - on a series → all of its seasons;
  - **manual rows survive** `processNext`, a title change and `mal-rebuild` (SC-008);
  - **`mal-rebuild`**: zero `FakeJikan` requests; "0 changed" on the second run; the re-derived
    columns and tags equal the originals (SC-006).

### Implementation for User Story 3

- [X] T025 [P] [US3] Create `Operations/MalStatus.fs`, `Operations/MalList.fs` and
  `Operations/MalShow.fs` (read-only, contract output columns).
- [X] T026 [P] [US3] Create `Operations/MalSet.fs` and `Operations/MalNone.fs`:
  - `mal-set` uses `IJikanSource.Live` from `env.Services` only when the anime isn't held, and maps
    `NotFound` → exit 3 and `Temporary` → exit 4;
  - both write `method manual` through `MalCatalog` and update the dubs that follow the season.
- [X] T027 [US3] Create `Operations/MalRetry.fs`: a season or series id; automatic rows → `pending`
  with `requested_at = now` and `attempts = 0`; manual rows → set `mal_anime.refresh_requested_at`.
- [X] T028 [US3] Create `Operations/MalRebuild.fs`:
  1. re-derive every `mal_anime` row (columns, tags, relations) from `raw` with
     `JikanModel.parseFull`;
  2. re-run `resolveSeason` with `IJikanSource.Stored` for every automatic row, in season order,
     writing only on changes;
  3. print the counts (re-derived, unchanged, changed, data missing).

  It must never construct the live client.
- [X] T029 [US3] Add `mal-status`, `mal-list`, `mal-show`, `mal-set`, `mal-none`, `mal-retry` and
  `mal-rebuild` to the CLI map in `Program.fs`, and print their usage on bad arguments (exit 1).

**Checkpoint**: US3 is complete, and the operator can fix every non-matched season.

---

## Phase 6: User Story 4 - Details stay current without waste (Priority: P3)

**Goal**: refreshes after 30 days, or 7 while airing. An unchanged hash only moves `checked_at`. A
404 keeps the last details, flagged.

**Independent Test**: enrich, advance the `TimeProvider` 31 days, and `processNext` → one `full`
request per due anime. An identical body → only `checked_at` moves. A changed body → the columns
and tags are updated. A 404 → `missing_since`, and the details are kept.

### Tests for User Story 4 (write first, must fail)

- [X] T030 [P] [US4] Integration tests in `tests/FrunchyBackend.Tests/RefreshTests.fs`
  (`TimeProvider`):
  - **not due**: nothing is refreshed before 30 days (7 for an airing anime);
  - **ordering**: new lookups are processed before refreshes (FR-002a);
  - **unchanged**: an identical body → `fetched_at` stays and `checked_at` moves;
  - **changed**: a changed score or genre → updated;
  - **ETag**: when a stored `etag` exists, `If-None-Match` is sent and `304` → unchanged;
  - **404**: → `missing_since` set, the details and dashboard unchanged, and visible in `mal-status`;
  - **504**: → `refresh_attempts` and `refresh_next_at` back off, and the old details are still
    used.

### Implementation for User Story 4

- [X] T031 [US4] Add refresh to `Workers/Enrichment.fs`:
  - claim a due `mal_anime` row (data-model "Due for refresh", `refresh_requested_at` first) when no
    season lookup is claimable;
  - `full` with the stored `etag`;
  - `Unchanged`, or the same `data_hash` → `touchChecked`;
  - a new hash → `upsertAnime`;
  - `NotFound` → `markMissing`;
  - `Temporary` → refresh backoff with the same formula, counted by the pause.

**Checkpoint**: all stories are complete.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T032 [P] Update `README.md`: the enrichment feature, the `mal-*` commands, the
  `Frunchy:Jikan:*` and `Frunchy:Enrichment:*` settings, and the roadmap (003 Implemented).
- [X] T033 [P] Update `CLAUDE.md`:
  - the architecture section: the enrichment worker and its queue, the `JikanClient` pipeline, "tests
    never call Jikan; `FakeJikan` + `JsonSamples/jikan/`", and manual rows never overwritten;
  - the commands list.
- [X] T034 Run the full `dotnet test` with 0 build warnings. Record the counts in the quickstart's
  validation log.
- [ ] T035 With the owner's go-ahead, run the first real enrichment on `frunchydb` (quickstart §3).
  The schema change only adds tables and an index. Record in the validation log:
  - the duration;
  - the counts per status and method (SC-001, SC-002);
  - any 429s;
  - the JUJUTSU KAISEN, Frieren and dub cases;
  - that `/u/jjchiw`'s four figures are unchanged.
- [ ] T036 **(owner, by hand)** Check 30 random automatic matches against MyAnimeList (SC-001: at most
  1 wrong). Fix the ambiguous ones with `mal-set` and `mal-none` as wanted (quickstart §4). Note the
  result in the validation log. If the automatic rate is under 85%, list the failing title patterns
  for a follow-up tier rule (research R2, "Validation").

---

## Phase 8: Amendment: MyAnimeList's official API as the default provider (research R13)

**Why**: Jikan was unreachable all session and is shutting down. The owner registered a MyAnimeList
app, and the client id lives in Bella as `Frunchy__Mal__ClientId`.

- [X] T037 Split the shared records and JSON helpers into `Infrastructure/MalModel.fs`
  (`MalAnime`, `MalTag`, `MalRelation`, `ApiError`). `JikanModel.fs` keeps only Jikan's parser.
- [X] T038 Create `Infrastructure/MalApiModel.fs`: the MyAnimeList API v2 parser (search nodes, details,
  error body), mapping type, status, relation and rating names, splitting genres into genres,
  themes and demographics by id, and reading partial dates (contracts/mal-usage.md).
- [X] T039 Turn `JikanClient.fs` into `Infrastructure/MalClient.fs`:
  - the `Provider` adapter (`mal-api` | `jikan`): paths, query shaping, parsers, errors;
  - the `X-MAL-CLIENT-ID` header;
  - 403 from MyAnimeList as temporary;
  - `MalSettings.Missing` without a client id;
  - the `Frunchy:Mal:*` settings.
- [X] T040 Add a `source` column on `mal_anime` and `mal_searches` (bootstrap + `ALTER … IF NOT EXISTS`). Stored
  bodies are re-read with their own provider's parser (`mal-rebuild`), and stored searches are reused
  only by the same provider. Adult entries are filtered in the matcher (`isAdult`).
- [X] T041 Worker and commands: the provider comes from `EnrichmentSettings`. The service stays off
  with a warning when the client id is missing. `mal-set` exits 4 without one. `CliEnv` carries the
  provider settings.
- [X] T042 Tests (223/223):
  - `FakeMal` (renamed) understands both providers' paths;
  - `MalFixtures` builds MyAnimeList API-shaped bodies;
  - new `MalApiModelTests` cover the parser, query shaping and settings;
  - `mal-set` without a client id is tested.

  Docs: spec clarification, research R13, `contracts/mal-usage.md`, data model, plan, quickstart,
  README, CLAUDE.md, SCHEMA.md.
- [X] T043 Record real fixtures and replay them over the whole 2026 sample.
  - **Recording**: an opt-in test, `FRUNCHY_RECORD_MAL=1 bella run -- dotnet test --filter-method
    "*record the 2026 sample*"`, runs the real worker against the real API through a recording
    handler, so the fixtures are exactly what Frunchy requests. That gave 191 responses, with no
    client id in them. A first, guessing Python recorder missed fallback searches and was replaced.
  - **Replay** (`SampleEnrichmentTests`): **76/78 seasons (97.4%) matched automatically**, all
    checked by hand in the report. The 2 left are Slime's "OVA Season 1" (several OVAs on
    MyAnimeList, ambiguous) and Dara-san of the Reiwa Era (MyAnimeList: "Dara-san of Reiwa", 63082;
    left for `mal-set`).
  - **Matching fixes found by the real data** (research R13):
    - `nsfw=true` on searches, since "gray" entries are hidden by default;
    - "(2020)" and "(TV)" markers dropped from queries;
    - a main-title match beats a synonym-only match;
    - the sequel walk steps through TV specials;
    - a season's own title is never cut on the Crunchyroll side;
    - several first-season hits narrow to TV/ONA;
    - OVA seasons also look at the series search's OVAs;
    - every match fetches and stores its anime.

## Dependencies & Execution Order

- **Setup (T001–T003)**: T002 and T003 run in parallel with T001. T003 needs the network once.
- **Foundational (T004–T012)**: T005, T006 and T009 in parallel with T004 → T007 (needs T003, T006)
  → T008 (needs T006) → T010 (needs T004, T006) → T011 (needs T008, T009) → T012 (needs T008,
  T009). This phase **blocks every story**.
- **US1 (T013–T019)**: tests T013–T015 in parallel → T016 → T017 → T018 → T019.
- **US2 (T020–T023)**: needs US1 (matched data to show). T021 runs in parallel with T020 → T022 →
  T023.
- **US3 (T024–T029)**: needs US1 (`resolveSeason`, `MalCatalog`). T025 and T026 in parallel → T027
  → T028 → T029. It's independent of US2.
- **US4 (T030–T031)**: needs US1's worker. It's independent of US2 and US3.
- **Amendment (T037–T043)**: after the polish phase. T043, T035 and T036 need the owner's MyAnimeList
  client id.
- **Polish (T032–T036)**: after the stories. T035 needs the owner's go-ahead (real Jikan, real
  database); T036 is the owner's by-hand check.

## Parallel Examples

```text
# Foundational
T005 SCHEMA.md   T006 Infrastructure/JikanModel.fs   T009 tests/.../Infrastructure/FakeJikan.fs

# US1 tests
T013 MalMatchingTests   T014 EnrichmentTests   T015 ResilienceTests (worker level)

# After US1, three streams
US2 (T020–T023 dashboard)   US3 (T024–T029 operator)   US4 (T030–T031 refresh)

# Polish
T032 README.md   T033 CLAUDE.md
```

## Implementation Strategy

1. **MVP = Phases 1–3 (US1)**: the catalog enriches itself, paced and resilient, with nothing
   visible yet but everything stored for 004.
2. **+ US2**: the dashboard shows scores, genres and "Top genres".
3. **+ US3**: the operator fixes what automatic matching couldn't.
4. **+ US4**: refresh keeps airing shows current.
5. **Polish**: docs, the first real enrichment run, and the owner's accuracy check.

## Notes

- Tests never call the real Jikan. Only T003 (recording fixtures) and T035 (the real run) use the
  network.
- Never let automatic code write a manual row. `MalCatalog`'s guard (T010) and the T024 tests enforce
  it.
- `Workers/ImportProcessor.fs` must not change (FR-002). The import and dashboard figures from 001
  must stay byte-for-byte identical (SC-009).
- The browser extension isn't affected, so no coordination tasks are needed (plan "Extension
  coordination").
- Commit after each task or logical group on `003-mal-enrichment`.
