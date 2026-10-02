# Research: MyAnimeList Enrichment

Decisions for [plan.md](plan.md). Sources:
- Jikan's OpenAPI document (`jikan-rest/storage/api-docs/api-docs.json`, the same content as the
  printed docs page);
- live probes made while designing (a handful of requests on 2026-10-02);
- the real `frunchydb` catalog (read-only queries);
- the `Microsoft.Extensions.Http.Resilience` 10.10.0 package.

## R1. Jikan endpoints and what each is used for

**Decision**: two endpoints only.

| Call | Used for | Cached by Jikan? |
|---|---|---|
| `GET /v4/anime?q={title}&limit=10&sfw` | finding candidates for a first season, or a season with no linked entry | no (the query goes to MyAnimeList) |
| `GET /v4/anime/{id}/full` | the details stored for a matched anime, including `relations` to walk to later seasons and movies | yes, 24 h |

**Facts the design relies on**:
- **Search results** use the same `anime` shape as the details: `titles[]` (Default, Synonym,
  Japanese, English), `type`, `aired`, `year`, `season`, `genres`, `themes`, `demographics`,
  `studios` and `score`. A candidate can therefore be judged without another request.
- **`relations` appear only in `/full`**, as `[{ relation, entry: [{ mal_id, type, name }] }]`.
  Frieren lists Sequel → "Sousou no Frieren 2nd Season", plus Side Story, Other and Adaptation (the
  manga).
- **`sfw` filters out adult entries** (rating `Rx`), which covers FR-005.
- **Errors** are `{ status, type, message, error, report_url }` with 400, 404, 429, 500 or 503. The
  live probe also got **504** "Jikan failed to connect to MyAnimeList" on two searches in a row,
  while `/anime/52991/full` was served from Jikan's cache (`Expires` and `Last-Modified` headers).
- **Missing values**: a score of `0` or `null` means no score. Absent arrays are empty.

**Alternatives**:
- `/anime/{id}/relations` instead of `/full`: it costs an extra request per anime, for data `/full`
  already includes.
- The official MyAnimeList API: it needs a client id and registration, and has no published limits.
  Jikan is what the owner chose.

## R2. Matching rules (FR-003 to FR-004)

**Decision**: deterministic, title-based tiers, per Crunchyroll season.

1. **Normalize** titles for comparison:
   - lowercase; remove accents and punctuation (`:` `–` `-` `!` `?` `'` `.`); collapse spaces;
   - drop dub markers: `(english dub)`, `(spanish dub)`, any `(… dub)` or `(… audio)`;
   - drop leading "the"; drop the words "movie", "the movie", "film".

   Then extract a **season marker** separately:
   - `season n`, `nth season`, `part n`, and roman numerals at the end;
   - "2nd Season" and "Season 2" are equivalent.

   The marker is kept as a number and compared on its own; it's never part of the text comparison.
2. **First season of a series**: the lowest-numbered original-language season (no dub marker) that
   isn't a movie listing. Search with the series title, and also with the season title when that
   isn't generic ("Season 1", "Season 01").

   Candidates are tried in tiers, and the first tier with any hit decides:
   - **Tier 1**: the normalized series title equals one of the candidate's normalized titles.
   - **Tier 2**: the same, after cutting both at the first `:` (subtitles dropped). This covers
     "ROLL OVER AND DIE" against "Roll Over and Die: I Will Fight for…".

   Then:
   - only formats TV, ONA, OVA or Special qualify (a movie listing only matches a Movie);
   - with exactly one qualifying candidate, it's matched;
   - with several, the earliest-aired one is fetched (`/full`) and its sequel chain walked (R3). If
     every other qualifier is in that chain, the earliest is the first season. Otherwise the season
     is **ambiguous**, with up to 5 candidates kept.
   - With none, it's **no match**.
3. **Other seasons and movies of the series** (FR-003a): breadth-first from the matched first season
   over `Sequel`, `Prequel`, `Side Story`, `Parent Story` and `Summary` links. Each linked entry is
   fetched with `/full`, at most 20 per series. A season is matched to a linked entry when:
   - its title, without dub and season markers, equals one of the entry's titles (tiers 1 and 2); or
   - its title is generic ("Season 3"), and the entry is the Nth TV/ONA in the sequel chain counted
     from the first season, where N is the title's marker. N never comes from `season_number`
     (`JUJUTSU KAISEN Season 3` is numbered 5); or
   - it's a movie listing (the format is Movie, or the title has "movie"/"film"), and exactly one
     linked Movie entry matches its title tiers.

   When no linked entry qualifies, the season is searched by its own title, with the same tiers.
