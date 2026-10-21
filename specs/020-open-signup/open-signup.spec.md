# Feature Specification: Say sign-up is by invitation, and let the root open it

**Feature Branch**: `020-open-signup`

**Created**: 2026-10-20

**Status**: Draft

**Input**: "I think we should add a dialog after clicking sign in that it's based on invitation also the main operator can set if the sign in needs invitation o no" (the owner, after the first invites on the live site)

## Clarifications

### Session 2026-10-20 (decided in the autonomous run; the owner can overturn any of them)

- Q: What can the root set? → A: The sign-up mode, `invite` (today's behaviour, and the default) or `open` (anyone who signs in with a provider can create an account without a code). Sign-in for existing accounts never changes, and neither does claiming an operator-created account.
- Q: Who sets it, and where? → A: The root only ("the main operator"), on a new page `/review/settings` ("Site settings"), linked from `/account` for the root. It's guarded like operator management (feature 008): `Layout.requireRoot` outermost (everyone else gets the plain 404 on every route and verb); a POST needs a recent sign-in (`requireFresh`) and the antiforgery check; the change is recorded as a `signup_mode_changed` security event on the root's account in the same transaction, plus one information log line.
- Q: Where is the mode kept? → A: In the database, so the root changes it without a redeploy and every copy sees it at once. A new table `instance_settings` (key, value, when, by whom). The schema bootstrap creates a new table on the live database; existing tables stay untouched. No row means `invite`. No configuration key.
- Q: The dialog? → A: In invite mode only, clicking a provider on `/signin` opens a dialog before leaving Frunchy:
  - Title: "Frunchy is invite-only".
  - Body: "Already have an account? Continue and sign in. New here? You'll need an invite code from someone on Frunchy: after you sign in, we'll ask for it."
  - "Continue with {Provider}" goes on to the provider; "Cancel" closes the dialog.
  - "Don't show this again on this device" remembers the choice in the browser only.

  It is a native `<dialog>`: the keyboard and the Esc key work, and focus moves into it and back.
- Q: Without JavaScript? → A: The provider links work as they do today, and the sign-in page carries the same note as plain text under its intro (invite mode only), so nobody depends on the dialog.
- Q: Open mode, everywhere else? → A: The sign-up page has no invite field and says nothing about codes. The sign-in page has no dialog and no note. Copy that says sign-up is invite-only (the home page, the sign-in intro) follows the mode. Invites keep working: operators can still create them, and a code entered in open mode is simply not needed.
- Q: Abuse in open mode? → A: The existing `signup` and `signin` rate limits apply. Anything more (captcha, approval) is out of scope; the root can switch back to invite-only at any time, and existing accounts are unaffected.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Know it's invite-only before signing in (Priority: P1)

A visitor clicks "Sign in with Umbral" on an invite-only Frunchy. A dialog explains that new accounts need an invite code, then lets them continue or cancel.

**Why this priority**: today a newcomer signs in at the provider and only then learns they need a code they don't have.

**Independent Test**: on an invite-only instance, `/signin` carries the dialog and the plain-text note; on an open instance neither.

**Acceptance Scenarios**:

1. **Given** invite mode, **When** `/signin` renders, **Then**:
   - each provider link carries `data-invite-dialog` (it opens the dialog);
   - the page has the dialog (`data-invite-only-dialog`) with a continue link per provider and a cancel button;
   - a visible note (`data-invite-only-note`) says the same in one sentence.
2. **Given** invite mode and JavaScript off, **When** a provider link is followed, **Then** the round trip starts as today.
3. **Given** "Don't show this again on this device" was ticked, **When** the visitor clicks a provider later, **Then** they go straight to the provider; the note stays.
4. **Given** open mode, **When** `/signin` renders, **Then** there is no dialog and no note.

---

### User Story 2 - The root opens or closes sign-up (Priority: P1)

The root opens `/review/settings`, sees the current sign-up mode and changes it.

**Acceptance Scenarios**:

1. **Given** the root signed in within 10 minutes, **When** they choose "Open" and save, **Then**:
   - the mode is `open`;
   - `signup_mode_changed` is on their account;
   - the page shows `?msg=saved`;
   - `data-signup-mode="open"` reflects it.
2. **Given** the root's sign-in is older than 10 minutes, **When** they save, **Then** they go through `/signin/reconfirm` first, as on `/review/operators`.
3. **Given** an operator who isn't root, a regular user or a visitor, **When** they request `/review/settings` with any verb, **Then** the plain 404.
4. **Given** a value other than `invite` or `open`, or no antiforgery token, **When** posted, **Then** nothing changes (`?msg=bad-input`).
5. **Given** the root on `/account`, **When** it renders, **Then** a "Site settings" link (`data-settings-link`) sits with the operator links; nobody else sees it.

---

### User Story 3 - Sign-up follows the mode (Priority: P1)

**Acceptance Scenarios**:

1. **Given** open mode and a new identity, **When** they complete `/signup` with a username and display name and no code, **Then** the account is created and no invite is consumed.
2. **Given** invite mode, **When** a new identity submits `/signup` without a valid code, **Then** it's refused as today.
3. **Given** the mode changes between the sign-up page and its submit, **When** submitted, **Then** the mode at submit time decides.
4. **Given** open mode, **When** `/signup` renders, **Then** it has no invite field and its intro doesn't mention a code.

### Edge Cases

- No root configured: nobody can open sign-up; the mode stays `invite`.
- The `instance_settings` row is missing or holds an unknown value: treated as `invite` (fail closed).
- Several copies of Frunchy: the mode is read per request from the database, so a change applies everywhere at once.

## Requirements *(mandatory)*

- **FR-001**: The sign-up mode MUST be `invite` or `open`, stored in the new table `instance_settings`; missing or unknown means `invite`.
- **FR-002**: `GET /review/settings` and `POST /review/settings/signup` MUST be wrapped in `Layout.requireRoot` outermost. The POST also needs `requireFresh`, `readValidatedForm` and the `operators-write` limiter, and records `signup_mode_changed` in the change's transaction.
- **FR-003**: `/signup` MUST require and consume a valid invite only in `invite` mode, deciding at submit time.
- **FR-004**: In `invite` mode, `/signin` MUST show the plain-text note and the dialog. The dialog is native, accessible and works by keyboard, and has a per-device "don't show again" kept in browser storage only. Without JavaScript, the links work unchanged.
- **FR-005**: Copy that states sign-up is invite-only MUST follow the mode. All copy is in `Views/Texts.fs`, in English and Spanish.
- **FR-006**: Sign-in for existing accounts, claiming, invites and the extension contract are unchanged.

## Success Criteria *(mandatory)*

- **SC-001**: A newcomer on an invite-only Frunchy learns they need a code before leaving for the provider.
- **SC-002**: The root opens or closes sign-up in under a minute, with no redeploy.
- **SC-003**: Non-root users can't tell the settings page exists.

## Assumptions

- "The main operator" is the root (`Frunchy:RootOperator`, feature 008).
- More site settings may join `/review/settings` later; this feature adds only the sign-up mode.
