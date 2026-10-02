# Data Model: MyAnimeList Enrichment

All new tables are **shared catalog data**: none references `users`, so user deletion never
touches them (FR-009). They're created by the idempotent `initializeSchema` in
`Infrastructure/Database.fs` and documented in `SCHEMA.md`. Existing tables are unchanged, apart
from one new index.

```
cr_series 1─* cr_seasons 1─1 cr_season_matches *─0..1 mal_anime 1─* mal_anime_tags *─1 mal_tags
                                       │ shared_from (dub → original season)    1─* mal_relations
mal_searches (stored search responses, by query)
```

## mal_anime: one MyAnimeList anime

| Column | Type | Notes |
|---|---|---|
| `mal_id` | `int` PK | MyAnimeList id |
| `url` | `text` NOT NULL | MyAnimeList page, used for the attribution link |
| `title` | `text` NOT NULL | `titles[type=Default]`, e.g. "Sousou no Frieren" |
| `title_english` | `text` NULL | e.g. "Frieren: Beyond Journey's End" |
| `title_japanese` | `text` NULL | |
| `synonyms` | `text[]` NOT NULL DEFAULT `'{}'` | `titles[type=Synonym]` |
| `type` | `text` NULL | TV, Movie, OVA, ONA, Special, TV Special, Music… as given |
| `episodes` | `int` NULL | |
| `status` | `text` NULL | e.g. "Finished Airing", "Currently Airing" |
| `airing` | `boolean` NOT NULL | sets the refresh age (7 or 30 days) |
| `aired_from` | `date` NULL | start date; decades and "earliest aired" ordering |
| `year` / `season` | `int` NULL / `text` NULL | e.g. 2023 / "fall" |
| `score` | `numeric(4,2)` NULL | a score of `0` or `null` from the service is stored as NULL ("no score") |
| `image_url` | `text` NULL | `images.webp.image_url`, or the jpg one |
| `raw` | `json` NOT NULL | the full `/full` response body, verbatim |
| `data_hash` | `text` NOT NULL | SHA-256 of `data` (change detection, research R7) |
| `etag` | `text` NULL | stored only when the service sends one |
| `fetched_at` | `timestamptz` NOT NULL | when the current `raw` was received |
| `checked_at` | `timestamptz` NOT NULL | last time the service confirmed it (fetch, or an unchanged refresh) |
| `missing_since` | `timestamptz` NULL | a refresh got 404; the last known details are kept (User Story 4, scenario 4) |
| `refresh_attempts` | `int` NOT NULL DEFAULT 0 | refresh queue (research R5) |
| `refresh_next_at` / `refresh_claimed_at` / `refresh_requested_at` | `timestamptz` NULL | |
| `refresh_error` | `text` NULL | last temporary failure |

**Due for refresh**: `checked_at < now − (airing ? 7 : 30) days`, and `missing_since IS NULL`, and
(`refresh_next_at IS NULL` or `refresh_next_at ≤ now`).

## mal_tags and mal_anime_tags: genres, themes, demographics, studios

`mal_tags` (`kind`, `tag_id`) PK, `name text NOT NULL`:
- `kind` ∈ `genre | explicit_genre | theme | demographic | studio`;
- `tag_id` is MyAnimeList's id within that kind;
- names are kept in MyAnimeList's English and are never translated (FR-019).

`mal_anime_tags` (`mal_id`, `kind`, `tag_id`) PK:
- `position int NOT NULL` keeps MyAnimeList's order, which gives "up to 3 genres in MyAnimeList's
  order" (FR-018);
- FK `mal_id → mal_anime ON DELETE CASCADE`, FK (`kind`, `tag_id`) → `mal_tags`.

They're rewritten in the same transaction as `mal_anime` whenever `raw` changes, and are fully
re-derivable from `raw` (`mal-rebuild`).

## mal_relations: links between MyAnimeList entries

(`mal_id`, `relation`, `related_id`) PK, `related_name text NOT NULL`:
- `relation` as given, e.g. Sequel, Prequel, Side Story, Parent Story, Summary, Other,
  Alternative Version;
- only `entry.type = anime` is kept;
- FK `mal_id → mal_anime ON DELETE CASCADE`. `related_id` has **no FK**, because the related
  anime may not be held yet.

