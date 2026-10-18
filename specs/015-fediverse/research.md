# Research: Fediverse Integration

Sources (read-only): Megáfono `specs/001-federation-core/contracts/{app-contract,internal-api}.md`, `002-replies-threads`,
`003-actor-deletion` and `004-blocks` contracts, and `samples/SampleApp/Megafono.fs` (the reference integration).

## R1. Modules

| File | What |
|---|---|
| `Infrastructure/MegafonoClient.fs` | Named `HttpClient` "megafono" (base URL `Frunchy:Megafono:Url`, bearer token, 10 s timeout, no Polly retries: the outbox retries). Functions `publish`, `updateObject`, `deleteObject`, `deleteActor`, `refreshProfile`, `listBlocks`/`addBlock`/`removeBlock` (user), and `listInstanceBlocks`/`addInstanceBlock`/`removeInstanceBlock`. Each returns a typed result (status + parsed body or error code/message) |
| `Infrastructure/Fediverse.fs` | Pure rules: `seriesHandleOf slug seriesId taken`, `actorUrl domain id`, `htmlToText`, `commentHtml text`, `remoteCommentId objectId`, `verifySignature secret now header body`, `episodeGroupKey`. Data access shared by slices and the worker: `ensureSeriesHandle`, `enqueue` (in a given transaction) |
| `Handlers/Megafono/Actors.fs` | `GET /megafono/actors`, `GET /megafono/actors/{id}` |
| `Handlers/Megafono/Webhooks.fs` | `POST /megafono/webhooks` |
| `Handlers/Account/Blocks.fs` | the account page's block section (GET fragment and POST add/remove) |
| `Handlers/Review/FediverseBlocks.fs` | `/review/fediverse` (operators) |
| `Workers/FediverseOutbox.fs` | the outbox worker, `processNext` (public for tests, like `ImportProcessor.processNext`) |
| `Operations/FediverseStatus.fs`, `Operations/SeriesHandle.fs` | CLI |

## R2. Signature and token

- **Signature**: copy the sample's `verifySignature`: parse `t` and `v1`; `|now - t| ≤ 300`;
  HMAC-SHA256 of `"{t}." + raw body bytes`; lowercase hex; `CryptographicOperations.FixedTimeEquals`.
- **Raw body**: read the raw body before any parsing, and limit it to 1 MB.
- **Bearer check**: the same constant-time comparison on `Authorization`, for every `/megafono/*`
  route.

## R3. Actor lookup

- **Handle**:
  - **Username match**: if `Username.tryCreate` accepts it and it matches the handle rule (no
    trailing `-`), look up `users`;
  - **Otherwise**: look up `series_handles`.
- **Id**:
  - a uuid → `users`;
  - `series-{id}` → a series with a handle (create its handle lazily if missing, FR-002).
- **Answer**: Person is `{id, handle, name, profileUrl, type, locked: false}`. Group is the same
  with `type: "Group"`, `name` = series title, and `iconUrl` = the series poster when it's an
  absolute https URL (MyAnimeList image), else omitted.

## R4. Series handles

- **Making one**:
  1. Take the slug (else `series-{id}`), lowercased.
  2. Replace runs of characters outside `[a-z0-9_-]` with `-`, then trim dashes.
  3. If longer than 30 characters, cut at the last `-` at or before 30 and trim again.
  4. Empty → `series-{id}`, truncated.
- **Uniqueness**: against `series_handles.handle` and `users.username`, in one transaction under
  `pg_advisory_xact_lock`. On a clash, append `-2`, `-3`… (shortening first so the result stays
  within 30).
- **Sign-up**: refuses a username present in `series_handles`, with the "taken" error.
- **CLI `series-handle`**:
  - validates the handle rule and uniqueness;
  - refuses when the series' thread already has an `object_id` (it has federated);
  - records a security-free log line.

## R5. Outbox worker

- **Claiming**: `SELECT … FROM fediverse_outbox WHERE state = 'pending' AND next_attempt_at <= now()
  AND NOT EXISTS (an older pending item with the same subject) ORDER BY id LIMIT 1 FOR UPDATE SKIP
  LOCKED`, then `claimed_at = now()`. Claims older than 10 minutes are retried, as the import
  processor does.
