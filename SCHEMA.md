# Database Schema

The source of truth is the `initializeSchema` script in `Infrastructure/Database.fs`. It runs on
every app start and before every operator CLI command, and it's idempotent (`CREATE … IF NOT
EXISTS`, no migration framework). Keep this document in sync with it. The design rationale is in
`specs/001-crunchyroll-history-import/data-model.md` and `research.md`.

> **Legacy layout**: databases created before feature 001 (tables `import_records`, `users.email`,
> `cr_imports.payload`) held only placeholder data. The bootstrap detects them and stops. Reset
> once with `dropdb frunchydb && createdb frunchydb`.

## Data classes

| Class | Tables | Rule |
|---|---|---|
| **Personal, raw** | `cr_imports`, `cr_import_pages` | stored exactly as received and never updated; deleted only when their user is deleted (constitution I) |
| **Personal, derived** | `watch_events`, `cr_import_entry_issues`, import counts | rebuildable from the raw pages (`user-rebuild`) |
| **Identity** | `users` | the sync key is stored only as a SHA-256 hash |
| **Shared catalog** | `cr_series`, `cr_seasons`, `cr_episodes` | public Crunchyroll metadata, shared by all users; never deleted |
| **Shared catalog, external** (feature 003) | `mal_anime`, `mal_searches` (raw), `mal_tags`, `mal_anime_tags`, `mal_relations`, `cr_season_matches` | MyAnimeList data (official API or Jikan); nothing references `users`, so user deletion never touches it |

Everything personal hangs off `users` with `ON DELETE CASCADE`, so `user-delete` removes all of it.

## Identity

### `users`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `username` | text UNIQUE | `^[a-z0-9][a-z0-9_-]{2,29}$`, lowercase; used in `/u/{username}` |
| `display_name` | text | |
| `sync_key_hash` | text UNIQUE NULL | lowercase hex SHA-256 of `frk_…`; the key itself is never stored; NULL until a key is generated |
| `sync_key_created_at` | timestamptz | |
| `cr_account_id`, `cr_profile_id` | text NULL | Crunchyroll history owner, bound on the first accepted submission |
| `created_at` | timestamptz | |

`ux_users_history_owner` on `(cr_account_id, coalesce(cr_profile_id, ''))`: one history owner
belongs to at most one user.

## Accounts (feature 002)

| Table | Key | Notes |
|---|---|---|
| `user_identities` | `(provider, provider_user_id)` | a sign-in identity (`google`, `discord`) belongs to exactly one user; same shape as ASP.NET Core Identity's user logins. Personal, cascades on user delete |
| `invites` | `id`, unique `code_hash` | one-time sign-up codes (SHA-256 only), `expires_at` = created + 7 days, `revoked_at`, `used_at`, `used_by_user_id` (`ON DELETE SET NULL`, so an invite stays "used" after its account is deleted). Not personal data |
| `security_events` | identity `id` | per-user account events (`signed_in`, `claimed`, `claim_refused`, `identity_linked`/`unlinked`, `sync_key_generated`, `binding_reset`, `reconfirmed`, `display_name_changed`, `account_created`) with `actor` user/operator. No IP or device data. Personal, cascades on user delete |

`users.sync_key_hash` and `users.sync_key_created_at` are **nullable**: a user who signed up on the
website has no key until "Connect extension". Sessions aren't stored (encrypted cookies), and
provider tokens, emails and avatars are never stored.

## Tracking

### `cr_imports`: one history submission

`status` is one of `pending → processing → completed | failed`. The columns `processed`,
`unresolved`, `unsupported` and `failed` add up to `entries_received`. `error` is set only when
the whole submission failed. Indexes: `(status, received_at)` for the worker claim, and
`(user_id, received_at DESC)` for last sync.

### `cr_import_pages`: raw pages

`(import_id, page_index)` PK, plus `url`, `captured_at`, and `body` as **`json`**, the exact text
received. `jsonb` would reorder keys and drop duplicates.

### `cr_import_entry_issues`

One row per entry that wasn't processed: `outcome` is `unresolved` (no panel), `unsupported`
(not an episode) or `failed` (invalid), with a human-readable `reason`.

### `watch_events`: derived ledger

PK `(user_id, cr_episode_id)`, one row per user and episode. It holds the newest watch state
`(watched_at, fully_watched, playhead_s)`; older or equal states never overwrite it, so arrival
order doesn't matter. Index `(user_id, watched_at DESC)`.

## Catalog

| Table | Key | Notes |
|---|---|---|
| `cr_series` | `id` = entry `parent_id` | title only from entries where `series_id = parent_id` (franchise movies don't rename the series), so a season's series can have **no row** when only its movies or specials were watched; code joining it must `LEFT JOIN` |
| `cr_seasons` | `id` = `season_id` | dub variants are separate seasons |
| `cr_episodes` | `id` = entry `id` | `duration_ms IS NULL` means **unresolved**: a stub created from an entry without panel, filled in when details arrive |

Catalog rows keep `source_at` (page `capturedAt`, else the import's `received_at`) and are only
overwritten by data at least as recent.

## MyAnimeList enrichment (feature 003)

The design is in `specs/003-mal-enrichment/data-model.md`. All of it is shared catalog data.

| Table | Key | Notes |
|---|---|---|
| `mal_anime` | `mal_id` | one MyAnimeList anime. `raw` holds the provider's details response verbatim (`json`), and `source` says which provider (`mal-api` or `jikan`); the columns are derived from it. A score of 0 is stored as NULL. `data_hash` detects changes on refresh (Jikan sends no ETag in practice); `missing_since` is set when a refresh gets 404, and the last details are kept. The `refresh_*` columns are its refresh queue |
| `mal_tags` | (`kind`, `tag_id`) | genre, explicit genre, theme, demographic or studio, with MyAnimeList's English name (never translated) |
| `mal_anime_tags` | (`mal_id`, `kind`, `tag_id`) | `position` keeps MyAnimeList's order |
| `mal_relations` | (`mal_id`, `relation`, `related_id`) | anime-to-anime links (Sequel, Prequel, Side Story…). `related_id` has no FK |
| `mal_searches` | `query` | the latest search response per query, verbatim (`json`), with its `source` provider |
| `cr_season_matches` | `season_id` | one row per Crunchyroll season: `status` (pending, matched, ambiguous, no_match, failed), `mal_id`, `method` (search, link, dub, manual), the candidates, and the queue columns (`attempts`, `next_attempt_at`, `claimed_at`, `requested_at`, `last_error`). Rows with `method = 'manual'` are never changed by automatic matching |

Index `ix_cr_episodes_season` on `cr_episodes (season_id)` serves the dashboard joins and the
queue's "latest watch per season" ordering.

**Rebuild**: `mal-rebuild` re-derives the `mal_anime` columns, tags and relations from `raw`, and
re-decides every automatic match from the stored responses, with no requests. Missing responses
are fetched again by the worker. Manual rows are operator input and are kept.

## Anime DNA (feature 004)

No table. `GET /u/{username}/dna` reads `watch_events`, `cr_episodes`, `cr_series` (LEFT JOIN),
`cr_season_matches`, `mal_anime`, `mal_anime_tags` and `mal_tags` at request time, so it is always
consistent with `user-rebuild` and `mal-rebuild`.

## Dashboard figures

Computed per request, no projection tables. Only resolved episodes (`duration_ms IS NOT NULL`)
count:
- **series** = distinct `series_id` of fully watched episodes;
- **episodes** = fully watched;
- **hours** = Σ(fully ? `duration_ms` : min(`playhead_s`·1000, `duration_ms`)) / 3.6e6;
- **recent** = the 6 newest watch events.

The definitions are in `specs/001-crunchyroll-history-import/data-model.md`.
