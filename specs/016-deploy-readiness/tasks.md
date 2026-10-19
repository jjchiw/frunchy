# Tasks: Deploy Readiness

**Input**: [deploy-readiness.spec.md](deploy-readiness.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/operations.md](contracts/operations.md)

**Tests**: required (constitution V), on the real app (TestServer + Postgres Testcontainer).

**Delegation**: one Sonnet agent, two commits (plan). The main session reviews each diff, runs the suite, builds
the image for both platforms and checks a restart under the AppHost.

## Phase 1: The app (US2, US3)

- [ ] T001 Schema: `data_protection_keys` per data-model.md in `initializeSchema`, documented in `SCHEMA.md` (new
  "Instance" data class: not personal, not cascaded, in the backup). Update `SchemaBootstrapTests` so the new
  table is an expected difference and the test still proves the earlier tables unchanged
  · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [ ] T002 `Infrastructure/DataProtectionStore.fs` (research R1–R3), placed after `Database.fs` in the fsproj:
  - `PostgresXmlRepository`;
  - `AesGcmXmlEncryptor` / `AesGcmXmlDecryptor`, with the `DataProtectionEncryptionKey` singleton;
  - `parseKey` (base64 of exactly 32 bytes; errors name `Frunchy:DataProtection:EncryptionKey` and never echo the value);
  - `register environment config services`: `SetApplicationName "frunchy"`, the repository, the encryptor when a
    key is set, and the refusals (invalid anywhere, missing in Production).

  Literal SQL with parameters only (the `SqlGuardrailTests` must pass)
  · Infrastructure/DataProtectionStore.fs, FrunchyBackend.fsproj
- [ ] T003 `Program.fs`:
  - remove the `KeysPath` block from `configureServices`;
  - call `DataProtectionStore.register builder.Environment builder.Configuration builder.Services` in `buildApp`
    after `configureServices`;
  - after `Build()`, log a warning when `Frunchy:DataProtection:KeysPath` is set (retired, ignored);
  - in `main`'s web path, log `Frunchy {Version} starting` from `AssemblyInformationalVersionAttribute` once,
    before `RunAsync`.

  · Program.fs
- [ ] T004 Test key: `PostgresFixture.createAppWith` adds `Frunchy:DataProtection:EncryptionKey` = a fixed
  base64-of-32-bytes test value (a named constant in the fixture), placed **before** `extraConfig` so a test can
  override it with `""`. The whole existing suite must still pass · tests/FrunchyBackend.Tests/Infrastructure/PostgresFixture.fs
- [ ] T005 [P] `DataProtectionTests`:
  - **Restart and copies**: two app instances on the same database. A session cookie from a sign-in on the first
    (`AuthHelpers.signedUp` or the test provider) opens `/account` on the second, and an antiforgery-protected
    form fetched from the first posts successfully on the second.
  - **First-start race**: two fresh apps started concurrently on an empty table, then the same cross-over works
    (the key-not-found refresh, research R1).
  - **Encryption at rest**: with the key set, each stored `xml` holds an `encryptedSecret` and no plain master key
    value.
  - **Production refusals**: no key (`""`), and a key that isn't base64 of 32 bytes. `buildApp` throws a message
    naming `Frunchy:DataProtection:EncryptionKey` that doesn't contain the value.
  - **Non-Production without a key**: the app starts and stores keys (set `builder.Environment.EnvironmentName`
    to `Development` in `customize`).
  - **Tampering**: changing one byte of a stored encrypted key makes it unreadable (the framework skips it or
    creates a new key), never decrypts it wrongly.
  - **Retired setting**: with `KeysPath` set, the app starts and the keys are still in the table.

  · tests/FrunchyBackend.Tests/DataProtectionTests.fs
- [ ] T006 `Handlers/Health.fs` (research R4): `GET /healthz`, mapped in `Program.endpoints` regardless of
  federation. First file in the Handlers section of the fsproj · Handlers/Health.fs, Program.fs, FrunchyBackend.fsproj
- [ ] T007 [P] `HealthTests`:
  - **Up**: 200 `ok` with `text/plain; charset=utf-8` and `Cache-Control: no-store`, and no `Set-Cookie`.
  - **Same answer everywhere**: with Host `anything.example`, `Accept-Language: es`, and cookies of a signed-in user.
  - **Database unreachable**: an app whose connection string points at a closed port answers 503 `unavailable`
    in under 1.5 s.
  - **Federation on**: with `FakeMegafono` scripted to fail every route, still 200.
  - **Other verbs**: `POST /healthz` gets the plain 404.

  · tests/FrunchyBackend.Tests/HealthTests.fs
- [ ] T008 Run the full suite; commit "impl(016): data-protection keys in Postgres, encrypted; /healthz; version log".

## Phase 2: Delivery and docs (US1, US4, US5)

- [ ] T009 `Dockerfile` (research R5, R6):
  - build stage `FROM --platform=$BUILDPLATFORM …sdk:10.0`, with `ARG TARGETARCH` and `ARG VERSION=0.0.0-dev`;
  - restore and publish with `-a $TARGETARCH` and `/p:Version=$VERSION`;
  - final stage with no `RUN`: remove the `/keys` lines, keep `USER $APP_UID`, port 8080 and the entrypoint;
  - update the header comment.

  · Dockerfile
- [ ] T010 [P] `.github/workflows/ci.yml` (research R7) · .github/workflows/ci.yml
- [ ] T011 [P] `.github/workflows/release.yml` (research R7): test job via `workflow_call`, image job with
  outputs `image` and `digest`, written to the job summary too · .github/workflows/release.yml
- [ ] T012 [P] Compose (research R8): Postgres 18 at `/var/lib/postgresql`, no keys volume or `KeysPath`, the
  required encryption key, `FRUNCHY_IMAGE`. Validate with `docker compose … config`, with and without
  `--profile federation`, using `.env.example` · deploy/compose.yaml, deploy/.env.example
- [ ] T013 Docs:
  - **README**: Getting Started gets the health check, the encryption key (Production needs it; back it up apart
    from the database; losing it signs everyone out) and "CI and releases"; the "Settings added" table; the
    roadmap row 016 "Deploy readiness", with private profiles renumbered to **017** (its row and notes).
  - **CLAUDE.md**: Commands and Architecture lines for `/healthz`, the key store and the workflows.
  - **`docs/deploy-caravana.md`**: the sample definition uses `/healthz`, adds the encryption key as a frunchy
    `secrets` entry, and drops the keys volume and data-protection notes that are now done.
  - Quickstart validation log.

  Commit "impl(016): CI and release workflows, multi-arch image, compose on Postgres 18, docs"
  · README.md, CLAUDE.md, docs/deploy-caravana.md, specs/016-deploy-readiness/quickstart.md

## Dependencies

T001 → T002 → T003 → T004 → T005; T006 → T007 (after T004); T008 closes phase 1. Phase 2 after T008: T009, then T010–T012 in parallel, then T013.
