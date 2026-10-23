# Database Schema

The source of truth is the `initializeSchema` script in `Infrastructure/Database.fs`. It runs on
every app start and before every operator CLI command, and it's idempotent (`CREATE … IF NOT
EXISTS`, no migration framework). Keep this document in sync with it. The design rationale is in
`specs/001-crunchyroll-history-import/data-model.md` and `research.md`.

> **Clean bootstrap** (feature 014): the script describes each table's final shape in one `CREATE
> TABLE`, for an empty database. There is no legacy check and no `ALTER` patch: a database built by an
> older version is reset (drop it and let the app create it again). `SchemaBootstrapTests` proves the
> script builds the same columns, constraints and indexes the old patch-style script did.
>
> **Guarded upgrades** (feature 021): a fresh database gets no `ALTER`, but a database that already exists in
> production (`profile_settings`, from feature 017) must be brought up to date without a reset. Such a change is a
> guarded, idempotent block in the script's `upgrades` section: it checks the live catalog first (here: a CHECK on
> `visibility` whose definition lacks `members`), changes the table only when that is true, and so does nothing on
> every later run. The whole script runs in one transaction under a transaction-scoped advisory lock, so two copies
> starting together take turns and the second finds nothing left to do. New tables and columns are still described
> only in their `CREATE TABLE`; an upgrade block is added only for a table that is already deployed, and is proved by
> a test that builds the previous shape (`SchemaUpgradeTests`).

## Data classes

| Class | Tables | Rule |
|---|---|---|
| **Personal, raw** | `cr_imports`, `cr_import_pages` | stored exactly as received and never updated; deleted only when their user is deleted (constitution I) |
| **Personal, derived** | `watch_events`, `cr_import_entry_issues`, import counts | rebuildable from the raw pages (`user-rebuild`) |
| **Identity** | `users` | the sync key is stored only as a SHA-256 hash |
| **Personal, settings** (feature 017) | `profile_settings` | the owner's profile visibility and share link (only its hash); cascades on user delete |
| **Shared catalog** | `cr_series`, `cr_seasons`, `cr_episodes` | public Crunchyroll metadata, shared by all users; never deleted |
| **Instance** (feature 016) | `data_protection_keys` | the instance's own data-protection keys, not personal: no user reference, untouched by `user-delete`, part of the backup |
| **Instance** (feature 020) | `instance_settings` | the root's site-wide switches (today only `signup_mode`); `updated_by` is `ON DELETE SET NULL`, so deleting that user never deletes the setting |
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
| `user_identities` | `(provider, provider_user_id)` | a sign-in identity (`google`, `umbral`) belongs to exactly one user; same shape as ASP.NET Core Identity's user logins. Personal, cascades on user delete |
| `invites` | `id`, unique `code_hash` | one-time sign-up codes (SHA-256 only), `expires_at` = created + 7 days, `revoked_at`, `used_at`, `used_by_user_id` (`ON DELETE SET NULL`, so an invite stays "used" after its account is deleted), `is_setup` (feature 023: `boolean NOT NULL DEFAULT false`, true for the setup invite the web app prints in the console while there are no accounts; the account that uses it becomes an operator. Added to existing databases by a guarded `ADD COLUMN IF NOT EXISTS`, so old rows read false). Not personal data |
| `security_events` | identity `id` | per-user account events (`signed_in`, `claimed`, `claim_refused`, `identity_linked`/`unlinked`, `sync_key_generated`, `binding_reset`, `reconfirmed`, `display_name_changed`, `account_created`, `operator_added`/`operator_removed`, `comment_hidden`/`comment_unhidden`, `fediverse_block_added`/`fediverse_block_removed`, `profile_visibility_changed` (older rows), `profile_made_public`/`profile_made_members`/`profile_made_private`, `share_link_created`/`share_link_replaced`/`share_link_revoked`) with `actor` user/operator. No IP or device data. Personal, cascades on user delete |
| `operators` | `user_id` | users the root operator (`Frunchy:RootOperator`) made operators (feature 008); the root itself is configuration only and never a row. `added_by` is the granting root (`ON DELETE SET NULL`, NULL when added from the terminal or when that account was deleted), `added_at`. Personal, cascades on user delete |
| `comments` | `id` | episode comments (feature 009; the id is a 22-char base64url minted with the form), written on `cr_episodes(id)`. `author_user_id` is the local author (`ON DELETE CASCADE`, so account deletion removes the comments); `remote_actor` is NULL until the fediverse integration, and exactly one of the two is set. `content` + `format` (`text`; `html` later), `summary` + `sensitive` (the spoiler warning; NULL/false when not a spoiler), `audience` (local comments are limited to `public`/`unlisted` by a CHECK), `created_at`, `edited_at`. A deleted comment is a tombstone: `deleted_at` set, `content = ''`, `summary` NULL. `hidden_at`/`hidden_by` record an operator hide (`hidden_by` is `ON DELETE SET NULL`). `in_reply_to` and `object_id` stay empty until the integration. Index `(cr_episode_id, created_at)`. Personal, cascades on user delete |
| `profile_settings` | `user_id` | a user's profile visibility and share link (feature 017). One row per user who chose a visibility or made a link; no row means the instance default (`Frunchy:Profiles:DefaultVisibility`, `members` unless set) applies and there is no link. `visibility` (`public`/`members`/`private`, CHECK `profile_settings_visibility_check`; feature 021 added `members`, applied to existing databases by a guarded upgrade; NULL follows the default), `share_token_hash` (UNIQUE, lowercase-hex SHA-256 of the `frs_` token, never the token), `share_link_id` (a new uuid for every link; the browser's grant cookie names it, so replacing or turning off a link ends every grant), `share_link_created_at` (the three link columns are all set or all NULL), `updated_at`. Personal, cascades on user delete |

## Fediverse (feature 015)

The design is in `specs/015-fediverse/data-model.md`. Frunchy integrates Megáfono (the ActivityPub
sidecar); none of these tables is derived from imports, so there is nothing to rebuild.

| Table | Key | Notes |
|---|---|---|
| `series_handles` | `series_id` | a series' fediverse handle (`^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$`, UNIQUE), made once from the Crunchyroll slug and never changed automatically. Unique against usernames too, in code (sign-up refuses a username that is a series handle). Not personal |
| `episode_threads` | `group_key` | one thread per episode group (the original season id `#` the episode number, or the episode id). `cr_episode_id` is the episode the thread was opened from (its page URL); `object_id`/`object_url` are NULL until Megáfono published it. Not personal |
| `remote_actors` | `id` (actor URI) | remote authors of remote comments: `handle` (`@user@host`), `name`, `profile_url`, `icon_url`, `updated_at`. Not tied to a Frunchy user, so they stay when a local user is deleted |
| `megafono_events` | `id` | the webhook event log: dedup and audit (`type`, `received_at`, `outcome`: applied, ignored, unknown_thread, stale) |
| `fediverse_outbox` | identity `id` | the outbox. `kind` (`ensure_thread`, `publish_comment`, `update_comment`, `delete_comment`, `delete_actor`, `refresh_profile`), `subject` (group key, comment id or user id), `state` (`pending` or `done`), `attempts`, `next_attempt_at`, `claimed_at`, `last_error`, `done_at`. It holds no payloads (the worker reads the subject's current state) and no foreign keys, so a `delete_actor` item outlives its user. Partial index `(next_attempt_at, id) WHERE state = 'pending'` |

Remote comments reuse `comments` (`remote_actor`, `format = 'html'`, `object_id`, `in_reply_to`,
`audience`); a remote comment's id is `r-` plus 40 characters of the base64url SHA-256 of its object id.

`users.sync_key_hash` and `users.sync_key_created_at` are **nullable**: a user who signed up on the
website has no key until "Connect extension". Sessions aren't stored (encrypted cookies), and
provider tokens, emails and avatars are never stored.

## Instance (features 016 and 020)

| Table | Key | Notes |
|---|---|---|
| `data_protection_keys` | identity `id` | the ASP.NET Core data-protection keys that protect session cookies, sign-in round trips and form tokens, so a restart, a release or a second copy keeps everyone signed in. `friendly_name` (UNIQUE, the framework's `key-<guid>`; storing the same name again is ignored), `xml` (the key's XML; its secret is an `encryptedSecret`, AES-256-GCM, when `Frunchy:DataProtection:EncryptionKey` is set), `created_at`. Rows are only inserted: expired keys stay so older payloads can still be read. Not personal; losing the encryption key makes them unreadable and signs everyone out |
| `instance_settings` | `key` | the root's site-wide switches, one row per key. Today only `signup_mode` (`invite` or `open`; no row, or any other value, means `invite`). `value`, `updated_at`, `updated_by` (the user who last changed it, `ON DELETE SET NULL`). Read per request, so every copy of Frunchy sees a change at once. Not personal |

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
order doesn't matter. Indexes `(user_id, watched_at DESC)` and, for the charts of feature 012 (a window of recent watches across all users), `(watched_at)`.

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

## Year in review (feature 005)

No table. `GET /u/{username}/year/{year}` reads the same tables as the DNA page at request time,
filtered to one UTC calendar year of `watch_events.watched_at` (the latest watch of each episode).

## Anime page (feature 006)

No table. `GET /u/{username}/anime/{seriesId}` reads `watch_events`, `cr_episodes`, `cr_seasons`,
`cr_series` (LEFT JOIN, for the title and slug), `cr_season_matches`, `mal_anime` and the tags at
request time.

## Dashboard figures

Computed per request, no projection tables. Only resolved episodes (`duration_ms IS NOT NULL`)
count:
- **series** = distinct `series_id` of fully watched episodes;
- **episodes** = fully watched;
- **hours** = Σ(fully ? `duration_ms` : min(`playhead_s`·1000, `duration_ms`)) / 3.6e6;
- **recent** = the 6 newest watch events.

The definitions are in `specs/001-crunchyroll-history-import/data-model.md`.
