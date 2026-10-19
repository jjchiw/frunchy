# Feature Specification: Deploy Readiness

**Feature Branch**: `016-deploy-readiness`

**Created**: 2026-10-19

**Status**: Draft

**Input**: "Get Frunchy ready to deploy while Caravana's Aspire publisher (PublishToCaravana) is built: tests on every push, a `/healthz` Caravana can check, the data-protection keys in Postgres so a release doesn't sign anyone out, and an image on GHCR for every `v*` tag. The deploy itself waits for the publisher (`docs/deploy-caravana.md`)."

## Clarifications

### Session 2026-10-19 (decided in the autonomous run; the owner can overturn any of them)

- Q: What does `/healthz` check? → A: That the app runs **and** reaches its database (`SELECT 1`, 1 second at most). 200 `ok` or 503 `unavailable`, plain text, never cached. A copy that can't reach Postgres shouldn't take traffic, and Caravana only switches to a new copy once it answers 200. It never touches Megáfono, MyAnimeList or a sign-in provider: one of those being down must not take the site off.
- Q: Where do the data-protection keys live? → A: In Postgres (a `data_protection_keys` table), always, with no file-system option. Caravana's advice: a volume would make Frunchy run one copy and stop before starting, so every release would have a gap; in the database the keys survive releases, need no volume and are in the nightly backup. `Frunchy:DataProtection:KeysPath` is retired: setting it logs a warning and does nothing.
- Q: The keys in a backup could forge anyone's session. Encrypt them? → A: Yes, with a key from configuration: `Frunchy:DataProtection:EncryptionKey`, the base64 of 32 bytes (AES-256-GCM), kept in Bella like Megáfono's master key. **Required in the Production environment**: startup fails naming the key, never its value. Outside Production it's optional, and without it the keys are stored as the framework stores them by default (plain XML) with its warning. Development, the AppHost and the tests run without it.
- Q: Losing the encryption key? → A: The keys can't be read, so every session and form token becomes invalid: everyone signs in again. Nothing else is lost (no user data is encrypted with it). The README says to back it up apart from the database.
- Q: Which images, and where? → A: A `v*` tag builds `linux/amd64` and `linux/arm64` and pushes `ghcr.io/<repository owner>/frunchy:<version>` (the tag without its `v`), after the tests pass. Never `latest`, like Megáfono's images: deployments pin a version or a digest. The workflow reports the image and its digest as outputs, so the deploy job can be added later without changing it.
- Q: Does the running app know its version? → A: Yes. The release build stamps the tag's version into the assembly, and the startup log line names it. Pages don't show it.
- Q: Which tests run in CI? → A: All of them (`dotnet test` from the root), on every push and pull request, with Docker for Testcontainers. The design-token parity test skips itself without the extension checked out next to the repo, as it already does. The opt-in MyAnimeList recording test stays off.
- Q: The self-host compose file? → A: It follows: Postgres 18 with the volume at `/var/lib/postgresql` (Postgres 18's layout), no keys volume, and the encryption key as a required variable. Nobody has deployed it yet, so the Postgres major version can change without an upgrade path.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Every push is tested (Priority: P1)

The owner pushes a branch or opens a pull request, and GitHub builds the whole solution and runs every test, so a broken build or a failing test shows before anything is merged or released.

**Why this priority**: nothing runs automatically today; every later step (images, deploys) needs a green build first.

**Independent Test**: push a branch: the workflow builds, runs the suite against a Postgres Testcontainer and reports the result on the commit.

**Acceptance Scenarios**:

1. **Given** a push to any branch or a pull request, **When** the workflow runs, **Then** it builds the solution (the app, the tests and the AppHost) and runs the full test suite, and fails if either fails.
2. **Given** two pushes to the same branch in a row, **When** the second starts, **Then** the first run is cancelled.
3. **Given** the extension isn't checked out next to the repo, **When** the suite runs, **Then** the design-token parity test is skipped, not failed.

---

### User Story 2 - A health check Caravana can call (Priority: P1)

Caravana (and any orchestrator) asks `GET /healthz` on the container's own address, over plain http on port 8080, with no domain in the Host header. It gets 200 while Frunchy can serve pages, and 503 while it can't reach its database.

**Why this priority**: Caravana only switches traffic to a new copy that answers its health check; without one, a deploy can't be checked.

**Independent Test**: on the test server, `GET /healthz` answers 200 `ok`; with the database unreachable it answers 503 `unavailable`, within about a second.

**Acceptance Scenarios**:

1. **Given** the app and its database are up, **When** `GET /healthz` is called, **Then** it answers 200 with the body `ok`, `text/plain`, and `Cache-Control: no-store`.
2. **Given** the database is down or slower than 1 second, **When** `GET /healthz` is called, **Then** it answers 503 with the body `unavailable`, and the error is logged without the connection string.
3. **Given** federation is on and Megáfono is down, or MyAnimeList is down, **When** `GET /healthz` is called, **Then** it still answers 200.
4. **Given** any Host header, no cookies and any `Accept-Language`, **When** `GET /healthz` is called, **Then** the answer is the same: no redirect, no session, no localization, no rate limit.

---

### User Story 3 - A release doesn't sign anyone out (Priority: P1)

Sessions, sign-in round trips and form tokens are protected by ASP.NET's data-protection keys. They now live in Postgres, encrypted with a key from configuration, so a new copy of Frunchy reads the same keys as the old one: people stay signed in across releases and restarts, and several copies can run side by side.

**Why this priority**: today the keys sit in the container (or a volume in compose), so every Caravana release would sign everyone out, or force a stop-then-start deploy with a gap.

**Independent Test**: start two app instances on the same database: a session cookie and an antiforgery token issued by the first are accepted by the second. With the encryption key set, the stored key XML holds no plain key material.

**Acceptance Scenarios**:

1. **Given** a signed-in browser, **When** Frunchy restarts or a new copy takes over on the same database, **Then** the browser is still signed in and an open form still submits.
2. **Given** two copies on the same database, **When** they start together for the first time, **Then** they end up using the same key ring (one key, not one each that rejects the other's cookies).
3. **Given** `Frunchy:DataProtection:EncryptionKey` is set, **When** a key is stored, **Then** its key material is encrypted (AES-256-GCM) and readable only with that key.
4. **Given** the Production environment and no encryption key, or one that isn't base64 of 32 bytes, **When** Frunchy starts, **Then** it refuses to start with a message naming `Frunchy:DataProtection:EncryptionKey`, never a value.
5. **Given** `Frunchy:DataProtection:KeysPath` is set, **When** Frunchy starts, **Then** it logs a warning that the setting is retired and keeps the keys in Postgres.
6. **Given** a user is deleted, **When** their rows cascade, **Then** the keys are untouched (they belong to the instance, not to a user).

---

### User Story 4 - A tag publishes an image (Priority: P2)

The owner pushes a tag `v0.1.0`. After the tests pass, GitHub builds Frunchy for amd64 and arm64 and pushes `ghcr.io/<owner>/frunchy:0.1.0`. The workflow's outputs carry the image name and digest for the deploy job that comes with PublishToCaravana.

**Why this priority**: a deploy needs an image; it's cheap to have it ready, but nothing consumes it until the deploy exists.

**Independent Test**: push a `v*` tag in a fork or a test repository: the package appears in GHCR with that version for both architectures, and the startup log of the image names the version.

**Acceptance Scenarios**:

1. **Given** a pushed tag `v1.2.3`, **When** the release workflow runs, **Then** it runs the tests first and, only if they pass, pushes `ghcr.io/<owner>/frunchy:1.2.3` for `linux/amd64` and `linux/arm64`.
2. **Given** any tag, **When** the image is pushed, **Then** no `latest` tag is written.
3. **Given** the image runs, **When** it starts, **Then** its startup log line names version `1.2.3`.
4. **Given** a push without a tag, **When** workflows run, **Then** no image is pushed.

---

### User Story 5 - The self-host compose file follows (Priority: P3)

`deploy/compose.yaml` runs the same shape as the coming Caravana deploy: Postgres 18, no keys volume, the encryption key required.

**Why this priority**: compose is the documented self-host path and must not drift from what the app now expects.

**Independent Test**: `docker compose config` validates with the example environment, with and without the federation profile.

**Acceptance Scenarios**:

1. **Given** `deploy/.env.example`, **When** the compose file is validated, **Then** it passes, and Postgres is version 18 with its volume at `/var/lib/postgresql`.
2. **Given** no `FRUNCHY_DATA_PROTECTION_KEY`, **When** compose is started, **Then** it refuses, naming the variable (as it does for `POSTGRES_PASSWORD`).

### Edge Cases

- The database is reachable but `initializeSchema` hasn't run yet: the app runs the schema before it listens, so `/healthz` can't answer before the table exists.
- Two copies create the first key at the same moment: the second must not fail; the store keeps both rows and the framework picks the newest active key, and both copies read the same ring on their next refresh. The test pins that cookies cross over after a refresh.
- The encryption key changes: stored keys can't be decrypted; the framework creates a new key and everyone signs in again. This is documented, not prevented.
- A key row is corrupted or unreadable: the framework logs and skips it, as it does for files.
- The test suite creates hundreds of apps on fresh databases: each gets its own key ring in its own database, no shared state.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: A GitHub Actions workflow MUST build the solution and run the whole test suite on every push and pull request, cancel superseded runs on the same ref, and need no repository secrets.
- **FR-002**: `GET /healthz` MUST answer 200 `ok` when the database answers `SELECT 1` within 1 second, and 503 `unavailable` otherwise; `text/plain; charset=utf-8`, `Cache-Control: no-store`; no redirect, authentication, session, antiforgery, localization or rate limit; independent of Megáfono, MyAnimeList and sign-in providers.
- **FR-003**: Data-protection keys MUST be stored in a Postgres table created by `initializeSchema`, and read from it by every copy of the app on that database; the application name MUST be fixed (`frunchy`) so keys don't depend on the content path.
- **FR-004**: When `Frunchy:DataProtection:EncryptionKey` is set (base64 of 32 bytes), stored keys MUST be encrypted with AES-256-GCM under it; a value that isn't base64 of 32 bytes MUST stop startup naming the key.
- **FR-005**: In the Production environment, a missing `Frunchy:DataProtection:EncryptionKey` MUST stop startup naming the key; elsewhere it's optional.
- **FR-006**: `Frunchy:DataProtection:KeysPath` MUST be retired: when set, log a warning and ignore it.
- **FR-007**: A workflow MUST, on a pushed `v*` tag and only after the tests pass, build and push `ghcr.io/<repository owner, lowercased>/frunchy:<version>` for `linux/amd64` and `linux/arm64`, never tagging `latest`, and expose the image name and digest as outputs. It MUST use only the workflow's own token (`packages: write`).
- **FR-008**: The release build MUST stamp the tag's version into the app, and the app MUST log its version once at startup.
- **FR-009**: `deploy/compose.yaml` MUST use Postgres 18 with its volume at `/var/lib/postgresql`, drop the keys volume, and require `FRUNCHY_DATA_PROTECTION_KEY`; `deploy/.env.example` MUST document it with its generation command.
- **FR-010**: Secrets (the encryption key, connection strings) MUST NOT appear in logs, error messages or workflow output.
- **FR-011**: The README, CLAUDE.md, SCHEMA.md and `docs/deploy-caravana.md` MUST describe the health check, the key storage and encryption key, the workflows and the image.

### Key Entities

- **Data-protection key**: one row per key the framework creates: a name (the framework's friendly name, unique), the key's XML (encrypted when the encryption key is set) and when it was stored. It belongs to the instance, not to a user.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every push gets a pass or fail on GitHub without anyone running the tests by hand.
- **SC-002**: `/healthz` answers in under 1.5 seconds in both states, and 503 whenever the database can't be reached.
- **SC-003**: A user signed in before a restart or a new copy is still signed in after it, with zero sign-outs caused by a release.
- **SC-004**: A `v*` tag produces a pinned two-architecture image without any secret stored in GitHub.
- **SC-005**: The existing test suite still passes unchanged apart from tests that pinned the retired file-system keys.

## Assumptions

- The GitHub repository is `jjchiw/frunchy` today; the image name follows the repository owner, so a move to an organization changes it without editing the workflow. The compose file's default image follows the owner too.
- The package's visibility (public, or registry credentials on Caravana) is the owner's decision when the deploy happens; this feature only pushes.
- `ubuntu-latest` runners have Docker, which the Testcontainers suite needs.
- The deploy job, the Caravana definition and PublishToCaravana are out of scope (`docs/deploy-caravana.md`).

## Verbatim Constraints

- "Frunchy has no `.github/` yet: no CI, no image, no release."
- Caravana's health check "must answer 200 over plain HTTP on 8080 to Nomad's check, which calls the copy's address directly (no Host header of your domain, no HTTPS)".
- "Better: keep the keys in Postgres … Then frunchy has no volume, deploys blue/green with no gap, and the keys are in the database backup."
