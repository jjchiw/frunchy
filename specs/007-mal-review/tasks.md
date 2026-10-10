# Tasks: Review MyAnimeList Matches on the Web

**Input**: [mal-review.spec.md](mal-review.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/review-pages.md](contracts/review-pages.md)

**Tests**: required by constitution V. Integration tests run against real Postgres with `FakeMal`
and never call the real MyAnimeList.

**Every UI task**: load the `oxpecker` and `impeccable` skills. Texts come only from
`Views/Texts.fs` (en + es). Test hooks are exactly the ones in the contract.

**Delegation**: Sonnet subagents write the code. The main session reviews every diff, security
first, and runs the tests.

## Phase 1: Setup

- [ ] **T001** Register the new files as minimal modules: `Infrastructure/MalActions.fs` after `MalClient.fs`, `Handlers/Review/MalReview.fs` after `Handlers/Users/Anime.fs`, `Operations/MalAuto.fs` after `MalRebuild.fs`, and the test files `MalActionsParityTests.fs`, `MalReviewAccessTests.fs` and `MalReviewTests.fs` after `AnimeLinkTests.fs`. `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj, Infrastructure/MalActions.fs, Handlers/Review/MalReview.fs, Operations/MalAuto.fs, tests/FrunchyBackend.Tests/MalActionsParityTests.fs, tests/FrunchyBackend.Tests/MalReviewAccessTests.fs, tests/FrunchyBackend.Tests/MalReviewTests.fs

## Phase 2: Foundational (blocks every story)

**Wave 1, independent (different files):**

- [ ] **T002** [P] `MalActions.fs` (research R3):
  - **Expect guard**: `Expect = Open | Manual | Any`, checked with `SELECT … FOR UPDATE` in the same transaction as the write.
  - **`setMatch`**: today's `MalSet.setMatch` moved here, plus `NotOpen`.
  - **`setNone`**, **`retry`** (moved from `MalRetry`) and **`backToAutomatic`** (new: the season and its dub rows to pending, the series' searches dropped).
  - **SQL**: literal, with values as parameters.

  · Infrastructure/MalActions.fs
- [ ] **T003** [P] `WebAuth.fs` (research R1, R2):
  - `validatePrincipal` also selects `username` into `ctx.Items`;
  - `currentUsername`;
  - `isOperator`, which reads `Frunchy:Operators` per request (array or comma list, case-insensitive, empty = nobody);
  - `requireOperator`, which answers 404 with the not-found message page for anyone else, and never redirects.

  · Infrastructure/WebAuth.fs
- [ ] **T004** [P] Add the `SecurityEventType` cases `mal_set`, `mal_none`, `mal_retry` and `mal_auto` (union, `toDb`, `all`) · Domains/Identity.fs
- [ ] **T005** [P] Add the review texts and the four event labels in en and es: page titles, the filter labels, column labels, the action buttons, the MAL id field label and hint, the manual view, the empty state, "N more", the account link, and the nine notices. Labels go in both `SecurityEventLabel` blocks · Views/Texts.fs

**⟶ Wait for Wave 1 to finish, then:**

- [ ] **T006** Point `mal-set`, `mal-none` and `mal-retry` at `MalActions` with `Any`, keeping their output and exit codes. Add `mal-auto <crSeasonId>` (exit 0, 2 for an unknown season, 3 when not manual) and register it in Program.fs's CLI dispatch and the help text. `MalOperationsTests` stays green with no assertion edited · Operations/MalSet.fs, Operations/MalNone.fs, Operations/MalRetry.fs, Operations/MalAuto.fs, Program.fs

**⟶ Wait for T006, then:**

- [ ] **T007** Write `MalActionsParityTests`:
  - for set, none, retry and auto, the CLI command and a direct `MalActions` call with `Any` leave identical `cr_season_matches` rows, dubs included (SC-001);
  - `Open` and `Manual` refuse wrong states with `NotOpen` and write nothing;
  - `setMatch` without a client id is `Unavailable`, and it makes no request.

  · tests/FrunchyBackend.Tests/MalActionsParityTests.fs

**Checkpoint**: the CLI behaves as before, plus `mal-auto`; the suite is green.

## Phase 3: User Stories 1 and 3 - Fix seasons, operators only (Priority: P1) 🎯 MVP

Files: Handlers/Review/MalReview.fs, Handlers/Account/AccountPage.fs, Program.fs (endpoints + the `review-write` limiter), tests/FrunchyBackend.Tests/MalReviewAccessTests.fs, tests/FrunchyBackend.Tests/MalReviewTests.fs

### Tests

- [ ] **T008** [US3] Write `MalReviewAccessTests`, with the operators configured through `createAppWith`:
  - **Access matrix**: for every route and verb (each POST with a valid token taken from an operator-visible page, and without one), a signed-out visitor, a signed-in non-operator and a configuration with no operators all get 404 and no data change; the operator gets 200 or 302.
  - **Rate limit**: 429 after 30 POSTs in a minute.
  - **Account link**: the operator's account page has `data-review-link`, and others don't.
  - **Security activity**: each successful action adds its event to the operator's account.

  · tests/FrunchyBackend.Tests/MalReviewAccessTests.fs
- [ ] **T009** [US1] Write `MalReviewTests`. Use the 2026 sample enriched by `routeSampleCatalog`; Slime's "OVA Season 1" `GR8D0NQ7R` is ambiguous there.
  - **List**: it shows exactly the open seasons in CLI order. The status filter works. Each row's users, fully watched count and watcher link are right. The ambiguous candidates are listed.
  - **Actions**: picking a candidate, entering an id, entering a link, marking none, and asking for a retry each redirect with the right notice and leave the same rows as the CLI.
  - **Dubs**: they follow their original.
  - **Refusals**: stale forms get `not-open`, bad input gets `bad-input`, an unknown season gets `unknown-season`, an unknown anime gets `unknown-anime` (FakeMal 404), and a missing client id gets `unavailable`.
  - **Languages**: en and es.

  · tests/FrunchyBackend.Tests/MalReviewTests.fs

### Implementation

- [ ] **T010** [US1] [US3] `MalReview.fs`:
  - **GET `/review/mal`**: the list (research R4), with the filter, the rows, the candidates each with a "Use this" form, the id/link form, the none and retry forms, the notices, the "N more" note and the empty state.
  - **POST set, none and retry**: `requireOperator` first, then `readValidatedForm`, input validation (R5), the `MalActions` call with `Open`, the security event (R6), and a redirect with the notice.
  - **Program.fs**: register the endpoints with `RequireRateLimiting("review-write")` on the POSTs, and add the `review-write` policy (30 per minute per IP).
  - **Design**: impeccable shape → build → audit.

  · Handlers/Review/MalReview.fs, Program.fs
- [ ] **T011** [US3] The account page's operator-only link (`data-review-link`) · Handlers/Account/AccountPage.fs

**Checkpoint**: an operator fixes seasons from the web, and everyone else gets 404.

## Phase 4: User Story 2 - Undo a manual decision (Priority: P2)

Files: Handlers/Review/MalReview.fs (the manual view and auto action only), tests/FrunchyBackend.Tests/MalReviewTests.fs (manual cases)

- [ ] **T012** [US2] Add tests to `MalReviewTests`: the manual view lists manual decisions newest first, and "back to automatic" makes the row pending (requested) and drops it from the view. A stale form gets `not-open` · tests/FrunchyBackend.Tests/MalReviewTests.fs
- [ ] **T013** [US2] `GET /review/mal/manual` and `POST /review/mal/auto` (with `Manual`), plus the tab links between the two views · Handlers/Review/MalReview.fs

## Phase 5: Polish

**Wave 1, independent (different files):**

- [ ] **T014** [P] Add the review page (as an operator) to the every-page design-system check · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] **T015** [P] Docs: the routes, `Frunchy:Operators`, `mal-auto`, and roadmap entry 007 · README.md, CLAUDE.md, FRONTEND.md, specs/003-mal-enrichment/contracts/operator-cli.md

**⟶ Wait for Wave 1 to finish, then:**

- [ ] **T016** Run the full `dotnet test` with 0 warnings and validate SC-001 to SC-005. Record the result in a validation log, with the real-data check as operator on `frunchydb` · specs/007-mal-review/quickstart.md

## Dependencies & Execution Order

- **Order**: Setup → Foundational → US1/US3 → US2 → Polish. US2 edits the same slice as US1, after it.
- **Foundational**: T002–T005 in parallel, then T006, then T007.
- **US1/US3**: T008 and T009, then T010, then T011.
- **US2**: T012, then T013.
- **Polish**: T014 and T015 in parallel, then T016.
