# Tasks: Operators manage invites on the web

**Input**: [operator-invites.spec.md](operator-invites.spec.md), [plan.md](plan.md)

**Tests**: required, on the real app; `data-*` hooks only.

- [ ] T001 `Infrastructure/InviteActions.fs` (plan "Design"): `create`, `list`, `revoke`, moved from the three
  `Operations/Invite*.fs` files, with the optional operator id that records `invite_created` / `invite_revoked` in the
  same transaction. Add the two `SecurityEventType` cases, their `toDb` names and en/es labels. The CLI files call
  the module with no operator id and keep their output · Infrastructure/InviteActions.fs, Operations/Invite*.fs,
  Domains/Identity.fs, Views/Texts.fs, FrunchyBackend.fsproj
- [ ] T002 `Handlers/Review/Invites.fs`: `GET /review/invites`, `POST /review/invites/create` (show-once page),
  `POST /review/invites/revoke`; every handler wrapped in `Layout.requireOperator` outermost; `review-write`
  limiter; notices `revoked`, `not-active`, `bad-days`, `bad-input`. Register in `Program.endpoints` · Handlers/Review/Invites.fs,
  Program.fs, FrunchyBackend.fsproj, Views/Texts.fs
- [ ] T003 The "Invites" link on `/account` for operators (`data-invites-link`), next to the existing operator links
  · Handlers/Account/AccountPage.fs, Views/Texts.fs
- [ ] T004 `InviteReviewTests`:
  - outsiders (signed out, a regular user) get 404 on every route and verb, and the limiter doesn't count them;
  - a table operator and the root both reach the page;
  - create shows the code once with `no-store`, the database holds only its hash, and the code isn't in the
    events;
  - a new identity signs up with the code and the row then shows used-by;
  - `days` 0, 366 and `abc` are refused;
  - a missing antiforgery token creates nothing;
  - revoking an active invite records the event, and its code is then refused at sign-up;
  - revoking it again gives `not-active`;
  - a bad id gives `bad-input`;
  - the `/account` link shows for operators only;
  - Spanish renders.

  · tests/FrunchyBackend.Tests/InviteReviewTests.fs
- [ ] T005 Docs:
  - **CHANGELOG.md**: a bullet under `## Unreleased`, "Added".
  - **README**: routes and accounts.
  - **CLAUDE.md**: a Routes bullet for feature 019, modelled on 008's.
  - Full suite, then commit.

## Dependencies

T001 → T002 → T003 → T004 → T005.
