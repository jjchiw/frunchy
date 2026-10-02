# Quickstart: validate feature 003

Contracts: [operator-cli](contracts/operator-cli.md), [dashboard](contracts/dashboard.md),
[mal-usage](contracts/mal-usage.md). Data rules: [data-model](data-model.md). Decisions:
[research](research.md).

## Prerequisites

Everything from features 001 and 002: .NET 10, Docker, and Postgres in `postgres-postgres-1`. The
automated tests need no network: the provider is replaced by `FakeMal` with MyAnimeList API-shaped
bodies (and, once recorded, real ones in `tests/FrunchyBackend.Tests/JsonSamples/mal/`).
Sections 3–4 use the real service and need a MyAnimeList client id: register an app at
https://myanimelist.net/apiconfig and store its Client ID in Bella as `Frunchy__Mal__ClientId`.

## 1. Automated validation (the gate)

```bash
dotnet test
```

Every feature 001 and 002 suite still passes (SC-009), plus:

| Suite | Proves |
|---|---|
| Title normalization (unit) | case, punctuation, dub and season markers; "2nd Season" = "Season 2"; generic season titles |
| Matching (unit, from fixtures) | tier 1 and 2 rules; first season among sequels; ambiguous when several; adult entries never candidates; Frieren S1 and S2, JUJUTSU KAISEN S1, S2, S3 (numbered 5) and the movie 0; dubs share their original |
| Enrichment worker (integration) | new seasons discovered without touching imports (FR-002); queue order (FR-002a); details, tags and relations stored with the raw response; a re-run sends zero requests (SC-005); user deletion leaves the catalog (FR-009) |
| Resilience (integration, fake service) | 429 and 504 retried up to 3 times honouring `Retry-After`; backoff doubles and is capped at 6 h (FR-014); 5 failures in a row → pause, then one probe (FR-015, `TimeProvider`); 404 is final; the pace never goes over the limit; a restart mid-lookup is picked up again |
| Refresh (integration, `TimeProvider`) | due after 30 days, or 7 while airing; unchanged hash → only `checked_at` moves; 404 → `missing_since`, details kept |
| Operator commands (integration) | `mal-status`, `mal-list`, `mal-show`, `mal-set` (unknown id → exit 3), `mal-none`, `mal-retry`; manual rows survive automatic re-runs (SC-008); `mal-rebuild` sends zero requests and is idempotent (SC-006) |
| Dashboard (integration) | score, genres and link on matched cards only; an unmatched card renders as before; "Top genres" counts equal a hand count over fully watched episodes in matched seasons (SC-007); hidden with no matched episodes; en and es; the four figures unchanged |

## 2. Local run with the fake service off (smoke)

```bash
dotnet run --launch-profile http
```

With the real database, the worker starts enriching (section 3). To look without calling MyAnimeList,
start with `--Frunchy:EnrichmentWorkerEnabled=false` and check that `/u/jjchiw` renders exactly
as before.

## 3. First real enrichment (SC-001, SC-002)

On the real `frunchydb` (501 seasons, 336 series). The first start creates the new tables; the
existing tables only get one index.

```bash
bella run -- python3 tests/FrunchyBackend.Tests/JsonSamples/mal/record.py   # optional: real fixtures (~10 min)
bella sdk run -- dotnet run --launch-profile http   # the worker starts at ~1 request/s
dotnet run -- mal-status                  # from another terminal, every few minutes
```

1. **Pace**: within about 30 minutes, `mal-status` shows no `pending` left apart from rows waiting
   on `next_attempt_at` after provider failures. The log shows no 429 caused by our pace (SC-002).
2. **Match rate**: matched ÷ (all seasons) ≥ 85% (SC-001). Record the numbers in the validation log.
3. **Accuracy**: pick 30 random automatic matches (`mal-show` on random series) and check each
   against MyAnimeList by hand. At most 1 may be wrong (SC-001).
4. **Known cases**: `mal-show` on JUJUTSU KAISEN's series id shows S1, S2, S3 and 0 each on their
   own entry, with the movie as type Movie. Frieren's S1 and S2 are on two entries. A dub season
   shows method `dub`.
5. **Dashboard**: `/u/jjchiw` shows scores and genres on the recent cards and a "Top genres"
   section. The four figures are still 327 / 5,921 / 2,367.6 h / 98.6 days.

## 4. Operator fixes

1. `dotnet run -- mal-list --status ambiguous` → pick one, then `mal-set <seasonId> <malId>`. Its
   card shows the anime on the next page view.
2. `dotnet run -- mal-retry <thatSeriesId>` → the manual row is kept (SC-008).
3. `dotnet run -- mal-rebuild` twice → both runs say "0 changed", and no request appears in the
   log (SC-006).

## 5. Outage behaviour (optional, by hand)

Start with `--Frunchy:Mal:BaseUrl=http://127.0.0.1:9/` (nothing listens there). After 5 failed
lookups the log shows a 5-minute pause, then a single probe. `mal-status` shows growing
`next_attempt_at` values. Restart with the real URL, and enrichment resumes without operator
action (SC-003).

## Validation log

- **2026-10-02 (T034)**: `dotnet test` gives **213/213**, with 0 build warnings.
  - **Earlier suites**: features 001 and 002 unchanged and green (159, including the extended schema
    test).
  - **New**: 54 tests. `JikanModelTests` (4, against the recorded Frieren response),
    `MalMatchingTests` (16), `EnrichmentTests` (6, the 2026 sample end to end through `FakeJikan`),
    `ResilienceTests` (11), `DashboardMalTests` (4), `MalOperationsTests` (8), `RefreshTests` (5).
  - **Smoke test**: on a throwaway database, `mal-status` and `mal-set` ran through the real CLI, and
    the web host started with "MyAnimeList enrichment worker started" and served `/`. The database
    was then dropped.
- **Jikan outage**: during the whole implementation session, MyAnimeList was unreachable through
  Jikan. Every search returned 504 "Jikan failed to connect to MyAnimeList", and so did any `/full`
  Jikan didn't have cached. This is the failure mode US1's scenarios 6–7 and FR-013 to FR-015 are
  for. Fixture recording (T003) and the first real run (T035) wait for it to recover.
- **Provider switch (research R13, T037–T042)**: MyAnimeList's official API is the default
  provider, and Jikan is the alternative. `dotnet test` gives **223/223**, with 0 build warnings:
  the 216 earlier tests, now on MyAnimeList-shaped bodies, plus 7 for the new parser, query
  shaping, settings and `mal-set` without a client id.
- **T003 (real fixtures), T035 (real run), T036 (accuracy check)**: waiting for the owner's
  MyAnimeList client id.
