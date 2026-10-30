# Feature Specification: Watch updates on the fediverse

**Feature Branch**: `028-watch-updates`

**Created**: 2026-10-29

**Status**: Draft

**Input**: the follow-up task "Post what a user is watching to their fediverse followers": today a user's fediverse
account (`@user@domain`, feature 015) only publishes their episode comments, as replies in the series' episode
threads, and Mastodon hides replies from home timelines unless the follower also follows the series. Following a
person shows almost nothing. Following someone should show what they're watching.

## Clarifications

### Session 2026-10-29 (decided by the main session)

- Q: What is posted? → A: A **watch update**: one Note from the user's Person actor, at most one per user and UTC
  day, listing what they finished recently, e.g. "Watched 3 episodes of Frieren (5, 6, 7) and 1 episode of The
  Apothecary Diaries (12)." Series titles link to the public series page `/anime/{seriesId}`, episode numbers to the
  public episode page `/anime/{seriesId}/episode/{episodeId}`. Never a post per episode: a sync of 300 episodes gives
  at most one post that day.
- Q: Who posts? → A: Only a user who (all of these, checked when the post is queued **and again when the worker sends
  it**):
  1. turned watch updates on (`/account#watch-updates`, **off by default**, a row in `watch_update_settings`);
  2. has an **effective visibility of Public** (their choice, else `Frunchy:Profiles:DefaultVisibility`, the rule of
     `Profiles.effective`);
  3. has a username that can federate (`Fediverse.isValidHandle`, as for comments);
  4. and federation is on (with it off nothing is queued, the route is absent and the section isn't shown).
- Q: What counts as "watched"? → A: An episode of known length (`duration_ms IS NOT NULL`) with `fully_watched` in
  `watch_events`, deduplicated the DNA way within a series (a dub and its original count once: key
  `coalesce(shared_from, season_id) || '#' || episode_number`, else the episode id). Partial watches and unresolved
  episodes are never posted.
- Q: Which window? → A: Only recent watching. When an import completes, the window is
  `(covered_from, covered_until]` on `watched_at`, where `covered_until` is the import transaction's `now()` and
  `covered_from` is the **latest** of: `now() - 48 hours`, the moment the user turned updates on (`enabled_at`), and the
  `covered_until` of the user's previous watch update (any state). So importing an old history posts nothing, nothing
  watched before opting in is ever posted, and an episode is never posted twice (unless rewatched, which moves its
  `watched_at`). A window with no qualifying episode makes no row and queues nothing.
- Q: One a day? → A: `watch_updates` has primary key `(user_id, day)` (`day` = the UTC date of `now()`); the row is
  inserted `ON CONFLICT DO NOTHING` and the outbox item is queued only when the row was inserted. A later import the
  same day posts nothing; its episodes are in the next day's window (they are after the previous `covered_until`),
  as long as that import comes within 48 hours of them. The Idempotency-Key is `watch:{userId}:{yyyy-MM-dd}`.
- Q: When is it queued? → A: In the import's own transaction, right after the import is marked completed
  (`Workers/ImportProcessor.fs`), through `Infrastructure/WatchUpdates.fs`. `user-rebuild` reprocesses imports through
  the same path; the window rule makes it post nothing already posted.
- Q: How is it sent? → A: The existing outbox: new kind `post_watch_update`, subject = the user id (so it runs in
  order with the user's `refresh_profile`, `delete_actor` and the removal below). The worker reads the user's pending
  `watch_updates` rows (oldest first), re-checks the four conditions (else the row becomes `skipped`), builds the Note
  from `watch_events` in the row's window **at send time** (no payload in the outbox; none left → `skipped`) and calls
  `POST /internal/actors/{userId}/objects` with `audience: "unlisted"` (Megáfono's advice for an automatic digest: it
  reaches every follower's home timeline and stays out of public timelines), no `inReplyTo`, no summary,
  `sensitive: false`, and `url` = `https://{domain}/u/{username}#watched-{yyyy-MM-dd}` (absolute; a distinct link per
  day). The answer's `id`/`objectUrl` are stored on the row, which becomes `published`. Megáfono confirmed
  (megafono_orchestrator, 2026-10-29) that a top-level Note from a Person needs nothing new: delivered to followers,
  the app's HTML published as given, the key opaque (up to 200 characters).
- Q: The text and its language? → A: English, like the episode threads (a post on the fediverse, not a page in the
  viewer's language); the words live in `Views/Texts.fs` (with Spanish values, unused by the post) and the post uses
  `Texts.en`. At most 5 series (most episodes first, then title), then "and N more series"; at most 6 episode numbers
  per series, ascending, then "…"; an episode without a number is counted but not listed.
- Q: Spoilers? → A: Titles and episode numbers only, never an episode title or a thumbnail: a number and a series
  name spoil nothing, so the post has no content warning (`summary` none, `sensitive: false`). Comments keep their own
  spoiler rule (`CommentRules`); a watch update carries no user text at all. Every title is HTML-escaped.
- Q: Turning it off, or the profile leaving Public? → A: Both stop new posts **and delete the earlier ones**: privacy
  first, the posts described the user's watching and the user has now said that shouldn't be public. In the change's
  transaction: the user's `pending` rows become `skipped`, and when the user has any `pending` or `published` row, a
  `delete_watch_updates` item (subject = user id) is queued; the worker calls `DELETE /internal/objects/{objectId}` for
  each row with an object that isn't `deleted` (2xx, 404, 410 → `deleted`; transient → retry the item; other 4xx →
  refused). Copies other servers already fetched may stay; the account page says so. Turning it back on (or the
  profile becoming Public again) posts new updates only; deleted ones don't come back. The removal is triggered by
  `POST /account/watch-updates` (off) and `POST /account/visibility` (effective level was Public and isn't now). A change
  of `Frunchy:Profiles:DefaultVisibility` deletes nothing: the worker's re-check stops new posts.
- Q: Account deletion? → A: Unchanged: `delete_actor` removes the actor and its objects on Megáfono's side, and the
  rows cascade with the user.
- Q: Series threads linking these? → A: Out of scope.

## Requirements *(mandatory)*

- **FR-001**: `watch_update_settings (user_id uuid PRIMARY KEY REFERENCES users ON DELETE CASCADE, enabled_at
  timestamptz NOT NULL DEFAULT now())`: a row means on. `watch_updates (user_id uuid REFERENCES users ON DELETE
  CASCADE, day date, covered_from timestamptz, covered_until timestamptz, state text CHECK (pending | published |
  skipped | deleted) DEFAULT 'pending', object_id text NULL, object_url text NULL, created_at, PRIMARY KEY (user_id,
  day))`. `fediverse_outbox.kind` accepts `post_watch_update` and `delete_watch_updates`: in the `CREATE TABLE` and as a
  guarded idempotent `upgrades` block (the check naming `kind` and lacking `post_watch_update` is dropped by its real
  name and the new check added), proved by `SchemaUpgradeTests`. SCHEMA.md documents all three.
- **FR-002**: `POST /account/watch-updates` (federation on only; absent otherwise): `requireSignedIn`,
  `readValidatedForm`, `enabled` exactly `on` or `off` (else `?msg=watch-updates-invalid#watch-updates`, nothing
  changes), `account-write` limiter. One transaction: on = insert the row if missing (keeps the first `enabled_at`); off
  = delete it and the removal of FR-005. A security event `watch_updates_enabled` / `watch_updates_disabled` only when
  the state really changed. Redirect `?msg=watch-updates-on|watch-updates-off#watch-updates`.
- **FR-003**: `/account` shows the section `section[data-account-section="watch-updates"]` (the `accountSection`
  helper, id `watch-updates`) only when federation is on and the username can federate: what it does, the current
  state `data-watch-updates-state="on|off"`, one form with the button to turn it on or off, the note that
  turning it off or leaving Public deletes earlier updates (copies elsewhere may stay), and, when the effective
  visibility isn't Public, `data-watch-updates-not-public` saying nothing is posted until the profile is public. The
  three messages render as the page's other notices do.
- **FR-004**: After an import completes, in its transaction: when all FR "who posts" conditions hold and the window
  has a qualifying episode, insert the `watch_updates` row (`ON CONFLICT DO NOTHING`) and, if inserted,
  `Fediverse.enqueue … "post_watch_update" (string userId)`. Federation off: nothing (no query).
- **FR-005**: The removal (`WatchUpdates.queueRemoval`) as in the clarifications, called by FR-002 (off) and by
  `/account/visibility` when the effective level goes from Public to anything else and federation is on.
- **FR-006**: The worker handles `post_watch_update` and `delete_watch_updates` as in the clarifications. Storing a
  published object never loses it: `object_id`/`object_url` are stored whatever the row's state, and the state becomes
  `published` only from `pending` (a removal that raced the send has queued a delete, which runs after it).
  **The request is frozen at the first attempt**: Megáfono answers 409 `conflict` when a key is reused with a
  different body (megafono_orchestrator, 2026-10-29), and the window's episodes or the username may change between a
  timed-out attempt and its retry. So the worker builds the Note's `content` and `url` once, stores them on the row
  (`watch_updates.content`, `watch_updates.url`, both NULL until then) before calling Megáfono, and every retry sends
  the stored values. (The re-check of the conditions still runs before every attempt.)
- **FR-007**: The Note's HTML is built by a pure public function (titles and numbers escaped, links absolute under
  `https://{domain}`), unit-tested without Postgres.
- **FR-008**: All copy in `Views/Texts.fs` (en and es), including the two event labels on the account's event list.
  No new design token, no change to `/api/*`, the import contract or the extension.
- **FR-009**: SQL literal with parameters (the guardrail test). Docs: `docs/fediverse.md` (a "Watch updates" section),
  `SCHEMA.md`, the CLAUDE.md routes/federation line, `changelog.d/028-watch-updates.md`.

## Success Criteria *(mandatory)*

- **SC-001**: A public user who turned watch updates on and syncs today's watching produces exactly one unlisted Note
  that day on their account, listing series and episode numbers with links, and nothing else.
- **SC-002**: Importing a whole old history, being Members or Private, not opting in, or federation off produces no
  post.
- **SC-003**: Turning updates off or leaving Public deletes the published updates on Megáfono and stops new ones,
  even when a post was already queued.
