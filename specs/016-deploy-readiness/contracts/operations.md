# Contract: health, settings, workflows and image

## `GET /healthz`

| State | Status | Body | Headers |
|---|---|---|---|
| database answers `SELECT 1` within 1 s | 200 | `ok` | `Content-Type: text/plain; charset=utf-8`, `Cache-Control: no-store` |
| anything else (down, timeout, refused) | 503 | `unavailable` | the same |

- Plain http on the app's port (8080 in the image).
- Any Host header, no cookies, no authentication, no redirect, no `Set-Cookie`, no rate limit.
- Never depends on Megáfono, MyAnimeList or a sign-in provider.
- Only `GET`: other verbs get the plain 404 like any unknown route.

## Settings

| Key | Default | Rule |
|---|---|---|
| `Frunchy:DataProtection:EncryptionKey` | — | Base64 of exactly 32 bytes; encrypts the stored keys (AES-256-GCM). **Required in Production**; invalid or missing-in-Production stops startup naming the key, never the value. Keep it in Bella and back it up apart from the database: losing it signs everyone out |
| `Frunchy:DataProtection:KeysPath` | — | **Retired**: when set, a warning is logged and it's ignored; the keys are always in Postgres |

Compose: `FRUNCHY_DATA_PROTECTION_KEY` (required) maps to the encryption key; `FRUNCHY_IMAGE` overrides the image
(default `ghcr.io/jjchiw/frunchy`).

## Workflows

| Workflow | Triggers | Does | Needs |
|---|---|---|---|
| `ci.yml` | push to any branch, pull request, `workflow_call` | `dotnet build` + `dotnet test` from the root | nothing (Docker on the runner) |
| `release.yml` | push of a `v*` tag | `ci.yml`, then the image | `packages: write` on `GITHUB_TOKEN` only |

## Image

- **Name**: `ghcr.io/<repository owner, lowercased>/frunchy:<version>`, the version being the tag without its leading `v`.
- **Platforms**: `linux/amd64` and `linux/arm64`. Never tagged `latest`.
- **Runtime**: listens on 8080 as the unprivileged `app` user, and its startup log names the version.
- **Release outputs**: `image` (the name without tag) and `digest` (`sha256:…`), for the future deploy job (`image@digest`).
