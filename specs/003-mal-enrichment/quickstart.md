# Quickstart: validate feature 003

Contracts: [operator-cli](contracts/operator-cli.md), [dashboard](contracts/dashboard.md),
[jikan-usage](contracts/jikan-usage.md). Data rules: [data-model](data-model.md). Decisions:
[research](research.md).

## Prerequisites

Everything from features 001 and 002: .NET 10, Docker, and Postgres in `postgres-postgres-1`. The
automated tests need no network: Jikan is replaced by the recorded fixtures in
`tests/FrunchyBackend.Tests/JsonSamples/jikan/`. Sections 3–4 use the real service.

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

With the real database, the worker starts enriching (section 3). To look without touching Jikan,
start with `--Frunchy:EnrichmentWorkerEnabled=false` and check that `/u/jjchiw` renders exactly
as before.

## 3. First real enrichment (SC-001, SC-002)

On the real `frunchydb` (501 seasons, 336 series). The first start creates the new tables; the
existing tables only get one index.

```bash
dotnet run --launch-profile http          # the worker starts at ~1 request/s
dotnet run -- mal-status                  # from another terminal, every few minutes
```

1. **Pace**: within about 30 minutes, `mal-status` shows no `pending` left apart from rows waiting
   on `next_attempt_at` after Jikan failures. The log shows no 429 caused by our pace (SC-002).
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

Start with `--Frunchy:Jikan:BaseUrl=http://127.0.0.1:9/` (nothing listens there). After 5 failed
lookups the log shows a 5-minute pause, then a single probe. `mal-status` shows growing
`next_attempt_at` values. Restart with the real URL, and enrichment resumes without operator
action (SC-003).

## Validation log

- *(empty; filled in during and after implementation)*
