# Implementation Plan: Operators manage invites on the web

**Branch**: `019-operator-invites` | **Date**: 2026-10-20 | **Spec**: [operator-invites.spec.md](operator-invites.spec.md)

## Summary

`/review/invites` lets operators create invites (code shown once), list recent ones with their state and revoke
active ones, through a shared `Infrastructure/InviteActions.fs` that the CLI's `invite-*` commands also use.
Modelled on `Handlers/Review/Operators.fs` (feature 008) and the show-once page of `Handlers/Account/ShareLink.fs`
(feature 017).

## Technical Context

- F# / .NET 10, Oxpecker, raw Npgsql; no schema change (the live database exists and the bootstrap never alters a
  table).
- **New**: `Infrastructure/InviteActions.fs` (create, list, revoke, each taking an optional transaction-scoped
  security-event writer), `Handlers/Review/Invites.fs`, `tests/FrunchyBackend.Tests/InviteReviewTests.fs`.
- **Changed**: `Operations/InviteCreate.fs`, `InviteList.fs`, `InviteRevoke.fs` (become thin wrappers, same output),
  `Domains/Identity.fs` (`InviteCreated`, `InviteRevoked` event types + labels), `Handlers/Account/AccountPage.fs`
  (link), `Views/Texts.fs`, `Program.fs` (endpoint), `FrunchyBackend.fsproj`, CHANGELOG.md, README, CLAUDE.md.

## Design

- **Create**: `InviteActions.create db (recordFor: Guid option) days` inserts the invite and, when an operator id
  is given, records `invite_created` for that user in the same transaction. Returns `{ Id; Code; ExpiresAt }`.
  `days` validated 1–365 before any query. The web renders a show-once page:
  - the code in IBM Plex Mono (`data-invite-code`) with the existing copy-button component;
  - the expiry as a `<time>`;
  - one sentence: "Send it to the person you invite. They sign in at {host}/signin and enter it on the sign-up
    page. It works once.";
  - a link back to `/review/invites`.

  Headers: `Cache-Control: no-store`.
- **List**: `InviteActions.list db limit` (100 for the web, unlimited for the CLI): the existing query plus `LIMIT`.
- **Revoke**: `InviteActions.revoke db (recordFor: Guid option) id`: `UPDATE … WHERE id = @id AND used_at IS NULL
  AND revoked_at IS NULL AND expires_at > now()`; one row changed → record `invite_revoked`; else `NotActive`. The
  form's id is parsed as a Guid first (bad → `?msg=bad-input`).
- **Page** (`/review/invites`): a notice from `?msg=` (`created` isn't one: create renders), the create form
  (number input `days`, default 7, min 1, max 365), and the table of rows: state pill (active amber, used lilac,
  revoked/expired neutral), created, expires, used by, and a revoke button for active rows. Night Broadcast
  components and colour roles; no new literals outside `Texts.fs`.
- **Order in each handler**: `Layout.requireOperator` (outermost) → for POSTs `readValidatedForm` → validate input →
  action → redirect or render. Limiter `review-write`.

## Review points

1. Every route and verb answers the plain 404 to non-operators, before any form read.
2. The code appears only in the create response; not in logs, events, redirects or URLs.
3. CLI output unchanged (`OperationsTests` keep passing).
4. No schema change.

## Delegation

One Sonnet agent; main session reviews the diff and runs the full suite.