4. **Dubbed seasons** (FR-003b): the dub markers are removed from the title. The season shares the
   match of the original-language season with that title in the same series, or with the same
   `season_number` when the title is generic. It makes no requests. A dub with no original is matched
   like an original.

**Rationale**:
- Exact rules can be tested and give the same decisions when re-run (FR-021). Fuzzy scoring would
  need thresholds tuned on data we don't yet have, and could mis-match silently.
- Real titles are well served by tiers 1 and 2: Crunchyroll mostly uses MyAnimeList's English title
  (Frieren), and the subtitle cut covers long light-novel titles.
- Ambiguous cases go to the operator (User Story 3) instead of being guessed.

**Alternatives**:
- Similarity scores (Levenshtein, Jaro-Winkler) with a threshold: rejected for now, because the
  confidence is opaque. They can be added as a tier 3 later, with its own SC-001 check.
- Matching seasons by `season_number`: rejected, because numbering is irregular in the real data.

**Validation**: SC-001 (85% automatic, at most 1 wrong in 30) is measured on the real catalog in
quickstart §3. If the rate falls short, tier rules are added in a follow-up, without changing the
storage.

## R3. Request budget for the real catalog

**Catalog**: 501 seasons in 336 series. 43 of the seasons are dubs in other languages and make no
requests. Movies appear as seasons: "JUJUTSU KAISEN 0" is season 0, and "BLUE LOCK THE MOVIE
-EPISODE NAGI-" is season 2.

**Estimate**:
- about 336 searches, plus about 336 `/full` fetches for first seasons;
- about 150–300 `/full` fetches for linked entries, capped at 20 per series;
- a few dozen fallback searches.

That's roughly **900–1,000 requests, 15–17 minutes at 1 per second**, within SC-002's 30 minutes.
Refreshes then cost about 1 request per held anime every 30 days (7 while airing), which is a
few per day.

## R4. Short retries and pacing: `Microsoft.Extensions.Http.Resilience`

**Decision**: a named `HttpClient` ("jikan") with a custom pipeline from
`Microsoft.Extensions.Http.Resilience` **10.10.0**. It's first-party, built on Polly v8, and brings
`Polly.RateLimiting`. `HttpRetryStrategyOptions.ShouldRetryAfterHeader` was verified in the pinned
DLL.

The pipeline, outermost first:
1. **Total timeout**: 60 s.
2. **Retry**: up to 3 retries, exponential backoff with jitter, base 2 s, honouring `Retry-After`.
   It retries 408, 429, 500, 502, 503, 504, `HttpRequestException` and attempt timeouts. It doesn't
   retry 400 or 404 (FR-013).
3. **Rate limiter**: a token bucket of 1 token per second, bucket size 1, queue limit 100. It sits
   inside the retry, so every attempt, retries included, takes a permit (FR-010).
4. **Attempt timeout**: 15 s.

Every value comes from `Frunchy:Jikan:*` configuration, so the tests can make them instant.

**Rationale**:
- These are the "resilience and retry" the owner asked for, using the standard .NET building blocks
  and no hand-written retry loop.
- One pipeline instance per named client, so all of a process's requests share one rate limiter.

**Not used**:
- Polly's circuit breaker. The spec's pause ("5 failed lookups in a row → 5 minutes → one probe",
  FR-015) is about whole lookups, not single HTTP attempts. It's implemented in the worker (R5) with
  the injected `TimeProvider`, which makes it exact and testable.
- The standard resilience handler (`AddStandardResilienceHandler`): its rate limiter is a
  concurrency limiter, not a per-second one, and its retry defaults differ.

**Multiple processes**: an operator CLI command has its own limiter. It makes at most a couple of
requests (`mal-set` fetching one anime), so the combined rate stays well under 3 per second.

## R5. Long outages: durable retry in Postgres (FR-014 to FR-017)

**Decision**: the work queue is the match table itself (`cr_season_matches`, see the data model),
with `status`, `attempts`, `next_attempt_at`, `claimed_at`, `requested_at` and `last_error`.
Refreshes use the same columns on `mal_anime`. Postgres is the queue, as in feature 001.

- **When the HTTP pipeline gives up** on a temporary error (`TransientJikanError`): `attempts += 1`
  and `next_attempt_at = now + min(6 h, 1 min × 2^(attempts−1)) × jitter(0.8–1.2)`. The status stays
  `pending`, and a `failed` reason is shown to the operator once `attempts ≥ 3`.
- **A definite outcome** (match, ambiguous, no match, a 404 by id) is final. `next_attempt_at` is
  cleared (FR-016).
