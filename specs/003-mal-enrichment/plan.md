# Implementation Plan: MyAnimeList Enrichment

**Branch**: `003-mal-enrichment` | **Date**: 2026-10-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/003-mal-enrichment/spec.md`

## Summary

A new background worker matches every Crunchyroll season, movies included, to a MyAnimeList entry
and stores the details in shared catalog tables. The imports aren't touched. The data comes from
MyAnimeList's official API v2 by default (a client id in Bella), or Jikan as an alternative. The
provider is a small adapter; everything else is provider-independent (amendment, research R13).

**Matching** (research R2):
- A series' first season is found by **search**, with exact, deterministic title tiers.
- Later seasons and movies are found by walking the matched entry's **relations** (sequel, prequel,
  side story…), with a search only as a fallback.
- **Dubs** share their original season's match.
- Anything uncertain is recorded as **ambiguous**, with its candidates, for the operator.

**Storage**: every Jikan response is stored verbatim (`mal_anime.raw`, `mal_searches.raw`), so
columns, tags, relations and automatic matches can be rebuilt with zero requests (`mal-rebuild`).

**Two layers of resilience**:
- **Short failures**: a named `HttpClient` with a `Microsoft.Extensions.Http.Resilience` pipeline.
  It has a total timeout, up to 3 retries with jitter honouring `Retry-After`, a token-bucket rate
  limiter at 1 request per second that every attempt passes through, and a per-attempt timeout.
- **Long outages**: the queue in Postgres. Match rows carry `attempts` and a `next_attempt_at` that
  doubles up to 6 h, and are claimed with `SKIP LOCKED` in the order of FR-002a. The worker pauses 5
  minutes after 5 failed lookups in a row, then sends one probe.

**Refresh**: every 30 days, or 7 while airing. Jikan sends no `ETag` in practice, so a hash of the
response detects changes.

**Dashboard**: the public dashboard gains the score, up to 3 genres and a MyAnimeList link on
recently-watched cards, plus a "Top genres" section counting fully watched episodes.

**Operator commands**: `mal-status`, `mal-list`, `mal-show`, `mal-set`, `mal-none`, `mal-retry`
and `mal-rebuild`.

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged)

**Primary Dependencies**:
- unchanged: Oxpecker 2.1.1 / ViewEngine 2.1 and Npgsql 10.0.3;
- **new**: `Microsoft.Extensions.Http.Resilience` **10.10.0** (first-party; brings Polly.Core,
  Polly.Extensions and Polly.RateLimiting 8.x; `HttpRetryStrategyOptions.ShouldRetryAfterHeader`
  verified in the DLL);
- built in: `IHttpClientFactory`, `System.Text.Json` and `TimeProvider`.

**Storage**: PostgreSQL. Six new tables (`mal_anime`, `mal_tags`, `mal_anime_tags`,
`mal_relations`, `mal_searches`, `cr_season_matches`) and one new index on `cr_episodes(season_id)`,
via the idempotent bootstrap.

**Testing**: the existing xUnit v3 + Testcontainers suite, plus:
- `FakeJikan`, a primary `HttpMessageHandler` serving **recorded real Jikan responses** and
  recording every request;
- `TimeProvider` for the backoff, the pause and the refresh;
- pure unit tests for normalization and matching.

**Target Platform**: unchanged (Kestrel; the worker runs in the web process).

**Project Type**: web service (server-rendered pages + JSON API + workers + CLI).

**Performance Goals**: first enrichment of the real catalog (about 900–1,000 requests) within 30
minutes at 1 request per second (SC-002). A new user's recent cards are enriched within 5 minutes.
Dashboard under 1 s with the service down (SC-004).

**Constraints**:
- at most 1 request per second per process, retries included (published limits: 3 per second, 60
  per minute);
- page views and imports never call the service;
- tests never call the real service;
- manual decisions are never overwritten;
- zero-request rebuild.

**Scale/Scope**: 501 seasons, 336 series, about 1,000 held anime eventually, a handful of users.

## Constitution Check

Gates from `.specify/memory/constitution.md` (v1.1.0). Checked before research and **re-checked
after design**; both are green.

- [x] **I. Core asset**:
  - raw imports and watch events are untouched;
  - the new derived tables state how they're rebuilt: automatic matches, columns, tags and
    relations from the stored Jikan responses via `mal-rebuild` with no requests; the stored
    responses from Jikan by the worker when missing; the Crunchyroll catalog from the raw imports
    (data-model, "Rebuild statement");
  - manual rows are operator input and are preserved.
- [x] **II. Boring tech**:
  - Postgres is the queue (status columns on `cr_season_matches` and `mal_anime`, `SKIP LOCKED`
    claims, as in feature 001);
  - the only new package is the first-party .NET resilience library the owner asked for;
  - no broker, scheduler, cache server or ORM;
  - one worker, sequential, sized to a few thousand requests in total.
- [x] **III. Vertical slices**:
  - the dashboard additions stay inside `Handlers/Users/Dashboard.fs`;
  - each operator command is one file in `Operations/`;
  - the worker is in `Workers/`;
  - the Jikan client, the matcher and the catalog store are in `Infrastructure/`, because the worker
    and the CLI share them, the same justification as `AccountActions` in feature 002;
  - `Program.fs` only wires them.
- [x] **IV. Idempotent async ingestion**:
  - the import path is unchanged, and external calls happen only in the worker and in `mal-set`;
  - calls are per distinct anime or title (searches and details stored and reused; dubs make none);
  - rate-limited below the published limits;
  - per-season status, attempts and error;
  - discovery and claims are idempotent.
- [x] **V. Tests**:
  - integration tests per worker behaviour, per operator command and for the dashboard, against real
    Postgres with the 2026 Crunchyroll sample and recorded real Jikan responses;
  - unit tests for normalization and matching.
- [x] **Constraints**:
  - every new label is in `Views/Texts.fs` (en and es), and MyAnimeList names aren't translated;
  - no frontend build;
  - schema in `Infrastructure/Database.fs`, with `SCHEMA.md` updated;
  - the package APIs used are verified in the pinned versions;
  - no secrets (Jikan needs no credentials).

No violations, so Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/003-mal-enrichment/
├── plan.md  research.md  data-model.md  quickstart.md
├── contracts/
│   ├── operator-cli.md   # mal-status / list / show / set / none / retry / rebuild
│   ├── dashboard.md      # card additions, "Top genres", hooks, texts
│   └── jikan-usage.md    # outgoing requests, pacing, response classification
└── tasks.md              # /speckit-tasks
```

