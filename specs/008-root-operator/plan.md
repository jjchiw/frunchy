# Implementation Plan: Operators Managed by a Root Operator

**Branch**: `008-root-operator` | **Date**: 2026-10-10 | **Spec**: [root-operator.spec.md](root-operator.spec.md)

## Summary

- **Who is who**: one root operator, the username in `Frunchy:RootOperator`, plus operators stored in
  a new `operators` table. Whether a user is in the table rides along with the user lookup each
  signed-in request already makes.
- **Shared actions**: add, remove and list live in `Infrastructure/OperatorActions.fs`, shared by a
  root-only page at `/review/operators` and three CLI commands. Each change records its security
  events in the same transaction.
- **Page rules**: the page follows feature 007's access rules: a 404 before any form is read, and
  never rate-limited for outsiders. Changes also need a recent sign-in.

## Project Structure

```text
Infrastructure/Database.fs           # operators table (idempotent)
Infrastructure/WebAuth.fs            # flag from validatePrincipal; isRoot; isOperator = root || flag
Infrastructure/OperatorActions.fs    # NEW: add, remove, list (+ security events)
Domains/Identity.fs                  # operator_added, operator_removed
Views/Layout.fs                      # requireRoot (shares the 404 branch with requireOperator)
Views/Texts.fs                       # operators page texts, notices, event labels, account lines
Handlers/Review/Operators.fs         # NEW slice: GET /review/operators, POST add/remove
Handlers/Account/AccountPage.fs      # operator status line, root link
Operations/OperatorAdd.fs, OperatorRemove.fs, OperatorList.fs   # NEW CLI
Program.fs                           # endpoints, CLI, CliEnv.RootOperator, operators-write limiter
SCHEMA.md, README.md, CLAUDE.md, FRONTEND.md, specs/007-mal-review docs
tests: OperatorActionsTests.fs, OperatorsPageTests.fs (NEW); MalReviewAccessTests.fs, DesignSystemTests.fs (migrated)
```

**Structure Decision**: as in features 002 and 007, actions shared by the CLI and the web go in
`Infrastructure/`, and the page is one slice file.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. A new personal table hanging off `users` with ON DELETE CASCADE; it is operator input, not derived data, so nothing needs rebuilding. |
| II. Boring tech | PASS. One table, no package. |
| III. Vertical slices | PASS. One slice for the page; shared actions in `Infrastructure/`. |
| IV. Idempotent ingestion | PASS. Not touched. |
| V. Tests | PASS. Integration tests on real Postgres: access matrix, freshness, CLI/web parity, events on both accounts, cascade on delete, the migrated 007 access tests. |
| Constraints | PASS. Schema in `initializeSchema` + `SCHEMA.md`; texts en + es; CDN-only. |

## Security review points (main session)

- **404 first**: `requireRoot` is outermost on the GET and both POSTs, then `requireFresh`, then the
  form is read.
- **Limiter**: the `operators-write` limiter partitions only the root, so outsiders never get a 429.
- **Protected root**: the root can't be added or removed, and nobody can promote themselves.
- **Old setting**: `Frunchy:Operators` no longer grants access.
- **Atomic events**: the events are written in the action's transaction.

## Delegation

Sonnet subagents write the code. The main session reviews every diff, security first, and runs the
tests.