- **Claiming**: stale claims older than 10 minutes are reset (as in feature 001), then one row is
  claimed with `FOR UPDATE SKIP LOCKED`, in the order of FR-002a:
  1. operator requests (`requested_at` set);
  2. new lookups, by the season's latest watch across all users (`max(watched_at)` over
     `watch_events` joined through `cr_episodes.season_id`);
  3. refreshes, oldest `checked_at` first.

  Rows waiting for their `next_attempt_at` are skipped.
- **The pause** (FR-015): the worker counts lookups in a row that ended in `TransientJikanError`.
  At 5 it sleeps 5 minutes (configurable) and then processes one job as the probe. If the probe also
  fails, it sleeps again; if it succeeds, the counter resets. The sleep uses the `TimeProvider`.
- **Restarts**: all state is in the database. The in-memory pause starts again at zero after a
  restart, and the per-row `next_attempt_at` still spaces out requests.

**Alternatives**:
- Hangfire, Quartz or a broker: forbidden by Principle II, and not needed.
- A separate `jobs` table: an extra join with no gain. The match row already has exactly one
  pending action.

## R6. Discovering new seasons without touching the import (FR-002)

**Decision**: at the start of each poll, the enrichment worker inserts a `pending` match row for every
`cr_seasons` row that has none: `INSERT … SELECT … WHERE NOT EXISTS`, idempotent. In the same sweep
it re-queues automatic `no_match` and `ambiguous` rows whose `title_seen` differs from the current
series and season titles (the title-change edge case).

**Rationale**: `Workers/ImportProcessor.fs` doesn't change, so imports can't be slowed or broken by
enrichment (FR-002, SC-004). The sweep costs one cheap statement per poll at this scale.

**Alternative**: inserting the row in the import's transaction. It couples the two workers, and the
sweep is needed anyway for the existing 501 seasons.

## R7. Change detection on refresh (FR-012, User Story 4)

**Fact**: the probe's `/full` responses carried `Expires`, `Last-Modified` and
`X-Request-Fingerprint`, but **no `ETag`**, although the docs promise one.

**Decision**:
- When an `ETag` was stored, send `If-None-Match`, and treat `304` as unchanged.
- Otherwise, compare the SHA-256 of the `data` object with the stored hash. When it's equal, only
  `checked_at` moves; when it differs, the response, columns and tags are replaced in one
  transaction.

Either way, a refresh costs at most one request per anime.

## R8. Rebuilding without requests (FR-021, Principle I)

**Decision**: the matcher reads through an interface with two implementations:
- `Live`: the "jikan" client, which also stores every response;
- `Stored`: reads only `mal_searches` and `mal_anime.raw`, and answers "unknown" for anything not
  held.

`mal-rebuild` runs in two steps:
1. Re-derive `mal_anime` columns and tags from `raw`.
2. Re-decide every automatic match with `Stored`. Manual rows are untouched. A season whose decision
   needs data not held is reported and left as it is.

It sends zero requests (SC-006) and is idempotent.

