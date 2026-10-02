# Contract: Operator CLI additions and changes

Base contract: `specs/001-crunchyroll-history-import/contracts/operator-cli.md`. Invocation and exit
code conventions are unchanged (0 ok, 1 usage, 2 unknown target, 3 invalid input).

## New commands

| Command | Effect | Output |
|---|---|---|
| `invite-create [--days N]` | creates a one-time invite valid for N days (default 7) | the code (`inv_…`), printed **once**, plus its id and expiry |
| `invite-list` | lists invites | id, created, expires, state (`active`/`used`/`revoked`/`expired`), and the used-by username when known |
| `invite-revoke <id>` | revokes an active invite | confirmation; exit 2 for an unknown id, 3 if it isn't active |

## Changed behaviour (same commands, now shared with the web, research R9)

| Command | Change |
|---|---|
| `user-create` | also refuses reserved usernames (data-model.md); the user has no linked identity and can be claimed (US2) |
| `user-regenerate-key` | records a `sync_key_generated` security event with `actor = operator` |
| `user-reset-binding` | records `binding_reset` with `actor = operator` |
| `user-delete --yes` | also removes linked identities and security events (cascade) |
| `user-list` | adds a column: linked providers (e.g. `discord,google` or `-`) |
