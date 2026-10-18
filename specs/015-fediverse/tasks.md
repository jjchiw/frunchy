# Tasks: Fediverse Integration

**Input**: [fediverse.spec.md](fediverse.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/frunchy-megafono.md](contracts/frunchy-megafono.md)

**Tests**: required (constitution V), against `FakeMegafono`, never a real Megáfono.

**Delegation**: four Sonnet agents in sequence (plan). The main session reviews each diff and runs the
suite and the AppHost.

## Phase 1: Foundation and accounts (US1)

- [x] T001 Schema: the 5 tables and the index from data-model.md in `initializeSchema`, documented in `SCHEMA.md`. The schema-equality test from 014 compares against the old bootstrap, so update it (the new tables are expected differences; adjust the old-bootstrap copy or the comparison so it still proves the 014 tables unchanged) · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [x] T002 [P] `FediverseRulesTests` (unit):
  - **Signature**: valid, wrong secret, tampered body, 301 s old, future-skewed, missing parts.
  - **Series handles**: long slugs, punctuation, empty, collisions with numeric suffixes still within 30.
  - **`htmlToText`**: paragraphs, `br`, entities, scripts as text, nested tags.
  - **`remoteCommentId`**: fits `^[A-Za-z0-9_-]{1,64}$` and is stable.
  - **`commentHtml`**: escaping and line breaks.

  · tests/FrunchyBackend.Tests/FediverseRulesTests.fs
