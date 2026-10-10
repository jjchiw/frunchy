# Data Model: Operators Managed by a Root Operator

## New table: `operators`

| Column | Type | Rule |
|---|---|---|
| `user_id` | uuid PK | `REFERENCES users(id) ON DELETE CASCADE`: deleting the user removes the grant (FR-012) |
| `added_by` | uuid NULL | `REFERENCES users(id) ON DELETE SET NULL`. The root who granted it; NULL when added from the terminal or when the granting account was deleted |
| `added_at` | timestamptz | `DEFAULT now()` |

**Invariants**:
- The root's username is never in the table: adding it is refused.
- A user is in it at most once (the primary key).

## Roles (computed per request)

| Role | Rule |
|---|---|
| Root | the signed-in username equals `Frunchy:RootOperator` (case-insensitive; empty = none) |
| Operator | root, or `EXISTS (operators where user_id = the user)`, read in the per-request user lookup |

## Security events

| Event | Recorded on | Actor |
|---|---|---|
| `operator_added`, `operator_removed` (web) | the root's account | user |
| the same (web) | the affected user's account | operator ("by the operator") |
| the same (terminal) | the affected user's account | operator |

## Transitions

| Action | From | To | Refused when |
|---|---|---|---|
| add (web or terminal) | not an operator | operator | unknown user, the root, already an operator, bad username |
| remove (web or terminal) | operator | not an operator | unknown user, the root, not an operator, bad username |

## Rebuild statement (constitution I)

Operator grants are owner input, not derived from the raw imports, and are kept like the account
rows they hang off. `user-rebuild` and `mal-rebuild` don't touch them.
