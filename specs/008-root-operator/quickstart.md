# Quickstart: validate feature 008

Contract: [operators](contracts/operators.md). Rules: [data-model](data-model.md). Decisions:
[research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker (Testcontainers)
```

| Suite | Proves |
|---|---|
| `OperatorsPageTests` (12) | **Access matrix (SC-002):** visitors, users and operators who aren't root get 404 on the GET and both POSTs (with and without a token), no redirect, nothing changed, and no 429 after 15 POSTs; with no root configured nobody gets in. **Page:** the root apart, operators with added-by and added-at, notices, the empty state. **Changes (SC-001):** add and remove take effect on the next request; username goes through the sign-up rule. **Refusals:** is-root, unknown, already, not-operator and bad input change nothing. **Freshness:** a stale session is sent to `/signin/reconfirm?returnUrl=%2Freview%2Foperators` and changes nothing, a stale non-root still gets 404. **Limit:** 429 on the root's 11th POST a minute only. **Events (SC-004):** on both accounts, with the right actor. **Cascade:** deleting an operator's account removes the grant; deleting the root's keeps it with nobody on record. **Languages:** en and es. **Account page:** `data-operator-status` for operators, `data-operators-link` for the root only. |
| `OperatorActionsTests` (4) | **Parity (SC-003):** the terminal and the page leave the same `operators` rows (apart from `added_by`) and the same events, except the root's own event row. **Exit codes** per the contract, and refusals change nothing. **`operator-list` output.** **No root configured:** the terminal still works. |
| `MalReviewAccessTests` (migrated) | The 007 access matrix on the new model: the root, a table operator, the retired `Frunchy:Operators` granting nothing, and removal from the table closing the pages at once. |
| `DesignSystemTests` | The operators page (as the root) and the account page's new hooks are on the design system. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

**SC-005** (same number of queries per signed-in page load): `WebAuth.validatePrincipal` still runs one
query; the operators flag is an `EXISTS` subselect in it, and `isRoot` reads configuration only.

## 2. On real data

Make yourself the root operator (never commit a username to `appsettings*.json`; use user-secrets,
Bella or the environment):

```bash
Frunchy__RootOperator=<your username> bella sdk run -- dotnet run --launch-profile http
open http://127.0.0.1:5099/account
```

1. `/account` shows "You're an operator" and a "Manage operators" link; follow it to `/review/operators`.
2. Add another existing account's username: the notice says it was added and the row shows you as who
   added it. That user's `/account` now shows the operator line and `/review/mal` opens for them, while
   `/review/operators` is a 404 for them.
3. Remove them: their `/review/mal` is a 404 on their next request.
4. Both accounts' security activity shows the "Operator added" / "Operator removed" events (theirs ends in
   "by the operator").
5. Try your own username: the notice says it is the root, nothing changes.
6. After 10 minutes without signing in again, a change sends you to re-confirm and brings you back.
7. From the terminal, with or without `Frunchy__RootOperator` set:

   ```bash
   dotnet run -- operator-add <username>
   dotnet run -- operator-list
   dotnet run -- operator-remove <username>
   ```

## 3. Validation log

- **Date**: 2026-10-10
- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 338 total, 337 passed, 0 failed, 1 skipped (the opt-in MyAnimeList recording test).
  The 321-test baseline became 322 after migrating the 007 access tests, then 338 with
  `OperatorsPageTests` (12) and `OperatorActionsTests` (4).
- SC-001 to SC-004 are covered by the suites above; SC-005 holds by construction (one user query, as before).
- The manual steps in section 2 were not run against a real provider sign-in in this session.
