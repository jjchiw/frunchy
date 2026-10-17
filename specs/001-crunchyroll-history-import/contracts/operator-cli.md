# Contract: Operator CLI

**Consumer**: the operator, with shell access to the server. There's no HTTP surface (research
R13).

Invocation: `dotnet run -- <command> [args]`, or the published binary
`FrunchyBackend <command> [args]`. The commands use the same configuration
(`ConnectionStrings:Database`) as the web app, bootstrap the schema if needed, print to stdout,
and exit with 0 on success or non-zero with a message on stderr. With no command, the web app
starts as before.

| Command | Effect | Output |
|---|---|---|
| `user-create <username> <displayName>` | creates the user (username rule `^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$` since feature 013, so it can't end in `-`; lowercased) and issues a sync key | the key, printed **once**, plus the dashboard path |
| `user-regenerate-key <username>` | replaces the key hash; the old key stops working immediately | the new key, printed once |
| `user-reset-binding <username>` | clears `cr_account_id` / `cr_profile_id`; the next accepted submission binds again; history is kept | confirmation |
| `user-rebuild <username>` | deletes the user's watch events and entry issues, sets all their imports back to `pending` (counts zeroed); the running worker, or the next app start, reprocesses them | number of imports queued |
| `user-delete <username> --yes` | deletes the user and everything they own (raw imports included; FR-019). Without `--yes` it refuses | counts of removed imports and watch events |
| `user-list` | lists username, display name, bound (yes/no), number of imports, last sync | table |

Errors: an unknown username exits with code 2; a username that is invalid or taken exits with
code 3.