### Source Code (repository root)

```text
FrunchyBackend.fsproj               # + Microsoft.Extensions.Http.Resilience 10.10.0; new files in compile order
appsettings.json                    # + Frunchy:Jikan:* and Frunchy:Enrichment:* defaults (research R11), EnrichmentWorkerEnabled
Infrastructure/
├── Database.fs                     # + 6 tables, the cr_episodes(season_id) index
├── JikanModel.fs                   # NEW: response records + parsing (search, full, error body); score 0 → None
├── MalMatching.fs                  # NEW, pure: normalize, season markers, dub detection, tiers, decide first season / linked / dub
├── MalCatalog.fs                   # NEW: SQL to store/read anime (raw, columns, tags, relations), searches, season matches
└── JikanClient.fs                  # NEW: `register config services` (named client + resilience pipeline); `Live` and `Stored` sources;
                                    #      response classification (ok / unchanged / notFound / definite / temporary)
Workers/
└── Enrichment.fs                   # NEW: discovery sweep, claim (FR-002a), resolve season, refresh, backoff, pause; processNext; EnrichmentService
Handlers/Users/Dashboard.fs         # + score/genres/link on matched cards, "Top genres" section (contracts/dashboard.md)
Views/Texts.fs                      # + TopGenres*, MyAnimeListLink, ScoreLabel (en, es)
Operations/
├── MalStatus.fs  MalList.fs  MalShow.fs  MalSet.fs  MalNone.fs  MalRetry.fs  MalRebuild.fs   # NEW, one per command
Program.fs                          # + JikanClient.register, EnrichmentService (unless disabled), mal-* CLI commands;
                                    #   the CLI gets a small service provider so mal-set uses the same paced client
tests/FrunchyBackend.Tests/
├── Infrastructure/FakeJikan.fs     # route table → recorded file | status | delay | exception; request log
├── JsonSamples/jikan/              # recorded real responses + record.sh (paced curl) to refresh them
├── MalMatchingTests.fs             # unit
├── EnrichmentTests.fs  ResilienceTests.fs  RefreshTests.fs  MalOperationsTests.fs  DashboardMalTests.fs
SCHEMA.md  README.md  CLAUDE.md     # updated in the polish phase
```

**Structure Decision**: the same single project.
- **Compile order**: Domains → Infrastructure (…, `WebAuth`, then `JikanModel`, `MalMatching`,
  `MalCatalog`, `JikanClient`) → Views → Handlers → Operations (existing) → `Workers/Enrichment.fs`
  → `Operations/Mal*` → `Workers/ImportProcessor.fs` → Program. The enrichment worker compiles
  before the mal-* commands because `mal-rebuild` reuses its `resolveAndWrite` (changed during
  implementation).
- **CLI dispatch**: the operator commands currently take `DbConfig -> string list -> Task<int>`.
  The mal-* commands that need the client (`mal-set`) get it through a small `CliEnv` record
  (`Db`, `Services`) that `runCli` builds. The existing commands are adapted mechanically and keep
  their behaviour.

## Extension coordination

Not affected: import API v1.2, the sync-key auth and who-am-I don't change, and the extension
calls nothing new. Under the coordination rule, no message, phase or task is needed for
`browser-extension-c7`.

## Complexity Tracking

> No Constitution Check violations, so nothing to justify.
