# Feature Specification: Fediverse Integration

**Feature Branch**: `015-fediverse`

**Created**: 2026-10-17

**Status**: Draft

**Input**: "Step 3 of docs/fediverse.md, first half: Frunchy joins the fediverse through Megáfono (contracts 001–004).
- Users are Person accounts and series are Group accounts.
- Each episode gets a thread post from its series account.
- Comments go both ways: a Frunchy comment is a reply in the thread, and a Mastodon reply shows on the episode page.
- Edits and deletes flow both ways.
- Deleting an account deletes its fediverse actor.
- Users and operators can block remote accounts and servers.

Following remote accounts and a feed wait for Megáfono's feed feature."

## Clarifications

### Session 2026-10-17 (decided in the autonomous run; the owner can overturn any of them)

- Q: How do Frunchy and Megáfono talk without slowing pages or losing work? → A: Through a Postgres **outbox**. Every call Frunchy must make to Megáfono goes in as a row in the same transaction as the change that needs it, and a background worker sends it with retries (Megáfono's idempotency keys make retries safe). This covers threads, comment publish/edit/delete, actor deletion and profile refresh. Pages never wait for Megáfono, and nothing is lost while it's down. Block management is the exception: it is interactive, so its calls are synchronous and show their error.
- Q: When is an episode's thread created? → A: On the first Frunchy comment on that episode group (its dub group, as in feature 009). The series account then posts "{series}: {episode label} {title}", linked to the episode page. Episode pages nobody comments on create nothing, so the catalog doesn't spam followers.
- Q: Are users' and series' accounts locked? → A: No (`locked: false`): follows are accepted at once. Follow approval, follower lists and following remote accounts are a later feature.
- Q: Series handles? → A:
  - **How it's made**: derived once from the Crunchyroll slug, lowercased and cut to 30 characters at a word boundary, with no dash at either end. Example: `frieren-beyond-journeys-end` becomes `frieren-beyond-journeys-end`; longer slugs are cut. A clash with a username or another series adds `-2`, `-3`, and so on.
  - **Stored**: never changed automatically.
  - **Collisions**: sign-up refuses a username that is already a series handle.
  - **Correction**: an operator can set a series handle with a CLI command until its series account has federated.
- Q: How is remote content shown? → A: As text. Megáfono already sanitizes replies to an allowlist, but Frunchy turns the HTML into plain text before showing it (paragraphs and line breaks kept; links shown as their text, not clickable). Frunchy then needs no HTML sanitizer, and no remote markup ever reaches a page.
- Q: Remote authors? → A: A `remote_actors` table keeps each remote account's id, handle, name, profile URL and icon URL, updated from every event that names it. A remote comment shows the handle with its server (`@bob@mastodon.social`), linking to their profile.
- Q: Following events (`follow.*`)? → A: Acknowledged and recorded in the event log only. Counts and lists come with following.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Accounts the fediverse can find (Priority: P1)

With federation on, every user is `@username@{domain}` and every series is `@{series-handle}@{domain}`. Mastodon finds them by handle or by pasting their Frunchy page, and follows are accepted at once.
- **Profiles**: a user's profile is their dashboard; a series' profile is its series page.
- **On the pages**: dashboards and series pages show the handle with a copy button, and their HTML points Mastodon to the actor.

**Why this priority**: nothing else federates without accounts.

**Independent Test**: with a fake Megáfono, call Frunchy's actor lookup by handle and by id for a user, a series, an unknown handle and a bad token, and check every field. The dashboard and series page carry the handle, the alternate link and the `Link` header.

**Acceptance Scenarios**:

1. **Given** federation on and a valid bearer token, **When** Megáfono asks `/megafono/actors?handle={username}`, **Then** Frunchy answers 200 with:
   - `id` = the user's id, `handle` = the username, `name` = the display name;
   - `profileUrl` = `https://{domain}/u/{username}`;
   - `type: "Person"` and `locked: false`.
2. **Given** a series with a handle, **When** asked by handle or by `series-{seriesId}`, **Then** Frunchy answers with:
   - `type: "Group"` and `locked: false`;
   - the series title as `name`;
   - `profileUrl` = `https://{domain}/anime/{seriesId}`.
3. **Given** an unknown handle or id, a username that doesn't fit the handle rule, or a deleted user, **When** asked, **Then** 404.
4. **Given** a missing or wrong token, **When** any `/megafono/*` route is called, **Then** 401, compared in constant time.
5. **Given** federation off, **When** any `/megafono/*` route is called, **Then** 404, and no page shows a handle.
6. **Given** a dashboard or series page with federation on, **When** it renders, **Then** it shows `@handle@domain` with a copy button, a `<link rel="alternate" type="application/activity+json">` to the actor, and the matching `Link` header.

---

### User Story 2 - A Frunchy comment reaches the fediverse (Priority: P1)

A signed-in user comments on an episode page:
1. Frunchy stores the comment at once, as today.
2. If the episode has no thread yet, the series account posts one.
3. The comment is published as the user's reply to it, with its spoiler warning.

The episode page then points Mastodon to the thread.

**Why this priority**: half of "comments both ways".

**Independent Test**: with the fake Megáfono, write a comment, run the outbox worker, and check:
- the thread publish (series actor, idempotency key `thread:{seriesId}:{episodeKey}`, content, URL);
- the reply publish (user actor, `comment:{id}`, `inReplyTo` the thread's URL, summary and sensitive, URL `#comment-{id}`);
- the stored object ids;
- the episode page's alternate link.

Retries after a 503 and a repeated worker run send nothing twice.

**Acceptance Scenarios**:

1. **Given** federation on and an episode group without a thread, **When** a user comments, **Then** the comment shows immediately, and the outbox gets "ensure thread" then "publish comment" for it.
2. **Given** the worker, **When** it runs, **Then**:
   - it publishes the thread from the series actor (`thread:{seriesId}:{episodeKey}`, public, a Note with the series title, episode label and title, and the episode page URL);
   - it stores the thread's object id and URL;
   - it publishes the comment from the user's actor (`comment:{id}`, public, `inReplyTo` = the thread's URL, content as escaped HTML paragraphs, `summary`/`sensitive` when a spoiler, `url` = the episode page `#comment-{id}`);
   - it stores the comment's object id.
3. **Given** a thread that exists, **When** another comment is written, **Then** only the comment is published.
4. **Given** Megáfono answering 503 or not reachable, **When** the worker runs, **Then** the item stays queued with its error and a later attempt time (backoff up to 1 hour). The comment stays visible on Frunchy throughout.
5. **Given** an episode with a thread, **When** its page renders, **Then** it has the alternate link and `Link` header to the thread's URL.

---

### User Story 3 - A fediverse reply shows on Frunchy (Priority: P1)

A Mastodon user replies to an episode thread, or to a Frunchy comment in it. Megáfono sends `reply.received` to Frunchy, which verifies the signature, records the event once, stores the reply as a remote comment on that episode, and shows it in the thread with its author's handle and server. Content warnings become spoilers.

**Why this priority**: the other half of "comments both ways".

**Independent Test**: post signed webhook bodies to `/megafono/webhooks` and check:
- a reply shows on the episode page with its text, author, time and spoiler;
- a repeat, a bad signature and a stale timestamp are refused or ignored;
- a reply to an unknown thread is acknowledged and dropped.

**Acceptance Scenarios**:

1. **Given** a correctly signed `reply.received` for a known thread, **When** it arrives, **Then** Frunchy stores a remote comment on the thread's episode:
   - `remote_actor`, `object_id`, `in_reply_to`, `audience`, `summary`/`sensitive` and `created_at` = `published`;
   - content as text;
   - its author recorded in `remote_actors`.

   Frunchy answers 204.
2. **Given** the same event id again, **When** it arrives, **Then** 204 and nothing changes.
3. **Given** a missing, wrong or more-than-5-minutes-old signature, **When** a webhook arrives, **Then** 401 and nothing is stored.
4. **Given** a reply with audience `followers` or `direct`, **When** it is stored, **Then** it isn't shown on the page (as in feature 009).
5. **Given** a reply whose thread Frunchy doesn't know, or an event type Frunchy doesn't handle, **When** it arrives, **Then** 204: it is recorded and ignored.
6. **Given** a remote comment, **When** the episode page renders, **Then** it shows:
   - the author's name and `@user@server` linking to their profile, opening in a new tab;
   - the time, the text, and the spoiler toggle when it carries a warning;
   - no edit or delete controls; operators can hide it.

---

### User Story 4 - Edits and deletes, both ways (Priority: P2)

- **Frunchy author edits or deletes**: the change reaches the fediverse.
- **Remote author edits or deletes**: the change reaches Frunchy.

**Why this priority**: people edit and delete; without this the two copies drift.

**Independent Test**: edit and delete a published comment and check the outbox's `PUT`/`DELETE` calls; send `reply.updated` and `object.deleted` and check the page. A stale `reply.updated` (older `updated`) changes nothing.

**Acceptance Scenarios**:

1. **Given** a published Frunchy comment, **When** its author edits it, **Then** the outbox sends `PUT /internal/objects/{objectId}` with the new content, summary and sensitive flag.
2. **Given** a published Frunchy comment, **When** its author deletes it, **Then** the outbox sends `DELETE /internal/objects/{objectId}`.
3. **Given** a comment edited or deleted before its publish was sent, **When** the worker runs, **Then**:
   - an edit publishes the latest text once;
   - a delete publishes nothing, or deletes the published object if the publish had already happened.
4. **Given** `reply.updated` with a newer `updated`, **When** it arrives, **Then** the remote comment's text, warning and `edited_at` change. An older one changes nothing.
5. **Given** `object.deleted` for a remote comment, **When** it arrives, **Then** the comment becomes a tombstone, as a local delete does.

---

### User Story 5 - Accounts follow their owners (Priority: P2)

- **Deleting an account**: deletes its fediverse actor.
- **Changing a display name**: refreshes the profile on the fediverse.

**Why this priority**: an account deleted on Frunchy must not live on in the fediverse.

**Independent Test**: delete an account and check the outbox's `DELETE /internal/actors/{id}`, treating 404 as done. Change a display name and check `PUT /internal/actors/{id}`.

**Acceptance Scenarios**:

1. **Given** federation on, **When** a user deletes their account (web or CLI), **Then** the outbox gets "delete actor" for their id in the same transaction. The worker sends `DELETE /internal/actors/{id}`, and 202 or 404 count as done.
2. **Given** federation on, **When** a user changes their display name, **Then** the outbox gets "refresh profile", and the worker sends `PUT /internal/actors/{id}`.

---

### User Story 6 - Blocks (Priority: P3)

- **A user** blocks remote accounts or whole servers from their account page, and lifts them.
- **An operator** blocks servers or accounts for the whole instance from a review page.

**Why this priority**: Megáfono's brief says series accounts shouldn't be announced widely until blocks exist. It isn't needed to try federation.

**Independent Test**: with the fake Megáfono:
- add, list and remove a user block and an instance block;
- each Megáfono error shows its message;
- a security event is recorded each time;
- non-operators get the plain 404 on the operator page.

**Acceptance Scenarios**:

1. **Given** a signed-in user with federation on, **When** they add a block (`@user@host`, an actor URL, or a domain), **Then** Frunchy calls `POST /internal/actors/{id}/blocks`, shows the result, lists their blocks (`GET`), and can lift one (`DELETE`).
2. **Given** Megáfono's `400`, `404`, `409 blocked` or `502`, **When** a block is added, **Then** the page says why in plain words, and nothing is recorded.
3. **Given** an operator, **When** they add or lift an instance block on `/review/fediverse`, **Then** Frunchy calls `/internal/blocks`. Non-operators get the plain 404 on every route and verb, as on the other review pages.
4. **Given** any block added or lifted, **When** it succeeds, **Then** a security event is recorded on the account that did it.

### Edge Cases

- **Federation off**: every new route answers 404, nothing goes to the outbox, and no handle shows. The site works as in 014.
- **Comment on a seasonless or numberless episode**: its group is the episode alone, so its thread is keyed by the episode id.
- **A user with a username ending in `-`** (pre-013): their actor lookup answers 404, since they can't federate. Their comments stay local, are never queued, and the page says nothing.
- **A reply to a Frunchy comment** (not the thread root): stored on the same episode, with `in_reply_to` pointing at the comment's object id. It's shown flat in the thread.
- **Megáfono gone for a long time**: the outbox keeps retrying with capped backoff and never drops an item. An operator sees the queue depth in `mal-status`'s style through a new `fediverse-status` command.
- **Out-of-order events**: `reply.updated` before `reply.received` creates the comment. `object.deleted` for an unknown reply records a tombstone, so a late `reply.received` doesn't revive it.
- **Series actor for a series with no slug**: its handle comes from the series id, lowercased (`series-gg5h5xqx4`, within 30 characters).

## Requirements *(mandatory)*

### Functional Requirements

**Accounts (Megáfono's app contract)**

- **FR-001**: With federation on, Frunchy MUST serve `GET /megafono/actors?handle={h}` and `GET /megafono/actors/{id}` on its own address. They require `Authorization: Bearer {Frunchy:Megafono:Token}`, compared in constant time; otherwise 401. They answer:
  - users (`id` = `users.id`, `type: Person`);
  - series (`id` = `series-{seriesId}`, `type: Group`);
  - `locked: false` for both.

  Unknown, deleted or handle-invalid accounts get 404. With federation off they get 404.
- **FR-002**: Every series MUST get a stored handle (rules in Clarifications), unique against every other series handle and every username. Sign-up MUST refuse a username equal to an existing series handle. An operator command MUST set a series handle until that series' actor has published anything.
- **FR-003**: Dashboards and series pages MUST, with federation on, show `@handle@{domain}` with a copy button and carry `<link rel="alternate" type="application/activity+json" href="https://{domain}/ap/actors/{id}">` and the matching `Link` header.

**Outbox**

- **FR-004**: Every call Frunchy makes to Megáfono for threads, comments and accounts MUST go through an outbox table, written in the transaction of the change that caused it, and sent by a background worker:
  - oldest first, per comment in order;
  - backoff from 1 minute, doubling, capped at 1 hour, never dropped;
  - claims with `FOR UPDATE SKIP LOCKED`;
  - idempotency keys `thread:{seriesId}:{episodeKey}` and `comment:{id}`;
  - each attempt's last error stored.
- **FR-005**: The worker MUST be disabled with federation off, and switchable like the other workers. Tests drive it directly.

**Threads and comments out**

- **FR-006**: The first Frunchy comment on an episode group MUST queue its thread. The series actor publishes it publicly:
  - **content**: "{series title}: {episode label} {episode title}", escaped;
  - **url**: the episode page;
  - **key**: `thread:{seriesId}:{episodeKey}`, where `episodeKey` is the group key (original season id `#` number, or the episode id).

  Its object id and URL are stored per group.
- **FR-007**: Each Frunchy comment by a user who can federate MUST be published as a reply from the user's actor:
  - `audience: public`;
  - `inReplyTo` = the thread's URL;
  - content as HTML paragraphs of the escaped text;
  - `summary`/`sensitive` from its spoiler warning;
  - `url` = `https://{domain}/anime/{seriesId}/episode/{episodeId}#comment-{id}`;
  - key `comment:{id}`.

  Its object id is stored in `comments.object_id`.
- **FR-008**: Edits and deletes of a published Frunchy comment MUST queue `PUT` and `DELETE /internal/objects/{objectId}`. An edit before publishing publishes the latest text. A delete before publishing publishes nothing.
- **FR-009**: An episode page whose group has a thread MUST carry the thread's URL as `<link rel="alternate" type="application/activity+json">` and `Link` header.

**Replies in**

- **FR-010**: Frunchy MUST serve `POST /megafono/webhooks` with the bearer check and the `Megafono-Signature` check (HMAC-SHA256 of `"{t}.{raw body}"` with the webhook secret, at most 5 minutes old, constant-time). It answers 401 on failure. It records each event id once and answers 204 for repeats, unknown types and events it ignores.
- **FR-011**: `reply.received` for a known thread MUST store a remote comment on the thread's episode with:
  - the actor id, object id, `in_reply_to` and audience;
  - `summary`/`sensitive`, `published` as `created_at`;
  - the content as HTML for the record (`format: html`).

  It MUST upsert the author into `remote_actors`.
- **FR-012**: `reply.updated` with a newer `updated` MUST update the text, warning and `edited_at`; an older one changes nothing. `object.deleted` MUST tombstone the remote comment. Both MUST work when they arrive before `reply.received`.
- **FR-013**: Remote comments MUST render as plain text: tags stripped, entities decoded, paragraphs and `<br>` as line breaks, nothing clickable inside. The author's name and `@user@server` link to their profile with `rel="noopener nofollow ugc"` in a new tab. Spoilers and the visibility rules are as for local comments; operators hide them as any comment.

**Accounts follow their owners**

- **FR-014**: Account deletion (web and CLI) MUST queue "delete actor" for the user id in its transaction; 202 and 404 are done. A display-name change MUST queue "refresh profile".

**Blocks**

- **FR-015**: The account page MUST, with federation on, let the user list, add (handle, actor URL or domain) and lift their blocks through Megáfono's user-block calls, synchronously, showing Megáfono's errors in plain words. It uses the antiforgery-checked forms, a rate limit, and a security event per change.
- **FR-016**: `/review/fediverse` MUST let operators do the same for instance blocks, under the review pages' rules (`requireOperator` outermost, `review-write` limiter, plain 404 for everyone else).

**Operations and general**

- **FR-017**: A CLI command `fediverse-status` MUST print the federation settings (never secrets), the outbox counts by state and the oldest pending item. `series-handle <seriesId> <handle>` MUST set a series handle (FR-002).
- **FR-018**: No secret MUST ever appear in a log, page or error. Every new text MUST be in English and Spanish. The new UI MUST use the Night Broadcast design system. The tests MUST NOT need a real Megáfono: a fake replaces the "megafono" HTTP client, as `FakeMal` does for MyAnimeList.

### Key Entities

- **Series handle**: series id → handle, set once.
- **Episode thread**: an episode group key → the series' thread object id and URL.
- **Outbox item**: kind, target ids, payload, state, attempts, next attempt time, last error.
- **Webhook event**: event id, type, received time (dedup log).
- **Remote actor**: actor id URI, handle, name, profile URL, icon URL, last update.
- **Comment** (existing): gains remote comments (`remote_actor`, `format: html`) and object ids.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Against the fake Megáfono, a Frunchy comment produces exactly one thread publish per episode group and one reply publish per comment, with the contract's fields, keys and URLs, and nothing is ever sent twice across retries and repeated worker runs.
- **SC-002**: A signed `reply.received` shows on the episode page on its next load. Each failure mode (missing, wrong or stale signature; bad token) stores nothing and answers 401.
- **SC-003**: With Megáfono down, comments keep working on Frunchy, and every queued item is sent once Megáfono is back.
- **SC-004**: Edits and deletes converge both ways: after any order of local edits and deletes, and remote `reply.updated` and `object.deleted`, both sides show the same text or the same absence.
- **SC-005**: With federation off, the full test suite passes as in 014 and no new route answers.
- **SC-006**: Manually under the AppHost: Megáfono's WebFinger answers for a real user and a series, and a comment produces the thread and reply in Megáfono's database. Remote peers are tried once Megáfono's dev-fediverse kit exists.

## Assumptions

- Megáfono 001–004 contracts as on its `main` (ba58955).
- Following remote accounts, follower lists, the feed, likes and mentions are later features.
- Remote media attachments are ignored; text only.
- The fediverse handle of a user is their username, so usernames ending in `-` can't federate. None exist locally.

## Verbatim Constraints

- Routes: `GET /megafono/actors?handle=`, `GET /megafono/actors/{id}`, `POST /megafono/webhooks`, `/review/fediverse`
- Actor ids: `users.id`; `series-{seriesId}`. Actor URL: `https://{domain}/ap/actors/{id}`
- Idempotency keys: `thread:{seriesId}:{episodeKey}`, `comment:{id}`
- Signature: `Megafono-Signature: t=<unix>,v1=<hex hmac-sha256 of "t.body">`, 5 minutes, constant-time
