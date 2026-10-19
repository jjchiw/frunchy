# Data Model: Private Profiles

## `profile_settings` (new)

One row per user who chose a visibility or created a share link. No row means the instance default applies and
there is no link.

| Column | Type | Rule |
|---|---|---|
| `user_id` | `uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE` | the owner |
| `visibility` | `text NULL CHECK (visibility IN ('public','private'))` | the owner's choice; `NULL` = follow `Frunchy:Profiles:DefaultVisibility` |
| `share_token_hash` | `text NULL UNIQUE` | lowercase-hex SHA-256 of the share token; never the token |
| `share_link_id` | `uuid NULL` | changes with every new link; grants name it |
| `share_link_created_at` | `timestamptz NULL` | |
| `updated_at` | `timestamptz NOT NULL DEFAULT now()` | |

- `CHECK ((share_token_hash IS NULL) = (share_link_id IS NULL) AND (share_link_id IS NULL) = (share_link_created_at IS NULL))`:
  the link columns are all set or all empty.
- Personal data (the visibility is a user setting): deleted with the user. `SCHEMA.md` lists it under Accounts.

## Grant (cookie only, never stored)

`frunchy.shares` = data-protected JSON `[{ "u": uuid, "l": uuid, "at": unix seconds }]`, at most 20 entries, 30 days.

## Security events (new types, no schema change)

`profile_visibility_changed` (detail: `public` or `private`), `share_link_created`, `share_link_replaced`,
`share_link_revoked`, all with `Actor.User`.