**How each derived table is rebuilt**: the catalog from the raw imports (feature 001), the stored
Jikan responses from Jikan itself (the worker re-fetches what's missing), and matches and tags from
both, with `mal-rebuild`.

## R9. Testing without the real service (constitution V)

**Decision**: the test host replaces the "jikan" client's primary handler with `FakeJikan`, a route
table from request path and query to a recorded response file (or a status code, a delay or an
exception). It also records every request, so tests can assert "zero requests" (SC-005, SC-006) and
count retries.

The fixtures are real responses recorded once with a small script:
- Frieren (search, S1, S2);
- JUJUTSU KAISEN (search, S1, S2, the movie 0, S3);
- an ambiguous title, a "no match" search and a 404 by id.

They're stored in `tests/FrunchyBackend.Tests/JsonSamples/jikan/`. They're public catalog data,
with no personal data.

**Test settings**: `Frunchy:Jikan:*` makes rate and backoff instant (rate 1000 per second, base
delay 1 ms), except in the one test that measures pacing. The pause test advances a `TimeProvider`
instead of waiting.

## R10. Dashboard additions (FR-018, FR-018a)

**Decision**: both additions go in the existing `Handlers/Users/Dashboard.fs` slice.
- **Recently-watched cards**: the query gets a left join `cr_episodes.season_id → cr_season_matches
  (status matched) → mal_anime`, plus a lateral subquery for the first 3 genres by position. A
  missing match gives `None`, and the card renders as today.
- **"Top genres"**: one query over the user's `watch_events` that are `fully_watched` and have a
  known duration (the rule of the "Episodes watched" figure), joined to matched seasons and to genre
  tags, giving the top 5 by count (ties by name). A second count gives coverage: fully watched
  episodes in matched seasons over all fully watched episodes.
- **Texts**: new `Views/Texts.fs` entries in en and es. Genre names come from MyAnimeList and aren't
  translated.

**Performance**: about 6k watch events per user today; both queries are a few milliseconds with the
existing primary keys plus an index on `cr_episodes(season_id)` (SC-004).

## R11. Hosting and configuration

**Decision**: `Workers/Enrichment.fs` contains:
- `processNext db jikan clock : Task<bool>`, called directly by the tests, like
  `ImportProcessor.processNext`;
- `EnrichmentService`, a `BackgroundService` that loops on `processNext`, sleeps 5 s when idle,
  and applies the R5 pause.

It's registered unless `Frunchy:EnrichmentWorkerEnabled=false`, which the tests set, like
`Frunchy:ImportWorkerEnabled`.

| Key | Default | Purpose |
|---|---|---|
| `Frunchy:EnrichmentWorkerEnabled` | `true` | run the background worker |
| `Frunchy:Jikan:BaseUrl` | `https://api.jikan.moe/v4/` | the service, or the fake in tests |
| `Frunchy:Jikan:RequestsPerSecond` | `1` | the token bucket (FR-010) |
| `Frunchy:Jikan:RetryBaseDelay` | `00:00:02` | base delay of the short retries |
| `Frunchy:Jikan:AttemptTimeout` | `00:00:15` | per attempt |
| `Frunchy:Enrichment:PauseAfterFailures` | `5` | FR-015 |
| `Frunchy:Enrichment:PauseFor` | `00:05:00` | FR-015 |
| `Frunchy:Enrichment:RefreshAfterDays` | `30` | FR-022 |
| `Frunchy:Enrichment:RefreshAiringAfterDays` | `7` | FR-022 |

None of these are secrets; they live in `appsettings.json` and the tests override them.

## R12. Browser extension

The import contract v1.2 doesn't change, and nothing the extension calls changes. Under the
coordination rule, no message or task is needed for the extension session.

## R13. Amendment: MyAnimeList's official API replaces Jikan as the default provider

**Context** (2026-10-02, during implementation):
- MyAnimeList was unreachable through Jikan for the whole implementation session. Every search, and
  every `/full` not in Jikan's cache, returned 504 "Jikan failed to connect to MyAnimeList".
- myanimelist.net itself answered 200 at the same time.
- The owner reported that Jikan is shutting down.

**Options looked at**:
- **jikan-edge** (`jikan.lucashdo.com`, MIT, a Cloudflare Worker): it keeps serving cached copies
  while MyAnimeList scraping fails, and allows 60 requests per minute. But it isn't Jikan-compatible:
  camelCase fields, a different envelope and errors, and it refuses `limit` and `sfw`. Its search
  results carry only the romaji title (no English title, synonyms, dates or rating), so every
  candidate would need a details request, about 2–3 times the requests. It's also one person's
  service scraping MyAnimeList's HTML.
- **MyAnimeList's official API v2** (`api.myanimelist.net/v2`): it needs a free client id
  (`X-MAL-CLIENT-ID`). Search returns the requested fields, including the English title and
  synonyms, so candidates are judged in one request. Details include related anime, studios,
  format, start date, score and rating. There are no published rate limits.

**Decision**: the provider is a small adapter (`Frunchy:Mal:Provider`), and the default is the
official API (`mal-api`). The owner registered an app and keeps the client id in Bella Baxter
(`Frunchy__Mal__ClientId`). Jikan stays as `jikan`.
- **Unchanged**: the matcher, queue, pacing, retries, storage, dashboard and commands. They only see
  the shared `MalModel` records.
- **Per provider**: paths, the parser (`MalApiModel` / `JikanModel`), error-body reading and
  query shaping. MyAnimeList refuses very long queries, so they're cut at the subtitle; the matcher
  compares full titles anyway.
- **Stored bodies** record their provider (`source`), so `mal-rebuild` re-reads each with its own
  parser.

**Mapping notes** (contracts/mal-usage.md):
- MyAnimeList's `genres` mixes genres, themes and demographics. They're split by MyAnimeList's
  stable genre ids.
- 403 means either a bad client id or DoS protection, so it's temporary and visible to the operator.
- Adult entries are excluded by the matcher itself (rating "Rx", or the Hentai genre), whatever the
  provider.

**Fixtures**: `tests/FrunchyBackend.Tests/JsonSamples/mal/record.py` records real responses for the
2026 sample. It reads the client id from the environment, e.g. under `bella run`, and never stores
it. Until it has run, the integration tests use MyAnimeList-shaped bodies from
`MalFixtures.fs`.
