# Implementation Plan: Members-only profiles, the new default

**Branch**: `021-members-profiles` (on `fix-changelog-unreleased`) | **Date**: 2026-10-20 | **Spec**: [members-profiles.spec.md](members-profiles.spec.md)

## Design

- **`Infrastructure/Profiles.fs`**:
  - `Visibility` gains `Members` (`toDb "members"`, `tryParse`).
  - `parseDefault`: blank → `Members`, and the error lists the three values.
  - `canView viewer grants owner` = `Public` || (`Members` && `viewer.IsSome`) || owner || matching grant.
  - `PageAccess` gains `MembersPage`. `pageAccess` maps `Members` to it for every viewer (owner or not); the view
    decides on the owner-only note.
  - **The redirect**: `gate` returns a richer result,
    `type GateResult = Show of PageAccess | NotFound | SignInFirst`, where `SignInFirst` is used when the viewer is
    signed out, has no matching grant and the profile is `Members`.
  - **Handlers**: the six `/u/` handlers turn `SignInFirst` into
    `WebAuth.redirectTo ctx ("/signin?returnUrl=" + Uri.EscapeDataString(path + query))` and keep their 404 for
    `NotFound`. Compare is already behind `requireSignedIn`, so it never gets `SignInFirst`.
- **Schema** (`Infrastructure/Database.fs`, `SCHEMA.md`):
  - **New databases**: the `CREATE TABLE profile_settings` CHECK becomes
    `visibility IN ('public', 'members', 'private')`.
  - **Existing databases**: after the `CREATE TABLE`s, a guarded block replaces the CHECK only when its definition
    lacks `members`, so every later run is a no-op:
    ```sql
    DO $$
    BEGIN
      IF EXISTS (SELECT 1 FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid
                 WHERE t.relname = 'profile_settings' AND c.contype = 'c'
                   AND pg_get_constraintdef(c.oid) LIKE '%visibility%'
                   AND pg_get_constraintdef(c.oid) NOT LIKE '%members%') THEN
        -- drop that constraint by its actual name (loop over the matches), then add the new one
      END IF;
    END $$;
    ```
    Look up the old constraint's real name (Postgres usually names it `profile_settings_visibility_check`) and drop
    it with `EXECUTE format('ALTER TABLE profile_settings DROP CONSTRAINT %I', name)` (a fixed SQL fragment, not a
    value from outside; mark the exception in `SqlGuardrailTests` if it complains). Then
    `ALTER TABLE profile_settings ADD CONSTRAINT profile_settings_visibility_check CHECK (visibility IN ('public','members','private'))`.
  - Check whether `initializeSchema` already runs in one transaction under an advisory lock. If not, wrap this block
    so two copies can't race.
  - **Tests**: a test builds the 017 shape (the old CHECK), runs `initializeSchema` twice, then shows `members` is
    accepted, existing rows are unchanged, and the second run changes nothing (the constraint OID is unchanged).
    `SchemaBootstrapTests`' expectations get updated.
- **Account** (`Handlers/Account/Visibility.fs`, `AccountPage.fs`): three radios in the order Public, Members,
  Private; the help texts; `data-visibility` can be `members`.
- **Views**:
  - `Components.profileAddress` handles `MembersPage`: the copy link, plus for the owner the
    `data-profile-members` note linking `/account#visibility`.
  - The Dashboard lead line stays Public-only.
  - The home note: three texts, chosen by the default.
  - Texts in en and es.
- **`Operations/ListUsers.fs`**: `members` with `*` when it comes from the default.
- **Tests**:
  - Update `PrivateProfileTests` (the real default is now `members`; the private behaviour stays tested by setting
    `private`).
  - New `MembersProfileTests`: every spec scenario (signed-in other user 200, operator 200, signed-out redirect with
    the right `returnUrl` for each route including `/year` and the query string, a share link opening a Members
    profile signed out, a choice outliving a default change, the owner's note, the home note, `user-list`, comment
    authors linked for signed-in users only).
  - The fixture default stays `public` (as before), so older tests are unaffected.
- **Docs**:
  - CHANGELOG `## Unreleased`: under Added, the Members level; under Changed, the default is now `members`.
  - README settings table and accounts paragraph.
  - CLAUDE.md: the 017 bullet updated, and the schema rule (guarded, idempotent changes for existing databases).

## Review points

1. A Private profile is still indistinguishable from an unknown username; only Members redirects, and only for the
   signed-out.
2. The schema change is a no-op on the second run and keeps the data.
3. Fail-safe parsing: an unknown stored value can't happen (the CHECK); an unknown setting stops startup.

## Delegation

One Sonnet agent; the main session reviews, runs the suite, and runs the schema upgrade against a copy of the 017
shape.
