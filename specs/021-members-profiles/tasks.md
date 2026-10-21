# Tasks: Members-only profiles, the new default

**Input**: [members-profiles.spec.md](members-profiles.spec.md), [plan.md](plan.md)

**Tests**: required, on the real app; `data-*` hooks only.

- [ ] T001 Schema: new CHECK in `CREATE TABLE profile_settings`, and the guarded, idempotent upgrade of an existing
  database (plan, "Schema"); `SCHEMA.md` documents it and the new rule; an upgrade test from the 017 shape (twice,
  data kept, second run a no-op) · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
  (or a new SchemaUpgradeTests.fs)
- [ ] T002 `Profiles.fs`: the `Members` level, `parseDefault` (blank means members), `canView`, `MembersPage`, and
  `GateResult` with `SignInFirst` · Infrastructure/Profiles.fs
- [ ] T003 The six `/u/` handlers: redirect `SignInFirst` to `/signin?returnUrl=…` (path + query, local), and keep
  the 404 for `NotFound` · Handlers/Users/*.fs
- [ ] T004 Account: three radios, the POST accepting the three values, texts · Handlers/Account/Visibility.fs,
  Handlers/Account/AccountPage.fs, Views/Texts.fs
- [ ] T005 The owner's members note, the home note (three texts), and `user-list` · Views/Components.fs,
  Handlers/Home.fs, Operations/ListUsers.fs, Views/Texts.fs
- [ ] T006 Tests: update `PrivateProfileTests` (default now members), add `MembersProfileTests` (plan "Tests"), and
  keep `ShareLinkTests`, `ProfileVisibilityTests` and `ProfileLinksTests` passing
  · tests/FrunchyBackend.Tests/MembersProfileTests.fs, tests/FrunchyBackend.Tests/PrivateProfileTests.fs
- [ ] T007 Docs and commit:
  - CHANGELOG `## Unreleased`: Added (the Members level) and Changed (the default is now members).
  - README and CLAUDE.md.
  - Commit after the full suite passes.

## Dependencies

T001 → T002 → T003 → T004 → T005 → T006 → T007.
