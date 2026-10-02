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

Everything personal hangs off `users` with `ON DELETE CASCADE`, so `user-delete` removes all of it.

## Identity

### `users`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `username` | text UNIQUE | `^[a-z0-9][a-z0-9_-]{2,29}$`, lowercase; used in `/u/{username}` |
| `display_name` | text | |
| `sync_key_hash` | text UNIQUE | lowercase hex SHA-256 of `frk_…`; the key itself is never stored |
| `sync_key_created_at` | timestamptz | |
| `cr_account_id`, `cr_profile_id` | text NULL | Crunchyroll history owner, bound on the first accepted submission |
| `created_at` | timestamptz | |

`ux_users_history_owner` on `(cr_account_id, coalesce(cr_profile_id, ''))`: one history owner
belongs to at most one user.

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
| `cr_series` | `id` = entry `parent_id` | title only from entries where `series_id = parent_id` (franchise movies don't rename the series) |
| `cr_seasons` | `id` = `season_id` | dub variants are separate seasons |
| `cr_episodes` | `id` = entry `id` | `duration_ms IS NULL` means **unresolved**: a stub created from an entry without panel, filled in when details arrive |

Catalog rows keep `source_at` (page `capturedAt`, else the import's `received_at`) and are only
overwritten by data at least as recent.

## Dashboard figures

Computed per request, no projection tables. Only resolved episodes (`duration_ms IS NOT NULL`)
count:
- **series** = distinct `series_id` of fully watched episodes;
- **episodes** = fully watched;
- **hours** = Σ(fully ? `duration_ms` : min(`playhead_s`·1000, `duration_ms`)) / 3.6e6;
- **recent** = the 6 newest watch events.

The definitions are in `specs/001-crunchyroll-history-import/data-model.md`.
