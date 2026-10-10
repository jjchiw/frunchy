# Contract: the operators page and commands

## Access

- **Root**: a signed-in user whose username is `Frunchy:RootOperator` gets the page and its actions.
- **Everyone else**: signed out, a regular user, or an operator who isn't root get **404** with the
  site's "Page not found" page. That holds for GET and POST, before any freshness or forgery check,
  with no rate limit, and nothing changes.

## Routes

| Method | Route | Form fields | Result |
|---|---|---|---|
| GET | `/review/operators` | none | the root's page |
| POST | `/review/operators/add` | `username` | 302 to `/review/operators?msg=<key>` |
| POST | `/review/operators/remove` | `username` | 302 to `/review/operators?msg=<key>` |

- **Recent sign-in**: a POST with a sign-in older than 10 minutes is redirected to
  `/signin/reconfirm?returnUrl=%2Freview%2Foperators` and changes nothing.
- **Forgery and rate limit**: every POST carries the antiforgery token and is limited by
  `operators-write`, 10 per minute per IP, counting the root only.

**Notice keys** (`msg`): `added` and `removed` (success); `unknown-user`, `is-root`,
`already-operator`, `not-operator` and `bad-input` (nothing changed).

## Test hooks

| Element | Hook |
|---|---|
| Page root | `data-operators` |
| The root's row | `data-operators-root` = root username |
| An operator row | `data-operator` = username, with `data-added-by` (username or empty) and `data-added-at` (ISO) |
| Add form | `data-action="add"` |
| Remove form on a row | `data-action="remove"` |
| Notice | `data-operators-notice` = key |
| Empty list | `data-operators-empty` |
| Account page: status line (operators) | `data-operator-status` |
| Account page: link (root only) | `data-operators-link`, href `/review/operators` |

## Commands

| Command | Exit codes |
|---|---|
| `operator-add <username>` | 0 added; 1 usage or bad username; 2 unknown user; 3 already an operator; 4 is the root |
| `operator-remove <username>` | 0 removed; 1 usage or bad username; 2 unknown user; 3 not an operator; 4 is the root |
| `operator-list` | 0; prints the root (from configuration, or "none") and each operator with who added them and when |

The commands and the page call the same `Infrastructure/OperatorActions.fs` functions.

## Setting

`Frunchy:RootOperator`: one username, `Frunchy__RootOperator` in Bella or environment variables.
`Frunchy:Operators` (feature 007) is retired and grants nothing.
