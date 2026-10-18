# Implementation Plan: Fediverse Integration

**Branch**: `015-fediverse` | **Date**: 2026-10-17 | **Spec**: [fediverse.spec.md](fediverse.spec.md)

## Summary

Frunchy implements Megáfono's app contract:
- **Actor lookup**: users as Persons and series as Groups, with stored series handles.
- **Webhook receiver**: signed and deduplicated; it stores remote replies as remote comments shown as
  text.

Every call Frunchy makes goes through a Postgres outbox drained by a worker:
- the thread per episode group;
- comment publish, edit and delete;
- actor deletion and profile refresh.

Blocks are synchronous pages for users and operators. Dashboards, series pages and episode pages
advertise their actor or thread to Mastodon.

## Technical Context

- **Language/Version**: F# on .NET 10.
- **Primary Dependencies**: Oxpecker and Npgsql, plus a named `HttpClient`. No new package.
- **Storage**: PostgreSQL, with 5 new tables (data-model).
- **Testing**: xUnit v3 and Testcontainers, with `FakeMegafono` in place of the "megafono" client and
  signed webhook bodies.
- **Target Platform**: server-rendered HTML plus the outbox worker.
- **Project Type**: web service.
- **Performance Goals**: comments never wait on Megáfono; the worker drains in order.
- **Constraints**:
  - constant-time token and signature checks;
  - no secret logged;
  - remote markup never rendered;
  - federation off means no change.
- **Scale/Scope**: 4 new slices, 1 worker, 2 infrastructure modules, 2 CLI commands, about 40 texts
  per language.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Watch history is untouched. The new tables hold fediverse state and user content; none are derived from imports. |
| II. Boring tech | PASS. Postgres is the queue (`SKIP LOCKED`), with no broker and no package. |
| III. Vertical slices | PASS. One file per endpoint group. The shared pieces (client, rules, enqueue) are in `Infrastructure/`, because several slices and the worker use them. |
| IV. Async, idempotent | PASS. External calls run only in the worker, except block pages, which are interactive by nature. Idempotency keys are Megáfono's, and webhook dedup is by event id. Failures are recorded per item with a retry time. |
| V. Tests | PASS. Integration tests on real Postgres for every slice and the worker, against the fake Megáfono; unit tests for signature, handles, html-to-text and ids. |
| Constraints | PASS. The schema goes in `initializeSchema` and `SCHEMA.md`; en and es texts; CDN-only frontend. |

## Project Structure

```text
Infrastructure/Database.fs, SCHEMA.md            # 5 tables
Infrastructure/MegafonoClient.fs                 # NEW
Infrastructure/Fediverse.fs                      # NEW: pure rules + enqueue + series handles
Handlers/Megafono/Actors.fs, Webhooks.fs         # NEW
Handlers/Episodes/Episode.fs, EditComment.fs, DeleteComment.fs   # enqueue; remote comments render; thread alternate link
Handlers/Users/Dashboard.fs, Handlers/Series/Series.fs           # handle line + alternate link
Handlers/Account/Blocks.fs (+ AccountPage.fs section), Handlers/Review/FediverseBlocks.fs   # NEW
Handlers/Auth/SignUp.fs                          # refuse series handles
Infrastructure/AccountActions.fs, Handlers/Account/DisplayName.fs   # enqueue delete_actor / refresh_profile
Views/Layout.fs                                  # head alternate link + Link header hook
Views/Texts.fs                                   # en + es
Workers/FediverseOutbox.fs                       # NEW
Operations/FediverseStatus.fs, Operations/SeriesHandle.fs   # NEW CLI
Program.fs                                       # client, worker, endpoints, CLI, limiter
tests: FediverseRulesTests (unit), MegafonoActorsTests, MegafonoWebhooksTests, FediverseOutboxTests, RemoteCommentsTests, FediverseBlocksTests, FediversePagesTests; Infrastructure/FakeMegafono.fs
docs: CLAUDE.md, README.md, FRONTEND.md, docs/fediverse.md, design/BRIEF.md (5.5 handle)
```

## Review points (main session)

- **Auth**: constant-time token and signature; 5-minute window; raw bytes hashed; 1 MB cap.
- **No secrets**: none in logs or `last_error`.
- **Remote content**: never rendered as HTML. Profile links carry `rel="noopener nofollow ugc"`.
  Only `https` URLs are linked.
- **Federation off**: nothing happens and every route is 404.
- **Outbox**: no payloads (the subject's state is read when sending), per-subject order, permanent
  vs transient failures.
- **Blocks**: operators only on the instance page, with the review pages' 404 rule.

## Delegation

Four Sonnet agents in sequence:
1. schema, rules, client, fake and actor lookup;
2. outbox and comments out;
3. webhooks and remote comments, edits and deletes in;
4. accounts, blocks, pages, CLI and docs.

The main session reviews each, then runs the AppHost by hand (SC-006).

## Complexity Tracking

None. The outbox is the constitution's "Postgres as the job queue".
