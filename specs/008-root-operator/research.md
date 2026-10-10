# Research: Operators Managed by a Root Operator

## R1. Who is root, who is an operator

- **Root**: `WebAuth.isRoot ctx`. The signed-in username equals `Frunchy:RootOperator` (trimmed,
  case-insensitive, empty = no root), read per request.
- **Operator**: `WebAuth.isOperator ctx`, which is `isRoot ctx || <operators-table flag>`.
- **The flag**: `validatePrincipal`'s one query becomes `SELECT display_name, username, EXISTS
  (SELECT 1 FROM operators o WHERE o.user_id = users.id) FROM users WHERE id = @id`, and the flag goes
  in `ctx.Items`. No extra query (FR-002).
- **Callers**: `Layout.requireOperator`, the `review-write` limiter and the account page pick it up
  unchanged.
- **The old setting**: the array and comma parsing of `Frunchy:Operators` is removed, so that setting
  grants nothing (FR-001).

## R2. Schema

Added to `initializeSchema`:

```sql
CREATE TABLE IF NOT EXISTS operators (
    user_id  uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    added_by uuid NULL REFERENCES users(id) ON DELETE SET NULL,
    added_at timestamptz NOT NULL DEFAULT now()
);
```

- **Deleting a user** removes their row (FR-012).
- **Deleting the user who granted it** leaves the grant in place, with "added by" reading as a
  deleted account.
- **SCHEMA.md** gets the table.

## R3. Shared actions

A new `Infrastructure/OperatorActions.fs`, modelled on `AccountActions.fs`:
- `add db (root: string option) (username: string) (by: By)` and
  `remove db (root: string option) (username: string) (by: By)`, plus `list db`.
- `By = Web of rootUserId | Server`.
- **Results**: `UnknownUser | IsRoot | AlreadyOperator | NotOperator | NoRoot`.
- **Transactions**: each write runs in one transaction with its security events, so the CLI records
  them too (FR-009). The CLI adds `RootOperator` to `CliEnv`, read from configuration in `runCli`.
- **The root is protected**: adding or removing the root's username is refused (`IsRoot`), because
  the root is configuration only.
- **No root**: from the web, there can't be one, since `requireRoot` guarantees it. From the CLI,
  `NoRoot` only matters if the root check needs one. The CLI works without a root, so it stays a
  way back in.

## R4. Security activity

Two new events, `operator_added` and `operator_removed`, with labels in both languages.
- **Web, root's account**: `Actor.User`, the root's own action.
- **Web, affected user's account**: `Actor.Operator`, shown as "by the operator", which is accurate.
- **Terminal**: only the affected user's account gets an `Actor.Operator` row, since no root account
  is involved.

`security_events` has no detail column, so neither row names the other person. The application log
does, at information level.
- `// simplified:`: a detail column, if the activity list ever needs the counterparty.

## R5. Access ordering, freshness, rate limit

- **GET `/review/operators`**: `Layout.requireRoot`, the 404 twin of `requireOperator`, comes first.
  The 404 branch is shared between the two.
- **POST `/review/operators/add|remove`**: `requireRoot`, then
  `WebAuth.requireFresh "/review/operators"`, then `readValidatedForm`, then the input check, then
  the action, then a redirect with `?msg=<key>`.
- **Why that order**: non-roots get 404 before any redirect to re-confirmation. A stale root is sent
  to `/signin/reconfirm` and comes back to the page.
- **Rate limit**: a new `operators-write` policy allows 10 per minute per IP for the root only.
  Everyone else gets `GetNoLimiter`, as `review-write` does, so outsiders never see a 429.
- **Username input**: the same rule as sign-up, via `Username.tryCreate`. Anything else gets
  `bad-input`, without a query.

## R6. Account page

`AccountPage.view` takes `isOperator` and `isRoot`:
- **Operators**: a "You're an operator" line (`data-operator-status`) next to the existing review
  link.
- **Root**: also an "Operators" link to `/review/operators` (`data-operators-link`).

## R7. Migrating tests and docs

- **`MalReviewAccessTests`**: `operatorsOf` becomes "root plus database operators". The array and
  comma cases are replaced by root and table cases, including "removing from the table closes the
  pages at once".
- **`DesignSystemTests`**: uses `Frunchy:RootOperator`.
- **Docs**: CLAUDE.md, the README and the 007 docs say `Frunchy:RootOperator`.
