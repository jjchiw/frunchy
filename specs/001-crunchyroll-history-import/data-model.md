# Data Model: Crunchyroll Watch-History Import with Real Dashboard

**Feature**: [spec.md](spec.md) · **Decisions**: [research.md](research.md) · **Date**: 2026-10-02

All tables are created by the idempotent bootstrap in `Infrastructure/Database.fs`
(`CREATE TABLE IF NOT EXISTS`). `SCHEMA.md` is updated in the same change. The legacy layout is
detected and rejected (research R14).

## Overview

```text
users ──1:n── cr_imports ──1:n── cr_import_pages            (raw, verbatim; Principle I)
  │               └──1:n── cr_import_entry_issues            (unresolved / unsupported / failed)
  └──1:n── watch_events ──n:1── cr_episodes ──n:1── cr_series
                                     └──n:1── cr_seasons ──n:1── cr_series
```

- **Personal data** is everything hanging off `users` (all `ON DELETE CASCADE`). Deleting a user
  removes all of it (FR-019).
- **Shared catalog**: `cr_series`, `cr_seasons` and `cr_episodes` are public Crunchyroll metadata.
  They're shared by all users and never deleted.
- **Derived data** (rebuildable from `cr_import_pages`): `watch_events`, `cr_import_entry_issues`,
  the import counts, and catalog contents.

## Tables

### `users`

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | |
| `username` | text NOT NULL UNIQUE | `^[a-z0-9][a-z0-9_-]{2,29}$`, stored lowercase (FR-001a) |
| `display_name` | text NOT NULL | 1–60 chars |
| `sync_key_hash` | text NOT NULL UNIQUE | hex SHA-256 of the key; the key itself is never stored (R4) |
| `sync_key_created_at` | timestamptz NOT NULL | |
| `cr_account_id` | text NULL | set on first accepted submission (FR-001e) |
| `cr_profile_id` | text NULL | only when the history owner includes a profile (R5, pending capture) |
| `created_at` | timestamptz NOT NULL default now() | |

Unique index `ux_users_history_owner` on `(cr_account_id, coalesce(cr_profile_id, ''))` WHERE
`cr_account_id IS NOT NULL`: one history owner, at most one user.