- **`ensure_thread`**: if the group's thread has an `object_id`, done. Otherwise publish from
  `series-{seriesId}` (key `thread:{seriesId}:{groupKey}`), and store the id and URL from 201 or
  200.
- **`publish_comment`**:
  - comment deleted → done, publishing nothing;
  - comment already has an `object_id` → done;
  - its group has no thread → fail with "waiting for thread" (retry);
  - author can't federate → done.

  Otherwise publish (key `comment:{id}`) and store `object_id`.
- **`update_comment`**:
  - no `object_id` yet → done (the pending publish sends the latest text);
  - deleted → done;
  - otherwise `PUT`.
- **`delete_comment`**: no `object_id` → done; otherwise `DELETE` (202 is done).
- **`delete_actor`**: `DELETE /internal/actors/{id}`; 202 and 404 are done.
- **`refresh_profile`**: `PUT /internal/actors/{id}`; 202 is done; 404 and 410 are done too, since
  nothing is left to refresh.
- **Failures**:
  - **transient** (network, 5xx, 409 `conflict` on idempotency): `attempts++`;
    `next_attempt_at = now + min(1h, 1min × 2^(attempts-1))`; store `last_error` (status and
    Megáfono's error code, never the token).
  - **permanent** (400 `invalid_request`, 410 on a parent): `done` with `last_error` set, logged at
    warning level.
- **Polling**: like the import worker, poll every 5 s when idle, and drain while there's work.
  Configured by `Frunchy:FediverseWorkerEnabled` (default true) and only with federation on.

## R6. Webhooks

- **Dedup**: `INSERT INTO megafono_events … ON CONFLICT (id) DO NOTHING`. If nothing was inserted,
  answer 204 at once. Otherwise apply the event and record its outcome, in one transaction.
- **`reply.received` / `reply.updated`**:
  - find the thread by `object.thread.objectId` = `episode_threads.object_id`; if absent →
    `unknown_thread`;
  - upsert the remote actor;
  - upsert the comment by `object_id`:
    - **new**: insert with `cr_episode_id` = the thread's episode;
    - **existing and live**: if the incoming `updated` (else `published`) is newer than the stored
      `edited_at` (else `created_at`), update the content, summary, sensitive flag, audience and
      `edited_at`; otherwise `stale`;
    - **existing tombstone**: ignore.
- **`object.deleted`**:
  - tombstone by `object_id`;
  - if unknown, insert a tombstone row on the thread's episode (when the thread is known), so a
    late `reply.received` stays deleted.
- **Other types** (`follow.*`, `actor.updated`, `delivery.failed`, unknown): `ignored`, except that
  `actor.updated` upserts `remote_actors`.

## R7. Remote content to text

`htmlToText`:
1. Replace `<br>`, `</p>` and `</li>` (any case or attributes) with `\n`.
2. Strip every tag.
3. Decode entities (`WebUtility.HtmlDecode`).
4. Collapse 3 or more newlines to 2, and trim.

The text is then shown as local comments are (escaped by ViewEngine, `whitespace-pre-line`). The
stored HTML stays in `content` for the record; the view converts on render.

## R8. Pages

- **Dashboard and series page**: a "Follow on the fediverse" line with `@handle@domain` in mono, the
  existing `Components.copyLink` (or a variant for plain text), and the alternate link in `<head>`.
  The layout needs a hook to add head elements; add an optional parameter to `Layout.page`, or a
  `ctx.Items` key the layout reads. Also the `Link` response header.
- **Episode page**: the same alternate link and header for the thread's URL. Remote comments are
  rendered by the same `commentView`, with an author block that differs (remote handle and server,
  external link) and no author controls.

## R9. Testing

- **`tests/.../Infrastructure/FakeMegafono.fs`**: a primary handler for the "megafono" client, like
  `FakeMal`.
  - It records every request: method, path, headers (`Idempotency-Key`), body.
  - Its answers are scripted per route, with defaults: publish → 201 with a generated id and
    `objectUrl` `https://frunchy.test/ap/objects/{id}`, the same body again on a repeated key;
    PUT → 200; DELETE → 202; blocks → 201/200/204.
  - It can fail the next N calls with 503.
- **Test app**: `createAppWith` sets federation on with test settings; a helper signs webhook bodies
  with the test secret.
- **Constraint**: no test needs Docker beyond the existing Postgres container, and none touches a
  real Megáfono.
