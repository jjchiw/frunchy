# Tasks: Operators Managed by a Root Operator

**Input**: [root-operator.spec.md](root-operator.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/operators.md](contracts/operators.md)

**Tests**: required by constitution V. Integration tests run against real Postgres.

**Delegation**: Sonnet subagents write the code. The main session reviews each diff, security
first, and runs the tests.

## Phase 1: Setup

- [x] **T001** Register the new files as minimal modules: `Infrastructure/OperatorActions.fs` after `AccountActions.fs`, `Handlers/Review/Operators.fs` after `Handlers/Review/MalReview.fs`, and `Operations/OperatorAdd.fs`, `OperatorRemove.fs` and `OperatorList.fs` after `MalAuto.fs`. Add the test files `OperatorActionsTests.fs` and `OperatorsPageTests.fs` after `MalReviewTests.fs`. `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj, Infrastructure/OperatorActions.fs, Handlers/Review/Operators.fs, Operations/OperatorAdd.fs, Operations/OperatorRemove.fs, Operations/OperatorList.fs, tests/FrunchyBackend.Tests/OperatorActionsTests.fs, tests/FrunchyBackend.Tests/OperatorsPageTests.fs

## Phase 2: Foundational (blocks every story)

**Wave 1, independent (different files):**

- [x] **T002** [P] Add the `operators` table to the schema string (research R2), and to SCHEMA.md · Infrastructure/Database.fs, SCHEMA.md
- [x] **T003** [P] Add `OperatorAdded` and `OperatorRemoved` (`operator_added`, `operator_removed`) to the union, `toDb` and `all` · Domains/Identity.fs
- [x] **T004** [P] Add the operators texts in en and es: page title and intro, the root row label, column labels, add field label and hint, add and remove buttons, the empty state, `OperatorNotice: string -> string` over the seven keys, the account status line and link, and the two event labels in both `SecurityEventLabel` blocks · Views/Texts.fs

**⟶ Wait for Wave 1 to finish, then:**

- [x] **T005** `WebAuth.fs` (research R1):
  - `validatePrincipal` selects the operators flag into `ctx.Items`;
  - `isRoot` reads `Frunchy:RootOperator`;
  - `isOperator` becomes `isRoot || flag`;
  - the `Frunchy:Operators` parsing is removed.

  · Infrastructure/WebAuth.fs
- [x] **T006** `OperatorActions.fs` (research R3, R4): `add`, `remove` and `list`, each write with its events in one transaction. SQL is literal, with parameters · Infrastructure/OperatorActions.fs

**⟶ Wait for T005 and T006, then:**

- [x] **T007** Migrate the 007 tests to the root setting plus table rows (research R7). The operators helper adds rows through `OperatorActions.add … Server`. Replace the array and comma cases with root, table, and "removing from the table closes access at once". Every other assertion is unchanged · tests/FrunchyBackend.Tests/MalReviewAccessTests.fs, tests/FrunchyBackend.Tests/DesignSystemTests.fs

**Checkpoint**: the suite is green on the new access model.

## Phase 3: User Story 1 - The root manages operators (Priority: P1) 🎯 MVP

Files: Views/Layout.fs, Handlers/Review/Operators.fs, Program.fs (endpoints + `operators-write`), tests/FrunchyBackend.Tests/OperatorsPageTests.fs

- [x] **T008** [US1] `OperatorsPageTests`:
  - **Access matrix**: a signed-out visitor, a regular user, an operator who isn't root, and no root configured get 404 on the GET and both POSTs (with and without a token), with nothing changed and no 429 after 15 POSTs.
  - **Root**: the page lists the root separately and operators with added-by and added-at.
  - **Changes**: add and remove work and take effect on the next request.
  - **Refusals**: is-root, unknown, already, not-operator and bad-input change nothing.
  - **Stale session**: redirects to reconfirm and changes nothing.
  - **Events**: added on both accounts, with the right actor.
  - **Cascade**: deleting the operator's account removes the row.
  - **Languages**: en and es.

  · tests/FrunchyBackend.Tests/OperatorsPageTests.fs
- [x] **T009** [US1]:
  - **`Layout.requireRoot`**: shares the 404 branch with `requireOperator`.
  - **`Operators.fs`**: the GET page, and the POST add and remove, in the order `requireRoot`, `requireFresh "/review/operators"`, `readValidatedForm`, `Username.tryCreate`, `OperatorActions`, a log line, then a redirect with `msg`.
  - **Program.fs**: the endpoints, and the `operators-write` limiter (root-only partition, 10 per minute per IP, `GetNoLimiter` otherwise).
  - **Design**: impeccable shape → build → audit.

  · Views/Layout.fs, Handlers/Review/Operators.fs, Program.fs

**Checkpoint**: the root manages operators from the web.

## Phase 4: User Story 2 - Operators know what they are (Priority: P2)

Files: Handlers/Account/AccountPage.fs

- [x] **T010** [US2] The account page takes `isRoot`. Operators get the `data-operator-status` line, and the root also gets `data-operators-link`. Tests go in `OperatorsPageTests` · Handlers/Account/AccountPage.fs

## Phase 5: User Story 3 - A way back in from the server (Priority: P2)

Files: Operations/OperatorAdd.fs, Operations/OperatorRemove.fs, Operations/OperatorList.fs, Program.fs (CLI), tests/FrunchyBackend.Tests/OperatorActionsTests.fs

- [x] **T011** [US3] `CliEnv` gains `RootOperator: string option` from configuration. The three commands use the contract's exit codes and are registered in `cliCommands` and the help text · Operations/OperatorAdd.fs, Operations/OperatorRemove.fs, Operations/OperatorList.fs, Program.fs
- [x] **T012** [US3] `OperatorActionsTests`:
  - **Parity**: the CLI and the web leave identical `operators` rows and events, except the root's own event row.
  - **Exit codes and output**: per the contract, and `operator-list` output.
  - **No root configured**: the CLI still works.

  · tests/FrunchyBackend.Tests/OperatorActionsTests.fs

## Phase 6: Polish

**Wave 1, independent (different files):**

- [ ] **T013** [P] Add the operators page as root to the every-page design-system check · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] **T014** [P] Docs: `Frunchy:RootOperator` replaces `Frunchy:Operators`; the new route and commands; roadmap entry 008 · README.md, CLAUDE.md, FRONTEND.md, specs/007-mal-review/quickstart.md, specs/003-mal-enrichment/contracts/operator-cli.md

**⟶ Wait for Wave 1 to finish, then:**

- [ ] **T015** Run the full `dotnet test` with 0 warnings, validate SC-001 to SC-005, and write the validation log · specs/008-root-operator/quickstart.md

## Dependencies & Execution Order

- **Order**: Setup → Foundational → US1 → US2 → US3 → Polish.
- **Foundational**: T002, T003 and T004 in parallel, then T005 and T006, then T007.
- **Shared files**: `Program.fs` is touched by T009 and T011, and `DesignSystemTests.fs` by T007 and T013. Those pairs run one after the other.