### `cr_imports` (history submission)

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | the submission reference returned to the client |
| `user_id` | uuid NOT NULL FK → users ON DELETE CASCADE | |
| `received_at` | timestamptz NOT NULL default now() | |
| `exported_at` | timestamptz NULL | from the envelope, informational |
| `status` | text NOT NULL | `pending` \| `processing` \| `completed` \| `failed` |
| `processing_started_at` | timestamptz NULL | used for stale-claim recovery (R6) |
| `completed_at` | timestamptz NULL | |
| `page_count` | int NOT NULL | 1–100 |
| `entries_received` | int NOT NULL | set at acceptance |
| `processed`, `unresolved`, `unsupported`, `failed` | int NOT NULL default 0 | set at completion; sum = `entries_received` (SC-003) |
| `error` | text NULL | only when `status = failed` (whole submission couldn't be processed) |

Indexes: `(status, received_at)` for the worker claim, and `(user_id, received_at DESC)` for last
sync and the in-progress check.

**State transitions**

```text
          accept (POST /api/imports)
                 │
                 ▼
            ┌─ pending ◄────────────── rebuild (operator) / stale-claim recovery (>10 min)
            │    │ worker claims (FOR UPDATE SKIP LOCKED)
            │    ▼
            │ processing ──── unexpected error ───► failed   (error set; counts untouched)
            │    │ all pages processed
            │    ▼
            └ completed  (counts set; entry-level problems are issues, not a failed status)
```

### `cr_import_pages` (raw, never modified)

| Column | Type | Rules |
|---|---|---|
| `import_id` | uuid FK → cr_imports ON DELETE CASCADE | |
| `page_index` | int | 0-based, in envelope order; PK (`import_id`, `page_index`) |
| `url` | text NOT NULL | Crunchyroll request URL exactly as sent |
| `captured_at` | timestamptz NULL | from the envelope |
| `body` | **json** NOT NULL | exact text of the page body (R2) |

Application code never runs UPDATE on this table. Only the user-deletion cascade deletes from it
(constitution I, exception).

### `cr_import_entry_issues`

| Column | Type | Rules |
|---|---|---|
| `import_id` | uuid FK → cr_imports ON DELETE CASCADE | |
| `page_index`, `entry_index` | int | PK (`import_id`, `page_index`, `entry_index`) |
| `cr_episode_id` | text NULL | entry `id` when present |
| `outcome` | text NOT NULL | `unresolved` \| `unsupported` \| `failed` |
| `reason` | text NOT NULL | human-readable (FR-009) |

Processed entries have no row. Their count is `cr_imports.processed`.

### `cr_series` (shared catalog)

| Column | Type | Rules |
|---|---|---|
| `id` | text PK | Crunchyroll series id = entry `parent_id` (R9) |
| `title` | text NULL | only from entries where `episode_metadata.series_id = parent_id` |
| `slug` | text NULL | `series_slug_title` |
| `source_at` | timestamptz NOT NULL | freshness guard (R7) |

### `cr_seasons` (shared catalog)

| Column | Type | Rules |
|---|---|---|
| `id` | text PK | `season_id` |
| `series_id` | text NOT NULL | parent series (`parent_id`) |
| `season_number` | int NULL | |
| `display_number` | text NULL | `season_display_number` |
| `title` | text NULL | |
| `audio_locale` | text NULL | dub variants are separate seasons |
| `source_at` | timestamptz NOT NULL | |

### `cr_episodes` (shared catalog)

| Column | Type | Rules |
|---|---|---|
| `id` | text PK | Crunchyroll episode id (entry `id` = `panel.id`) |
| `series_id` | text NOT NULL | entry `parent_id`, known even when unresolved |
| `season_id` | text NULL | |
| `series_title` | text NULL | `episode_metadata.series_title` (shown in the recent list; movies show their own title) |
| `title` | text NULL | `panel.title` |
| `episode_number` | int NULL | NULL → shown as "Special" / "Especial" |
| `episode_label` | text NULL | `episode_metadata.episode` (raw label) |
| `duration_ms` | int NULL | **NULL = unresolved** (R8) |
| `thumbnail_url` | text NULL | `images.thumbnail[0]`, the variant with width closest to 320 |
| `source_at` | timestamptz NULL | NULL while unresolved |

`resolved` ⇔ `duration_ms IS NOT NULL`. An unresolved row is created from `id` + `parent_id`, and
its details are filled in by any later entry that has a panel.

### `watch_events` (derived ledger, one per user and episode)

| Column | Type | Rules |
|---|---|---|
| `user_id` | uuid FK → users ON DELETE CASCADE | PK (`user_id`, `cr_episode_id`) (FR-007) |
| `cr_episode_id` | text FK → cr_episodes | |
| `watched_at` | timestamptz NOT NULL | entry `date_played` |
| `playhead_s` | int NOT NULL | entry `playhead` (seconds) |
| `fully_watched` | bool NOT NULL | |
| `import_id` | uuid NOT NULL | the import that last set this state (diagnostics) |
| `updated_at` | timestamptz NOT NULL | |

Index `(user_id, watched_at DESC)`. Upsert rule (R7): replace only if
`(watched_at, fully_watched, playhead_s)` of the incoming entry is greater than the stored one.

## Entry → outcome mapping (processing)

| Entry condition | Outcome | Writes |
|---|---|---|
| no `panel` | `unresolved` | `cr_episodes` stub (id, series_id), `watch_events` upsert, issue row |
| `panel.type` ≠ `episode` | `unsupported` | issue row only |
| panel present but `id`, `date_played` or `duration_ms` missing/invalid | `failed` | issue row with reason |
| otherwise | `processed` | `cr_series`/`cr_seasons`/`cr_episodes` upserts (freshness-guarded), `watch_events` upsert |

Every entry gets exactly one outcome: received = processed + unresolved + unsupported + failed
(SC-003).

## Dashboard figures (computed per request, R10)

For user *u*, over `watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id` with
`e.duration_ms IS NOT NULL`:

| Figure | Definition |
|---|---|
| series | `count(DISTINCT e.series_id)` where `w.fully_watched` |
| episodes | `count(*)` where `w.fully_watched` |
| hours | `sum(CASE WHEN w.fully_watched THEN e.duration_ms ELSE least(w.playhead_s*1000, e.duration_ms) END) / 3.6e6` |
| days | hours / 24 (both rounded to 1 decimal, half away from zero) |
| recent | top 6 by `w.watched_at DESC` (partial included, flagged) |
| last sync | `max(cr_imports.received_at)` for *u* |
| sync in progress | exists an import of *u* with status `pending` or `processing` |
| newest episode received | first row of recent |

Expected values: `PostHistory.json` → 29 / 96 / 39.3 h / 1.6 days.
`crunchyroll-history-2026-sample.json` → 67 / 298 / 118.2 h, most recent "Clevatess" ep. 13
(SC-004).

## Validation rules (at submission time, nothing stored on failure)

1. `Authorization: Bearer <key>` resolves to a user → else 401 `unauthorized`.
2. Body is JSON with `pages` as an array of 1–100 objects, each with a string `url` and an object
   `body` that has an array `data` → else 400 `invalid_submission`.
3. Every page URL path matches `/content/v2/{accountId}/watch-history`, with the same `accountId`
   on all pages (+ `profileId` if required, R5) → else 400 `history_owner_missing`.
4. Owner = user's binding, or the user is unbound and the owner is free → else 409
   `history_owner_mismatch` / `history_owner_taken`.
5. Body ≤ 30,000,000 bytes → else 413 `payload_too_large`.

On success, one transaction binds the user if needed, inserts `cr_imports` (`pending`,
`entries_received` = Σ `data.length`) and all `cr_import_pages`, then the endpoint returns 202.

## Domain types (`Domains/*.fs`)

- `Identity.fs`: `User`, `Username` (validated), `SyncKey` (create/hash), `HistoryOwner`.
- `Tracking.fs`: `ImportStatus` DU (persisted as lowercase text), `EntryOutcome` DU, `ImportCounts`,
  `WatchEvent`.
- `Catalog.fs`: `CrSeries`, `CrSeason`, `CrEpisode`.
- `Domains/Analytics.fs` and `Types.fs` are removed (R14).
- Parsing of Crunchyroll pages lives in `Infrastructure/CrunchyrollPage.fs`, a pure module:
  `JsonElement` → `ParsedEntry` with its outcome. It gets unit tests.
