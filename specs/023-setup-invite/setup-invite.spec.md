# Feature Specification: The setup invite

**Feature Branch**: `023-setup-invite`

**Created**: 2026-10-22

**Status**: Draft

**Input**: "if there are no users (or active users) we should show the invitation code of the first user in the logs, something as Umbral does when it is setting up the first operator token" (the owner). Today the first invite on a fresh deploy needs a command in Caravana's console (README, "The first account on a new deploy").

## Clarifications

### Session 2026-10-22 (decided in the autonomous run; the owner can overturn any of them)

- Q: When? → A: On every start of the web app (not CLI commands) while the database has **no users at all**. "No active users" is too vague to act on. Once the first account exists, nothing is printed again.
- Q: What is printed? → A: A one-time **setup invite**: a normal invite (code `inv_…`, only its hash stored) marked as a setup invite. It's valid for 7 days and printed to the console (stdout, where `docker logs` and Caravana's Logs tab show it) in a loud banner like Umbral's:
  ```
  ================================================================
    Frunchy has no accounts yet.

    Sign in   {public origin}/signin
    Invite    inv_XXXXXXXXXXXX

    The invite works once and makes its account an operator.
    A restart prints a new invite and cancels this one.
  ================================================================
  ```
  The banner is written with `Console.WriteLine`, not the logger, so log levels and structured sinks can't hide it. A short logger line ("Frunchy has no accounts yet: the setup invite is in the console output") points to it.
- Q: Does the setup invite give special rights? → A: Yes: the account created with it becomes an **operator** (a row in `operators`, in the same transaction that creates the account and consumes the invite), so the first person can create invites on the web (`/review/invites`) without a console. The root is still `Frunchy:RootOperator` (configuration); choosing that username makes them root as well.
- Q: A restart? → A: Like Umbral's token, an announced invite shouldn't outlive the process that announced it. Each start while there are no users **revokes** every unused setup invite and mints a new one. The old code then stops working, and the banner says so.
- Q: Two copies starting together? → A: Under one advisory lock, a copy that finds a setup invite minted less than 2 minutes ago doesn't mint another. It prints a short banner saying another copy just printed the invite ("look for it in the other copy's log"), so two codes never compete.
- Q: Open sign-up mode (feature 020)? → A: The setup invite is still printed. In open mode the first person could sign up without it, but they'd have no operator rights; with it, they get them.
- Q: Turning it off? → A: `Frunchy:SetupInvite:Enabled`, default `true`. Self-hosters who create the first account with the CLI can set it to `false`.
- Q: The database? → A: `invites` gains `is_setup boolean NOT NULL DEFAULT false`: in the `CREATE TABLE` for new databases, and as a guarded, idempotent `ALTER TABLE invites ADD COLUMN IF NOT EXISTS` in `upgrades` for the live database (021's pattern). Existing invites read as not setup.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The first account from the logs (Priority: P1)

The owner deploys a fresh Frunchy, opens the container's logs, copies the setup invite, signs in at `/signin` and enters it on the sign-up page. Their account is created and they're an operator.

**Acceptance Scenarios**:

1. **Given** an empty database, **When** the web app starts, **Then** the console shows the banner with the sign-in address and an `inv_` code, and `invites` holds one unused, unrevoked row with `is_setup = true`, expiring in 7 days, storing only its hash.
2. **Given** that code, **When** a new identity signs up with it, **Then** the account is created, the invite is used, and the account is an operator (on `/account` it sees the operator links, and `/review/invites` opens). An `operator_added` security event is on the account, with `Actor.Operator`, as the CLI's `operator-add` does.
3. **Given** a user exists, **When** the app starts, **Then** nothing is printed and no invite is created.
4. **Given** an empty database and an earlier unused setup invite, **When** the app starts again, **Then** the earlier one is revoked (its code refused at sign-up) and a new one is printed.
5. **Given** an empty database and a setup invite minted less than 2 minutes ago, **When** another copy starts, **Then** it mints nothing and prints the "another copy" banner.
6. **Given** `Frunchy:SetupInvite:Enabled=false`, **When** the app starts on an empty database, **Then** nothing is printed or created.
7. **Given** a normal invite, **When** it's used, **Then** no operator rights are granted (unchanged).
8. **Given** the live database (with `invites` but no `is_setup`), **When** the new version starts, **Then** the column is added once, existing rows read `false`, and a second start changes nothing.

### Edge Cases

- **Operator CLI commands** (`invite-create` and the rest) never print the banner, which only comes from the web app's start.
- **The banner's address** uses the public origin when known (`Frunchy:Federation:Domain`, or the request-independent configured base URL if one exists); otherwise just the path `/signin` with "on this site".
- **Deleting the only user** later: the next start prints a setup invite again, which is correct, since there's no account to get in with.

## Requirements *(mandatory)*

- **FR-001**: At web start, while `users` is empty and the setting is on, revoke unused setup invites and mint and print a new one (FR-002), or print the "another copy" banner (FR-004).
- **FR-002**: The code appears only in the console banner, never in the logger, a security event or the database (hash only).
- **FR-003**: Signing up with a setup invite makes the account an operator in the same transaction, recording `operator_added`.
- **FR-004**: One advisory lock serializes the check-and-mint between copies, with a 2-minute window.
- **FR-005**: `invites.is_setup` exists on new and existing databases (guarded and idempotent), documented in SCHEMA.md.
- **FR-006**: README "The first account on a new deploy" leads with the logs, keeping the console command as the fallback. CHANGELOG `## Unreleased` and CLAUDE.md are updated.
