# Data Model: Fediverse Integration

All tables are new, in `initializeSchema`, documented in `SCHEMA.md`.

```sql
-- A series' fediverse handle, set once (FR-002)
CREATE TABLE IF NOT EXISTS series_handles (
    series_id  text PRIMARY KEY,
    handle     text NOT NULL UNIQUE CHECK (handle ~ '^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$'),
    created_at timestamptz NOT NULL DEFAULT now()
);

-- One thread per episode group (FR-006); object ids come back from Megáfono
CREATE TABLE IF NOT EXISTS episode_threads (
    group_key     text PRIMARY KEY,            -- original season id '#' number, or the episode id
    series_id     text NOT NULL,
    cr_episode_id text NOT NULL REFERENCES cr_episodes(id),   -- the episode the thread was opened from (its page URL)
    object_id     text NULL UNIQUE,            -- NULL until published
    object_url    text NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- Remote authors (FR-011)
CREATE TABLE IF NOT EXISTS remote_actors (
    id          text PRIMARY KEY,              -- actor id URI
    handle      text NOT NULL,                 -- @user@host
    name        text NOT NULL,
    profile_url text NULL,
    icon_url    text NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- Webhook event log: dedup and audit (FR-010)
CREATE TABLE IF NOT EXISTS megafono_events (
    id          text PRIMARY KEY,
    type        text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    outcome     text NOT NULL                  -- applied | ignored | unknown_thread | stale
);

-- The outbox (FR-004)
CREATE TABLE IF NOT EXISTS fediverse_outbox (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind            text NOT NULL CHECK (kind IN ('ensure_thread', 'publish_comment', 'update_comment', 'delete_comment',
                                                   'delete_actor', 'refresh_profile')),
    subject         text NOT NULL,             -- group_key, comment id, or user id
    state           text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'done')),
    attempts        int NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    claimed_at      timestamptz NULL,
    last_error      text NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    done_at         timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_fediverse_outbox_pending ON fediverse_outbox (next_attempt_at, id) WHERE state = 'pending';
```

## Rules

- **The outbox holds no payloads.** Each item names its subject, and the worker reads the subject's
  current state when it runs. An edit or delete queued before the publish therefore needs no
  special case: publishing uses the latest text, and a deleted comment publishes nothing (FR-008).
- **`delete_actor`** keeps the user id after the user row is gone (no foreign key, on purpose).
- **Order**: the worker takes the oldest due item. For a given comment (`subject`), it never runs an
  item while an older pending item for the same subject exists. `ensure_thread` for a group
  precedes its comments' `publish_comment`: publish waits, by retrying, until the thread has an
  object id.
- **Remote comments** use the existing `comments` columns:
  - `remote_actor` = the actor id;
  - `format` = `html`;
  - `object_id` = the remote object id;
  - `in_reply_to`;
  - `audience`;
  - `created_at` = `published`;
  - `edited_at` = `updated`.

  `comments.id` for a remote comment is `r-` plus the first 40 characters of a SHA-256 of the
  object id, in base64url (fits the id rule).
- **Personal data**: remote actors and remote comments aren't tied to a Frunchy user, so they stay
  when a local user is deleted. A local user's comments cascade, as before.
- **Rebuilds**: none of these tables are derived from imports. Nothing to rebuild.
