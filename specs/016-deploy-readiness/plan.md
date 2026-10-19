# Implementation Plan: Deploy Readiness

**Branch**: `016-deploy-readiness` | **Date**: 2026-10-19 | **Spec**: [deploy-readiness.spec.md](deploy-readiness.spec.md)

## Summary

Make Frunchy deployable before Caravana's Aspire publisher exists:
- **Data-protection keys** move to a Postgres table, encrypted with AES-256-GCM under a configured key that
  Production requires, so releases and parallel copies keep everyone signed in.
- **`/healthz`** checks the app and its database.
- **Releases** stamp their version into the app.
- **Two GitHub workflows**: tests on every push, and a two-architecture GHCR image on every `v*` tag.
- **The compose file** follows (Postgres 18, no keys volume).

## Technical Context

- **Stack**: F# on .NET 10, Oxpecker, raw Npgsql, Postgres 18, xUnit v3 on Microsoft.Testing.Platform, and
  Testcontainers.
- **New code**:
  - `Infrastructure/DataProtectionStore.fs` (repository, encryptor, decryptor, `register`);
  - `Handlers/Health.fs`;
  - `.github/workflows/ci.yml` and `release.yml`.
- **Changed**: `Program.fs` (registration, startup checks, version log, endpoint), `Infrastructure/Database.fs`
  (table), `Dockerfile`, `deploy/compose.yaml`, `deploy/.env.example`, `PostgresFixture.fs` (test key) and
  `SchemaBootstrapTests.fs`.
- **No new packages**: `AesGcm` and `Microsoft.AspNetCore.DataProtection` are in the shared framework.
- **Performance**: `/healthz` takes 1 s at most; key reads happen only at startup and on refresh.

## Constitution Check

- **Simplicity (no new infrastructure)**: one table and two workflows, with no queue, cache or framework. Pass.
- **Watch history is the core asset**: untouched. The keys belong to the instance (a new "Instance" data class in
  `SCHEMA.md`), not to users. Pass.
- **SQL guardrail**: literal SQL with parameters only. Pass.
- **Tests (V)**: health, key sharing, encryption and the startup refusals are all tested on the real app. Pass.
- **Secrets**: the key stays out of appsettings, logs and errors, and Production can't run without it. Pass.

## Project Structure

```text
.github/workflows/ci.yml                     new
.github/workflows/release.yml                new
Infrastructure/DataProtectionStore.fs        new, after Infrastructure/Database.fs in the fsproj
Infrastructure/Database.fs                   + data_protection_keys
Handlers/Health.fs                           new, first in Handlers
Program.fs                                   register, retire KeysPath, version log, Health.endpoint
Dockerfile                                   BUILDPLATFORM cross-publish, VERSION arg, no /keys
deploy/compose.yaml, deploy/.env.example     Postgres 18, encryption key, no keys volume
tests/FrunchyBackend.Tests/HealthTests.fs                new
tests/FrunchyBackend.Tests/DataProtectionTests.fs        new
tests/FrunchyBackend.Tests/Infrastructure/PostgresFixture.fs   test encryption key
tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs       the new table is an expected difference
SCHEMA.md, README.md, CLAUDE.md, docs/deploy-caravana.md docs
```

## Review points (main session)

1. **The decryptor never sees a key it can't verify**: GCM's tag is checked (`AesGcm.Decrypt` throws on tamper),
   and the associated data is fixed.
2. **No secret reaches a log or exception message**: the key value, the connection string.
3. **The cross-copy test really uses two app instances** on one database, and the first-start race is exercised.
4. **`/healthz` sets no cookie** and never touches Megáfono.
5. **Release** can't push without the tests passing, never writes `latest`, and uses only `GITHUB_TOKEN`.
6. **The Dockerfile's final stage has no `RUN`**, so it builds for arm64 without emulation.

## Delegation

One Sonnet agent, two commits:
1. the app (T001–T008);
2. delivery and docs (T009–T013).

The main session reviews each diff, runs the full suite, builds the image locally for both platforms, and runs the
health and restart checks under the AppHost.

## Complexity Tracking

None.
