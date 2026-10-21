# Contract: health, settings, workflows and image

## `GET /healthz`

| State | Status | Body | Headers |
|---|---|---|---|
| database answers `SELECT 1` within 1 s | 200 | `ok` | `Content-Type: text/plain; charset=utf-8`, `Cache-Control: no-store` |
| anything else (down, timeout, refused) | 503 | `unavailable` | the same |

- Plain http on the app's port (8080 in the image).
- Any Host header, no cookies, no authentication, no redirect, no `Set-Cookie`, no rate limit.
- Never depends on Megáfono, MyAnimeList or a sign-in provider.
- Only `GET`: other verbs get routing's 405, as on every GET-only route.

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
| `ci.yml` | push to any branch, pull request, `workflow_call` (optional input `ref`: what to check out, empty = the event's own ref) | `dotnet build` + `dotnet test` from the root | nothing (Docker on the runner) |
| `release.yml` | push of a `v*` tag, or `workflow_dispatch` ("Run workflow" on `main`, input `version` X.Y.Z, no `v`) | job `changelog` decides the version and tag (on a dispatch also commits the renamed `CHANGELOG.md` to `main`, tags it and pushes both atomically), then `ci.yml` on the tag, then the image | `contents: write` for `changelog` and `release`, `packages: write` on `GITHUB_TOKEN` only |
| `release.yml`, job `deploy` (feature 018) | the same tag or dispatch, after `ci.yml`; skipped for a pre-release tag (`-` in the name) and while the repository variable `BELLA_BAXTER_PROJECT` is empty | `dotnet aspire deploy` through `bella run` (Bella Baxter keyless): builds and pushes the amd64 image as `<registry>/<owner>/frunchy:<version>` and deploys it to Caravana | environment `production` (tag rule `v*` and branch rule `main`), `packages: write` and `id-token: write`, repository variables `BELLA_BAXTER_TENANT/PROJECT/ENV`, a Bella trust domain; no deploy secret in GitHub |

## Image

- **Name**: `ghcr.io/<repository owner, lowercased>/frunchy:<version>`, the version being the tag without its leading `v`.
- **Platforms**: `linux/amd64` and `linux/arm64`. Never tagged `latest`.
- **Runtime**: listens on 8080 as the unprivileged `app` user, and its startup log names the version.
- **Release outputs**: `image` (the name without tag) and `digest` (`sha256:…`), for the future deploy job (`image@digest`).
