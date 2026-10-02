# Data Model: Accounts with Social Sign-In

**Feature**: [spec.md](spec.md) · **Decisions**: [research.md](research.md) · **Date**: 2026-10-02

Changes go in the idempotent bootstrap in `Infrastructure/Database.fs`. Everything from feature 001
stays as it is. `SCHEMA.md` is updated in the same change.

```text
users ──1:n── user_identities      (provider + provider user id; ≥1 once claimed or signed up)
  │──1:n── security_events          (last 20 shown on the account page)
  └──0:1◄── invites.used_by_user_id (the invite that created the account; SET NULL on delete)
```

All personal rows hang off `users` with `ON DELETE CASCADE`, so self-service deletion removes them
too (FR-013). Invites aren't personal data. They keep "used", with `used_by_user_id` set to NULL.

## Changed: `users`

| Column | Change | Why |
|---|---|---|
| `sync_key_hash` | `NOT NULL` → **NULL** (still `UNIQUE`) | a new sign-up has no key until "Connect extension" (FR-010) |
| `sync_key_created_at` | `NOT NULL` → **NULL** | same |

Applied with `ALTER TABLE users ALTER COLUMN … DROP NOT NULL`, which is idempotent. The other
columns don't change: `username` (fixed after creation, FR-010a) and `display_name` (now
editable, 1–60 characters).

## New: `user_identities`

| Column | Type | Rules |
|---|---|---|
| `provider` | text NOT NULL | `google` \| `discord` (and `test` in tests only) |
| `provider_user_id` | text NOT NULL | the provider's stable id (Google `sub`, Discord user id) |
| `user_id` | uuid NOT NULL FK → users ON DELETE CASCADE | |
| `linked_at` | timestamptz NOT NULL default now() | |
| `last_used_at` | timestamptz NOT NULL default now() | updated on each sign-in with it |

- PK `(provider, provider_user_id)`: one provider identity belongs to at most one user (FR-002,
  SC-004).
- Index on `user_id`.
- **Invariant**: unlinking is allowed only while the user has ≥ 2 rows (FR-012). It's enforced in
  the same transaction:
  `DELETE … WHERE … AND (SELECT count(*) FROM user_identities WHERE user_id = @u) > 1`.
- Shape matches ASP.NET Core Identity's user logins (`LoginProvider`, `ProviderKey`), so a later
  move to Identity or to an external broker is a column mapping.

## New: `invites`

| Column | Type | Rules |
|---|---|---|
| `id` | uuid PK | shown by `invite-list`, used by `invite-revoke` |
| `code_hash` | text NOT NULL UNIQUE | SHA-256 of `inv_…`; the code is shown once at creation |
| `created_at` | timestamptz NOT NULL default now() | |
| `expires_at` | timestamptz NOT NULL | `created_at + 7 days` (`--days` overrides) |
| `revoked_at` | timestamptz NULL | |
| `used_at` | timestamptz NULL | |
| `used_by_user_id` | uuid NULL FK → users ON DELETE SET NULL | |

**States** (derived): `active` (not used, not revoked, not expired), `used`, `revoked`, `expired`.
Consumption is one conditional `UPDATE … RETURNING`, in the sign-up transaction (research R4).

## New: `security_events`

| Column | Type | Rules |
|---|---|---|
| `id` | bigint identity PK | |
| `user_id` | uuid NOT NULL FK → users ON DELETE CASCADE | |
| `occurred_at` | timestamptz NOT NULL default now() | |
| `event` | text NOT NULL | one of the types below |
| `provider` | text NULL | when relevant (sign-in, link, unlink, claim, re-confirm) |
| `actor` | text NOT NULL | `user` \| `operator` |

Index on `(user_id, occurred_at DESC)`. There are no IP or device columns (FR-022).

**Event types**:
- `account_created`, `signed_in`, `claimed`, `claim_refused`;
- `identity_linked`, `identity_unlinked`;
- `sync_key_generated`, `binding_reset`, `reconfirmed`, `display_name_changed`.

Each is recorded in the same transaction as its action (SC-013). The operator's
`user-regenerate-key` and `user-reset-binding` record with `actor = operator`. `user-delete` removes
the user, so nothing is recorded.

## Not stored

- **Sessions**: encrypted cookies (research R2), so there's no table. "Account deleted while signed
  in elsewhere" is handled by validating that the user still exists on each request.
- **Pending sign-up**: the short-lived external cookie carries `(provider, provider_user_id)`;
  there's no row until `/signup` succeeds.
- **Email addresses, avatars, provider tokens**: never stored (FR-021). The provider tokens are
  thrown away after the callback.

## Account page queries (signed-in user *u*)

| Section | Query |
|---|---|
| identity | `users` row (username, display name, binding columns, `sync_key_hash IS NOT NULL` as "key active") |
| linked logins | `user_identities WHERE user_id = u ORDER BY linked_at` |
| recent syncs | `cr_imports WHERE user_id = u ORDER BY received_at DESC LIMIT 10` (status + counts) |
| security events | `security_events WHERE user_id = u ORDER BY occurred_at DESC LIMIT 20` |

## Validation rules

- **Username**: the feature 001 rule, plus a reserved list: `admin`, `api`, `account`, `accounts`,
  `login`, `logout`, `signin`, `signout`, `signup`, `claim`, `settings`, `support`, `help`,
  `frunchy`, `u`, `static`, `www`. Checked case-insensitively at sign-up and in `user-create`.
- **Display name**: trimmed, 1–60 characters.
- **Invite code**: `inv_` + 12 Crockford base32 characters, matched by hash; case-insensitive input
  is accepted and normalized.
- **Sync key on claim**: matched by hash only; the same generic error whether the key is unknown or
  the account is already linked.
