# Tasks: Say sign-up is by invitation, and let the root open it

**Input**: [open-signup.spec.md](open-signup.spec.md), [plan.md](plan.md)

**Tests**: required, on the real app; `data-*` hooks only.

- [x] T001 Schema: `instance_settings` in `initializeSchema` and `SCHEMA.md`; `SchemaBootstrapTests` expects it
  · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [x] T002 `Infrastructure/SignUpMode.fs` and the `SignUpModeChanged` event type (en and es label)
  · Infrastructure/SignUpMode.fs, Domains/Identity.fs, Views/Texts.fs, FrunchyBackend.fsproj
- [x] T003 `Handlers/Review/Settings.fs`, its endpoint in `Program.endpoints`, and the root-only "Site settings" link
  on `/account` · Handlers/Review/Settings.fs, Program.fs, Handlers/Account/AccountPage.fs, Views/Texts.fs,
  FrunchyBackend.fsproj
- [x] T004 Sign-up follows the mode (page and submit, account creation without the invite step in open mode, one
  transaction) · Handlers/Auth/SignUp.fs, Views/Texts.fs
- [x] T005 Sign-in: the invite-only note, and the dialog with its per-device "don't show again"; the copy that
  mentions invites follows the mode · Handlers/Auth/SignIn.fs, Handlers/Home.fs, Views/Texts.fs
- [x] T006 `SignUpModeTests` covering the spec's scenarios and edge cases (missing or unknown row means invite; no
  root configured) · tests/FrunchyBackend.Tests/SignUpModeTests.fs
- [x] T007 Docs and commit:
  - **CHANGELOG.md**: under `## Unreleased`, "Added".
  - **README**: routes and accounts.
  - **CLAUDE.md**: a Routes bullet for feature 020.
  - **Commit**: after the full suite passes.

## Dependencies

T001 → T002 → T003 → T004 → T005 → T006 → T007.
