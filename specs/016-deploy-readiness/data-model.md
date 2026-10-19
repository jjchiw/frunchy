# Data Model: Deploy Readiness

## `data_protection_keys` (new)

ASP.NET Core data-protection keys for the whole instance. Not personal data: no user reference, untouched by
`user-delete`, included in the nightly backup.

| Column | Type | Rule |
|---|---|---|
| `id` | `bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY` | insertion order, read in this order |
| `friendly_name` | `text NOT NULL UNIQUE` | the framework's name for the key (`key-<guid>`); a second store of the same name is ignored |
| `xml` | `text NOT NULL` | the key's XML; its secret part is an `encryptedSecret` (AES-256-GCM) when `Frunchy:DataProtection:EncryptionKey` is set |
| `created_at` | `timestamptz NOT NULL DEFAULT now()` | when it was stored |

- Rows are only inserted, never updated or deleted by the app (expired keys stay, as the framework expects, so old
  payloads can still be read until they expire).
- Created in `initializeSchema` (idempotent `CREATE TABLE IF NOT EXISTS`) and documented in `SCHEMA.md` under a new
  "Instance" data class.