- [x] T003 `Infrastructure/Fediverse.fs`:
  - the pure rules (research R2, R4, R7);
  - `actorUrl`;
  - `episodeGroupKey` (reuse 009's group rule);
  - `ensureSeriesHandle` (transaction, advisory lock);
  - `enqueue connection tx kind subject`.

  Make T002 pass · Infrastructure/Fediverse.fs
- [x] T004 `Infrastructure/MegafonoClient.fs` (research R1) and `tests/.../Infrastructure/FakeMegafono.fs` (research R9). Register the named client in Program.fs only when federation is on. Tests swap the primary handler as FakeMal does · Infrastructure/MegafonoClient.fs, Program.fs, tests/FrunchyBackend.Tests/Infrastructure/FakeMegafono.fs
- [ ] T005 [US1] `MegafonoActorsTests`:
  - a user by handle and by id, every field;
  - a series by handle and by `series-` id, with the handle created lazily and stable;
  - unknown, deleted and trailing-dash usernames → 404;
  - wrong, missing and right-but-padded tokens → 401;
  - federation off → 404;
  - sign-up refuses a username equal to a series handle.

  · tests/FrunchyBackend.Tests/MegafonoActorsTests.fs
- [ ] T006 [US1] `Handlers/Megafono/Actors.fs` and the sign-up refusal. Endpoints only when federation is on · Handlers/Megafono/Actors.fs, Handlers/Auth/SignUp.fs, Program.fs

## Phase 2: Comments out (US2, US4 out, US5)

- [ ] T007 [US2] `FediverseOutboxTests`, driving `FediverseOutbox.processNext` directly:
  - **First comment on a group**: the thread is published (series actor, key, content, URL), then the reply (user actor, key, `inReplyTo` = the thread's URL, summary/sensitive, URL), and both object ids are stored.
  - **Second comment**: only a reply.
  - **The dub group** shares the thread.
  - **A 503 twice, then 201**: the backoff times grow, and nothing is sent twice.
  - **A repeated worker run** sends nothing.
  - **Edit before publish**: the latest text is published once.
  - **Delete before publish**: nothing is published.
  - **Edit after publish**: `PUT`.
  - **Delete after publish**: `DELETE`.
  - **Account deletion** (web and CLI) queues and sends `DELETE /internal/actors/{id}`; 404 is done.
  - **Display-name change**: `PUT /internal/actors/{id}`.
  - **A trailing-dash user's comment** isn't sent.
  - **Federation off**: nothing is queued.
  - **No secret** appears in `last_error`.

  · tests/FrunchyBackend.Tests/FediverseOutboxTests.fs
- [ ] T008 [US2] `Workers/FediverseOutbox.fs` (research R5) and the enqueue calls:
  - in comment create, edit and delete (the comment's transaction);
  - in account deletion (`AccountActions.deleteUser`, shared with the CLI);
  - in the display-name change.

  Register the worker (enabled with federation on and `Frunchy:FediverseWorkerEnabled`, which the tests turn off, as for the other workers) · Workers/FediverseOutbox.fs, Handlers/Episodes/*.fs, Infrastructure/AccountActions.fs, Handlers/Account/DisplayName.fs, Program.fs
- [ ] T009 [US2] Add the episode thread's alternate link and `Link` header to the episode page, with a layout hook for head links (research R8). Tests go in `FediversePagesTests` (T014) · Views/Layout.fs, Handlers/Episodes/Episode.fs

## Phase 3: Replies in (US3, US4 in)

- [ ] T010 [US3] `MegafonoWebhooksTests` and `RemoteCommentsTests`, using bodies signed with the test secret:
  - **`reply.received`**: shows on the episode page (author name and `@user@host` link with `rel`/`target`, time, text from HTML, spoiler from `summary`/`sensitive`), and `remote_actors` is upserted.
  - **Repeats**: the same event id → 204, unchanged.
  - **Auth**: a bad or old signature or a bad token → 401, nothing stored.
  - **Unknown thread or type**: 204, recorded.
  - **`followers` and `direct` audiences**: stored, not shown.
  - **`reply.updated`**: newer updates, older doesn't.
  - **`object.deleted`**: tombstones it; before `reply.received`, it stays deleted.
  - **A reply to a Frunchy comment**: same episode, `in_reply_to` kept.
  - **Rendering**: a `<script>` in the content shows as text; an operator can hide a remote comment.
  - **Malformed requests**: not JSON → 400; over 1 MB → 413.

  · tests/FrunchyBackend.Tests/MegafonoWebhooksTests.fs, RemoteCommentsTests.fs
- [ ] T011 [US3] `Handlers/Megafono/Webhooks.fs` (research R6) and remote comments in the episode thread's query and view (research R7, R8). Remote comments get the moderation controls, not the author ones · Handlers/Megafono/Webhooks.fs, Handlers/Episodes/Episode.fs, Program.fs

## Phase 4: Discovery, blocks, operations (US1 pages, US6)

- [ ] T012 [US6] `FediverseBlocksTests`:
  - **User blocks**: list, add by handle, by URL and by domain, and remove.
  - **Megáfono's errors**: 400, 404, 409 `blocked` and 502 each show their plain message and change nothing.
  - **Security events**: one per change.
  - **Account page**: the section only with federation on.
  - **Instance blocks** on `/review/fediverse` for operators; the plain 404 on every route and verb for others; never a 429 for outsiders.

  · tests/FrunchyBackend.Tests/FediverseBlocksTests.fs
- [ ] T013 [US6] Build the blocks UI:
  - **Account section**: `Handlers/Account/Blocks.fs` with its POST routes, rendered in `AccountPage.fs`.
  - **Operator page**: `Handlers/Review/FediverseBlocks.fs`.
  - **Wiring**: security event types `fediverse_block_added` and `fediverse_block_removed`; a limiter (`account-write` for users, `review-write` for operators).

  · Handlers/Account/Blocks.fs, Handlers/Account/AccountPage.fs, Handlers/Review/FediverseBlocks.fs, Domains/Identity.fs, Program.fs
- [ ] T014 [US1] `FediversePagesTests`:
  - dashboard and series page: the handle line, copy button, alternate link and `Link` header;
  - episode page: the thread's alternate link and `Link` header once a thread exists;
  - none of these with federation off.

  Implement the handle lines in Dashboard.fs and Series.fs · tests/FrunchyBackend.Tests/FediversePagesTests.fs, Handlers/Users/Dashboard.fs, Handlers/Series/Series.fs
- [ ] T015 CLI `fediverse-status` and `series-handle` (FR-017), with tests in the CLI test style · Operations/FediverseStatus.fs, Operations/SeriesHandle.fs, Program.fs
- [ ] T016 Texts (en and es) for everything added. Design: impeccable audit of the remote comment, the handle lines and the block pages. Docs:
  - **CLAUDE.md**: routes, outbox, settings, CLI.
  - **README.md**: roadmap 015, the federation walk-through under Aspire.
  - **FRONTEND.md**: the new pages and sections.
  - **docs/fediverse.md**: step 3 done; following and feed next.
  - **design/BRIEF.md** §5.5: the handle now built.

  · Views/Texts.fs, docs
- [ ] T017 Run the full `dotnet test`, then the manual AppHost check (SC-006): WebFinger for a user and a series through the gateway; a comment shows up as thread plus reply in Megáfono's database. Append the validation log · specs/015-fediverse/quickstart.md

## Dependencies

- **Phase 1** (T001–T006) → **Phase 2** (T007–T009) → **Phase 3** (T010–T011) → **Phase 4** (T012–T017).
- **Texts** are added as each phase needs them (T016 finishes them).
