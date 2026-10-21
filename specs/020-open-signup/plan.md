# Implementation Plan: Say sign-up is by invitation, and let the root open it

**Branch**: `020-open-signup` (on top of `019-operator-invites`) | **Date**: 2026-10-20 | **Spec**: [open-signup.spec.md](open-signup.spec.md)

## Design

- **Table** (`initializeSchema` + `SCHEMA.md`, "Instance" data class, next to `data_protection_keys`):
  ```sql
  CREATE TABLE IF NOT EXISTS instance_settings (
      key         text PRIMARY KEY,
      value       text NOT NULL,
      updated_at  timestamptz NOT NULL DEFAULT now(),
      updated_by  uuid NULL REFERENCES users(id) ON DELETE SET NULL
  );
  ```
  `SchemaBootstrapTests`: the new table is an expected difference, as 016 did for `data_protection_keys`.
- **`Infrastructure/SignUpMode.fs`**:
  - `type SignUpMode = Invite | Open`, with `parse` (unknown → `Invite`) and `toDb`;
  - `current db`: one primary-key query on `instance_settings` (key `signup_mode`; no row → `Invite`);
  - `set connection tx rootId mode`: an upsert that records `SecurityEventType.SignUpModeChanged` for `rootId` in
    the same transaction.

  New `SecurityEventType` case `SignUpModeChanged` (`signup_mode_changed`), with en and es labels.
- **`Handlers/Review/Settings.fs`**, modelled on `Handlers/Review/Operators.fs`:
  - **`GET /review/settings`** (`Layout.requireRoot`): a section "Sign-up" with two radios, Invite only (default) and
    Open, each with a one-line help, plus Save and the current state (`data-signup-mode`).
  - **`POST /review/settings/signup`**: `requireRoot` → `requireFresh "/review/settings"` → `readValidatedForm` →
    `mode` must be `invite` or `open` (else `?msg=bad-input`) → `SignUpMode.set` → log line naming the root and the
    mode → `?msg=saved`. Limiter `operators-write`.
- **`/account`**: a "Site settings" link (`data-settings-link`) next to "Manage operators", for the root only (same
  condition as that link).
- **Sign-in** (`Handlers/Auth/SignIn.fs`):
  - `page` reads `SignUpMode.current`.
  - In `Invite` mode, the intro gets the note `t.SignInInviteOnlyNote` (`data-invite-only-note`).
  - Each provider `<a>` keeps its `href` and gains `data-invite-dialog` plus an Alpine click handler. When the
    browser hasn't stored `frunchy.inviteNoticeSeen`, the handler prevents the default, remembers the provider's
    href and calls `$refs.inviteDialog.showModal()`.
  - The `<dialog>` (`data-invite-only-dialog`, `x-ref`) has a title, the body, a checkbox "Don't show this again on
    this device", a primary "Continue with {Provider}" link (the remembered href; ticking the box stores the key
    first), and a Cancel button that closes it.
  - Every `localStorage` access is wrapped in try/catch. Without Alpine, the links work as plain links.
  - Styling follows Night Broadcast: a surface card, solid fills, amber primary, fully rounded buttons, and
    `::backdrop` dimmed.
- **Sign-up** (`Handlers/Auth/SignUp.fs`):
  - `page` and the view take the mode: in `Open` mode there's no invite field and the intro is `t.SignUpIntroOpen`.
  - `submit` reads the mode at submit time. `Invite` keeps today's path. `Open` creates the account in the same
    transaction without the invite step: split `createAccount` so the invite consume is optional; it stays one
    transaction.
- **Copy that says invite-only**: home page and sign-in intro texts get an open-mode variant where they mention
  invites (grep `Views/Texts.fs` for invite wording).
- **Tests** (`SignUpModeTests`, plus extending `SchemaBootstrapTests`): every scenario in the spec. The dialog is
  checked by its hooks and markup (no JS runtime in tests). Open-mode sign-up must not touch `invites`. Root-only
  404s on every route and verb, `requireFresh` redirect, and the event recorded.

## Review points

1. Missing or unknown setting fails closed (`Invite`).
2. Root-only with `requireFresh`, the 404 for everyone else, and the event in the same transaction.
3. Open-mode sign-up still runs in one transaction with the username checks unchanged (`Username.tryCreateNew`).
4. Without JavaScript nothing breaks, and `localStorage` failures can't block sign-in.

## Delegation

One Sonnet agent; the main session reviews the diff, runs the suite and looks at `/signin` in a browser locally.
