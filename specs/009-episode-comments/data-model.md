# Data Model: Episode Page and Comments

## New table: `comments`

Personal content written by users; not derived data. It hangs off `users` (local authors) and the
shared catalog (`cr_episodes`).

```sql
CREATE TABLE IF NOT EXISTS comments (
    id             text PRIMARY KEY CHECK (id ~ '^[A-Za-z0-9_-]{1,64}$'),
    cr_episode_id  text NOT NULL REFERENCES cr_episodes(id),
    author_user_id uuid NULL REFERENCES users(id) ON DELETE CASCADE,
    remote_actor   text NULL,
    content        text NOT NULL,
    format         text NOT NULL DEFAULT 'text' CHECK (format IN ('text', 'html')),
    summary        text NULL,
    sensitive      boolean NOT NULL DEFAULT false,
    in_reply_to    text NULL,
    audience       text NOT NULL DEFAULT 'public' CHECK (audience IN ('public', 'unlisted', 'followers', 'direct')),
    object_id      text NULL UNIQUE,
    created_at     timestamptz NOT NULL DEFAULT now(),
    edited_at      timestamptz NULL,
    deleted_at     timestamptz NULL,
    hidden_at      timestamptz NULL,
    hidden_by      uuid NULL REFERENCES users(id) ON DELETE SET NULL,
    CHECK ((author_user_id IS NULL) <> (remote_actor IS NULL)),
    -- Megáfono publishes replies only as public or unlisted; remote replies may arrive with any audience
    CHECK (author_user_id IS NULL OR audience IN ('public', 'unlisted'))
);

CREATE INDEX IF NOT EXISTS ix_comments_episode ON comments (cr_episode_id, created_at);
```

| Column | In this feature | Later (integration) |
|---|---|---|
| `id` | 22-char base64url, minted with the form (research R3) | `Idempotency-Key: comment:{id}`, `#comment-{id}` in the object's `url` |
| `cr_episode_id` | the episode the comment was written on; never changes | same |
| `author_user_id` / `remote_actor` | always the local user / always NULL | remote replies set `remote_actor` (actor id URI) |
| `content`, `format` | plain text, `text` | sanitized HTML from `reply.received`, `html` |
| `summary`, `sensitive` | the spoiler warning (R7) and its flag; NULL/false when not a spoiler | `summary`, `sensitive` both ways |
| `in_reply_to` | NULL (threads are flat) | `inReplyTo`: a comment id or a remote object id |
| `audience` | `public`; local comments are limited to `public`/`unlisted` by a CHECK, and an edit never changes it | remote: `public`/`unlisted` shown; `followers`/`direct` stored, not shown. Megáfono refuses other audiences, and audience changes, on publish and edit (its 002 `internal-api.md`) |
| `object_id` | NULL | Megáfono's object id once published or received |
| `edited_at` | set by an author edit that changes something | `reply.updated` |
| `deleted_at` | tombstone: `content = ''`, `summary = NULL` | `object.deleted`, `DELETE /internal/objects/{id}` |
| `hidden_at`, `hidden_by` | operator hide; cleared on unhide | same; local only |

## Rules

- **Valid content** (FR-007, R9): after trimming, folding CRLF to LF and removing control characters
  other than LF and tab, 1 to 2,000 Unicode scalar values.
- **Spoiler** (R7): `sensitive = true` ⇔ `summary IS NOT NULL` for local comments; the summary is
  computed at write in the author's language.
- **Shown in a thread**: `deleted_at IS NULL AND audience IN ('public', 'unlisted')`, ordered by
  `created_at, id`. Hidden comments are shown as a placeholder (R12).
- **Group** (R2): the comments whose `cr_episode_id` is in the page episode's group, computed per
  request. No group column is stored.
- **State transitions**:

```text
           edit (author)           hide (operator)
  visible ───────────────▶ visible ───────────────▶ hidden ──unhide──▶ visible
     │                                                │
     └──── delete (author) ──▶ deleted (tombstone) ◀──┘ delete (author)
```

  A deleted comment can't be edited, hidden or unhidden; those requests get the 404 page.

## Not stored

- **Episode page**: catalog rows (`cr_episodes`, `cr_seasons`, `cr_series`, `cr_season_matches`,
  `mal_anime`, tags) read per request.
- **Episode group**: computed per request.

## Lifecycle

- **User deletion**: the author's comments go (`CASCADE`); hides they made stay with `hidden_by` NULL.
- **`user-rebuild` / re-sent history / `mal-rebuild`**: never touch `comments`. A changed dub link
  only regroups threads.
- **Constitution I**: comments aren't derived from raw imports, so there's nothing to rebuild. They
  are user content, removed only by their author (tombstone) or account deletion.

## Security events

`security_events.event` gains `comment_hidden` and `comment_unhidden` (research R11). The column has
no CHECK, so no schema change. `SCHEMA.md` lists them.