Derived from `raw` and rewritten with it. It's used to walk from a first season to its later
seasons and movies (research R2.3).

## mal_searches: stored search responses

| Column | Type | Notes |
|---|---|---|
| `query` | `text` PK | the exact `q` sent, after trimming |
| `raw` | `json` NOT NULL | the response body, verbatim |
| `searched_at` | `timestamptz` NOT NULL | |

Only the latest response per query is kept. A new search of the same query replaces it. It's used
to show candidates (FR-008) and by `mal-rebuild`'s stored-only matching (FR-021).

## cr_season_matches: one row per Crunchyroll season (match and work queue)

| Column | Type | Notes |
|---|---|---|
| `season_id` | `text` PK, FK `cr_seasons(id)` ON DELETE CASCADE | |
| `status` | `text` NOT NULL | `pending` \| `matched` \| `ambiguous` \| `no_match` \| `failed` |
| `mal_id` | `int` NULL, FK `mal_anime` | set only when `matched` |
| `method` | `text` NULL | `search` \| `link` \| `dub` \| `manual`; NULL while pending |
| `shared_from` | `text` NULL, FK `cr_seasons(id)` | for `dub`: the original-language season it follows |
| `candidates` | `jsonb` NULL | up to 5 `{ malId, title, type, year }` for `ambiguous`, also kept for `no_match` when search had results |
| `reason` | `text` NULL | a short machine-readable reason, e.g. `several-first-seasons`, `no-title-match`, `not-found`, `manual-none` |
| `title_seen` | `text` NULL | `series title ‖ season title` at decision time (re-queue on title change) |
| `decided_at` | `timestamptz` NULL | |
| `attempts` | `int` NOT NULL DEFAULT 0 | temporary failures only |
| `next_attempt_at` | `timestamptz` NULL | backoff (FR-014) |
| `claimed_at` | `timestamptz` NULL | worker claim; stale after 10 minutes |
| `requested_at` | `timestamptz` NULL | operator `mal-retry` or `mal-set` (goes first, FR-002a) |
| `last_error` | `text` NULL | last temporary failure, for the operator |
| `created_at` | `timestamptz` NOT NULL DEFAULT `now()` | |

**Constraints**:
- `CHECK ((status = 'matched') = (mal_id IS NOT NULL))`;
- `CHECK (method <> 'dub' OR shared_from IS NOT NULL)`.

**Manual**: `method = 'manual'`, either `matched` (set by hand) or `no_match` with `reason =
'manual-none'`. Automatic decisions never write to a manual row (FR-006).

### State transitions

```
            (sweep: new season)            definite outcome
   ∅ ───────────────────────────▶ pending ───────────────────▶ matched | ambiguous | no_match
                                    │  ▲                               │ (automatic only)
             temporary failure      │  │ next_attempt_at reached       │ title changed, or
                                    ▼  │                               │ operator mal-retry
                                  failed (attempts ≥ 3) ───────────────┘──────▶ pending
   operator mal-set  ──▶ matched (method manual)       operator mal-none ──▶ no_match (manual)
```

- `pending` with attempts 1–2 stays `pending`. It becomes `failed` from the third temporary failure
  on. Both are claimable once `next_attempt_at` is reached.
- A dub row follows its original: when the original becomes `matched`, the dub is set to `matched`
  (`method dub`, `shared_from`) in the same transaction. When the original is re-decided, its dubs
  are re-decided with it.

## Index added to an existing table

`CREATE INDEX IF NOT EXISTS cr_episodes_season_idx ON cr_episodes (season_id)` speeds up the
dashboard joins and the "latest watch per season" ordering (research R5, R10).

## Rebuild statement (constitution I)

- `cr_season_matches` (automatic rows), `mal_anime` columns, `mal_anime_tags` and `mal_relations`
  are **derived**. `mal-rebuild` re-derives them from `mal_anime.raw` and `mal_searches.raw` with no
  requests, and gives identical results when run twice (FR-021, SC-006).
- `mal_anime.raw` and `mal_searches.raw` are the stored copies of the external source. When missing
  they're re-fetched by the worker. The Crunchyroll catalog they're matched against is rebuilt from
  the raw imports (feature 001).
- **Manual rows** are operator input, not derived, and are preserved by every rebuild.
