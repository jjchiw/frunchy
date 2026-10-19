# Tasks: Private Profiles

**Input**: [private-profiles.spec.md](private-profiles.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/visibility.md](contracts/visibility.md)

**Tests**: required (constitution V), on the real app. Tests read `data-*` hooks only.

**Delegation**: two Sonnet agents in sequence (plan). The main session reviews each diff and runs the suite.

## Phase 1: The rule and private by default (US1)

- [x] T001 Schema: `profile_settings` per data-model.md in `initializeSchema`, documented in `SCHEMA.md`
  (Accounts). Update `SchemaBootstrapTests` for the expected new table · Infrastructure/Database.fs, SCHEMA.md,
  tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [x] T002 `ShareToken` (generate, hash, shape check) next to `SyncKey` in `Domains/Identity.fs`, plus the four new
  `SecurityEventType` cases with their `toDb` names (data-model.md) · Domains/Identity.fs
- [x] T003 `Infrastructure/Profiles.fs` (research R2, R4): `Visibility`, `parseDefault`, `ProfileSettings`
  singleton, `effective`, `canView` (pure), `loadAccess`, the grant cookie (read, add, write), and `visibleTo`.
  Register the singleton in `Program.configureServices`; an invalid setting throws there naming the key
  · Infrastructure/Profiles.fs, FrunchyBackend.fsproj, Program.fs
- [x] T004 [P] `ProfileRulesTests` (unit): `canView` over public, private, owner, a matching grant, a grant with an old
  link id, a grant for another user; `parseDefault` (missing, `PUBLIC`, ` private `, `friends`); and the grant
  cookie round trip with 21 grants, keeping the newest 20 · tests/FrunchyBackend.Tests/ProfileRulesTests.fs
- [x] T005 Fixture: `createAppWith` adds `Frunchy:Profiles:DefaultVisibility=public` before `extraConfig` (research R7),
  with a comment saying why; the whole existing suite must still pass · tests/FrunchyBackend.Tests/Infrastructure/PostgresFixture.fs
- [x] T006 Apply the rule on every `/u/` route (research R5): dashboard, DNA, year (both routes), anime, compare (with
  the compare order). Hidden profiles get the same unknown-user 404. The owner's private pages show
  `Components.privateProfileNote` in place of the copy-link block. Add texts in en and es (`Views/Texts.fs`)
  · Handlers/Users/*.fs, Views/Components.fs, Views/Texts.fs
- [x] T007 Home note by default (two texts, en and es); `user-list` visibility column · Handlers/Home.fs,
  Views/Texts.fs, Operations/ListUsers.fs
- [x] T008 [P] `PrivateProfileTests`, with the default `private`:
  - **Strangers**: each route (dashboard, DNA, `/year`, `/year/{y}`, an anime page, compare as another signed-in
    user) answers 404, signed out, as another user and as the root operator.
  - **Indistinguishable**: the body equals an unknown username's after removing per-request values.
  - **Owner**: sees every page with `data-profile-private`.
  - **The real default**: with no setting at all, the default is private (pin `parseDefault` and one route via an
    app built with `extraConfig` removing the key, i.e. set to `""`).
  - **Public default**: everything opens.
  - **Invalid setting**: refuses to start, naming the key.
  - **Home note**: follows the default.

  · tests/FrunchyBackend.Tests/PrivateProfileTests.fs
- [x] T009 Full suite; commit "impl(017): profiles follow a visibility rule, private by default".

## Phase 2: Choosing, sharing, and everything that links (US2, US3, US4)

- [ ] T010 `Handlers/Account/Visibility.fs` and the account section (research R6): the radios and the link state,
  `?msg=` notices, en and es texts · Handlers/Account/Visibility.fs, Handlers/Account/AccountPage.fs, Views/Texts.fs
- [ ] T011 `Handlers/Account/ShareLink.fs`: create or replace (renders the show-once page, following
  `Handlers/Account/SyncKey.fs`) and revoke · Handlers/Account/ShareLink.fs, Views/Texts.fs
- [ ] T012 `Handlers/Share.fs` `GET /s/{token}` (research R4) and the `share` limiter (30 a minute per IP) in
  `Program.fs` · Handlers/Share.fs, Program.fs
- [ ] T013 Comments link only visible authors; the MyAnimeList review links to series pages (research R5)
  · Handlers/Episodes/Episode.fs, Handlers/Review/MalReview.fs
- [ ] T014 [P] `ProfileVisibilityTests` (US2):
  - choosing public opens the profile to strangers, and choosing private hides it;
  - the choice survives a default change (an app on the same database with the other default);
  - invalid value, missing antiforgery and signed-out posts change nothing;
  - the security events are recorded.

  · tests/FrunchyBackend.Tests/ProfileVisibilityTests.fs
- [ ] T015 [P] `ShareLinkTests` (US3):
  - **Create and use**: create a link and read it from `data-share-link`. A fresh browser opening it gets a 303 to
    the dashboard, then 200 on the dashboard, DNA, a year and an anime page.
  - **Replace**: the old browser gets 404 and the new link works.
  - **Turn off**: 404.
  - **Other users**: a grant for A doesn't open B.
  - **Bad tokens**: malformed, unknown and replaced tokens get 404 and no `Set-Cookie`.
  - **Tampered cookie**: ignored.
  - **Owner**: opening their own link redirects.
  - **Rate limit**: the 31st request in a minute gets 429.
  - **Database**: only the hash is stored (the token string isn't in `profile_settings`); the events are recorded.

  · tests/FrunchyBackend.Tests/ShareLinkTests.fs
- [ ] T016 [P] `ProfileLinksTests` (US4):
  - **Comment authors**: a private author is plain text for a stranger and linked for the owner and for a link holder.
  - **Compare**: 404 on a hidden profile, and it works on a shared one.
  - **Card**: a private viewer sees the card on a public profile.
  - **MyAnimeList review**: rows link to `/anime/{id}`, never `/u/`.
  - **Federation on**: a stranger gets 404 without the `Link` header; the owner's page still has it.

  · tests/FrunchyBackend.Tests/ProfileLinksTests.fs
- [ ] T017 Docs: README (accounts paragraph, settings table, routes, roadmap row 017 implemented), CLAUDE.md
  (routes and the rule), SCHEMA.md (done in T001), the quickstart validation log. Commit "impl(017): visibility
  choice, share links, links follow the rule, docs".

## Dependencies

T001 → T002 → T003 → T004/T005 → T006 → T007 → T008 → T009. Phase 2 after T009: T010 → T011 → T012 → T013, then T014–T016 in parallel, then T017.
