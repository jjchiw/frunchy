# Feature Specification: Operators manage invites on the web

**Feature Branch**: `019-operator-invites`

**Created**: 2026-10-20

**Status**: Draft

**Input**: "we are missing that in the ui … If I am an operator I should be able to generate invitation codes" (the owner, on `/account` of the live site, after creating the first invite with the CLI through Caravana's console)

## Clarifications

### Session 2026-10-20 (decided in the autonomous run; the owner can overturn any of them)

- Q: Who may create invites? → A: Operators: the root and every user in the `operators` table, the same people as `/review/mal`. Everyone else gets the plain 404 on every route and verb, so the page doesn't reveal it exists.
- Q: Where? → A: `/review/invites`, linked from the operator buttons on `/account` next to "Review MyAnimeList matches" and "Manage operators".
- Q: What does the page do? → A: Three things, the same as the CLI's `invite-create`, `invite-list` and `invite-revoke`, through one shared module so web and CLI behave identically:
  - create an invite valid for N days (1 to 365, default 7);
  - list recent invites with their state (active, used and by whom, revoked, expired), created and expiry times;
  - revoke an active invite.
- Q: How is the code shown? → A: Once, on the page the create POST renders (no redirect, `Cache-Control: no-store`), with a copy button and one sentence on how to use it: sign in at `/signin`, then enter the code on the sign-up page. Only its hash is stored, as today, so a lost code is revoked and replaced, never shown again. The code never goes in a URL.
- Q: Who created an invite? → A: Recorded as a security event on the operator's account (`invite_created`, `invite_revoked`, `Actor.User`), in the same transaction as the change. The `invites` table keeps its shape: the live database exists now and the schema bootstrap creates tables but never alters them.
- Q: Limits? → A: The `review-write` limiter (30 POSTs a minute per IP), counted for operators only because the operator check runs first. The list shows the 100 most recent invites.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an invite (Priority: P1)

An operator opens `/review/invites`, picks how many days the invite lasts and creates it. The page shows the code once, with a copy button and how to use it.

**Why this priority**: inviting friends is the next thing the owner does on the live site, and today it needs a command in Caravana's console.

**Independent Test**: as an operator, create an invite on the web, sign up a new identity with that code; it works once.

**Acceptance Scenarios**:

1. **Given** an operator, **When** they post the create form with `days=7`, **Then** the response (200, `no-store`) shows the code in `data-invite-code` with a copy button and the expiry. Only its hash is stored. An `invite_created` event is on the operator's account.
2. **Given** that code, **When** a new identity signs up with it, **Then** the account is created and the invite shows as used by that username.
3. **Given** `days` outside 1–365 or not a number, **When** posted, **Then** nothing is created and the page says so (`?msg=bad-days`).
4. **Given** no antiforgery token, **When** posted, **Then** nothing is created.

---

### User Story 2 - See and revoke invites (Priority: P1)

The page lists recent invites with their state; an active one can be revoked.

**Acceptance Scenarios**:

1. **Given** invites in every state, **When** an operator opens the page, **Then** each row (`data-invite-row`, `data-invite-state`) shows its state, created and expiry times in `<time>` elements, and for a used one the username it was used by.
2. **Given** an active invite, **When** the operator revokes it, **Then** it shows as revoked, its code is refused at sign-up, and an `invite_revoked` event is recorded (`?msg=revoked`).
3. **Given** an invite that is used, revoked, expired or unknown, **When** a revoke is posted, **Then** nothing changes (`?msg=not-active`).

---

### User Story 3 - Only operators (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a visitor or a signed-in user who isn't an operator, **When** they request any `/review/invites` route with any verb, **Then** the plain 404 page, and no limiter counts them.
2. **Given** an operator (root or table), **When** they open `/account`, **Then** an "Invites" link (`data-invites-link`) sits with the other operator links. Others don't see it.

### Edge Cases

- Two operators revoke the same invite at once: one succeeds, the other gets `not-active`.
- A user deleted after using an invite: the row shows "used" without a username (the reference is set to null).
- The CLI keeps its commands and output unchanged.

## Requirements *(mandatory)*

- **FR-001**: `GET /review/invites`, `POST /review/invites/create` and `POST /review/invites/revoke`, every handler wrapped in `Layout.requireOperator` outermost, POSTs with `readValidatedForm` and the `review-write` limiter.
- **FR-002**: Create, list and revoke MUST go through one shared module used by the CLI too, with unchanged CLI behaviour.
- **FR-003**: The code MUST be shown only in the create response (no redirect, `no-store`), never stored in plain text, never placed in a URL or a log.
- **FR-004**: `invite_created` and `invite_revoked` security events on the operator's account, in the change's transaction.
- **FR-005**: An "Invites" link on `/account` for operators only.
- **FR-006**: All copy in `Views/Texts.fs`, English and Spanish; tests read `data-*` hooks only.
- **FR-007**: No schema change.

## Success Criteria *(mandatory)*

- **SC-001**: An operator invites a friend without leaving the browser, in under a minute.
- **SC-002**: Non-operators can't tell the page exists (404 on every route and verb).
- **SC-003**: Codes are never recoverable after the create response.

## Assumptions

- The sign-up flow (sign in with a provider, then enter the code) is unchanged.
- Existing invites in the live database work with the page as they are.
