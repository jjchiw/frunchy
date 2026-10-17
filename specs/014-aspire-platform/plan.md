# Implementation Plan: Aspire Platform Reset

**Branch**: `014-aspire-platform` | **Date**: 2026-10-17 | **Spec**: [aspire-platform.spec.md](aspire-platform.spec.md)

## Summary

- **AppHost**: an F# Aspire AppHost (13.6.1 with `FSharp.Aspire.Hosting.AppHost`) runs Postgres
  (`frunchydb` and `megafono`), Megáfono and its gateway, and Frunchy, with the shared secrets as
  parameters.
- **Frunchy changes**:
  - a federation switch (off by default) with validated Megáfono settings;
  - forwarded headers trusted only from configured proxies;
  - Discord gone;
  - the schema bootstrap rewritten for an empty database, with its equality to the old one proven
    by a test.
- **Self-hosters** get a Dockerfile and a compose file where federation is an optional profile.

## Technical Context

- **Language/Version**: F# on .NET 10. Aspire 13.6.1.
- **Primary Dependencies**:
  - **added**: Aspire.AppHost.Sdk, Aspire.Hosting.PostgreSQL and FSharp.Aspire.Hosting.AppHost
    (AppHost only);
  - **removed**: AspNet.Security.OAuth.Discord.
- **Storage**: PostgreSQL; the schema is equivalent and the bootstrap rewritten.
- **Testing**: xUnit v3 and Testcontainers, unchanged. The AppHost is checked by build and a manual
  run (quickstart), not by the test suite.
- **Target Platform**: macOS for development (Docker), Linux containers for self-hosting.
- **Project Type**: web service plus a dev orchestrator.
- **Constraints**:
  - tests stay independent of Aspire and Megáfono;
  - no secret ever logged;
  - the internal API is never exposed.
- **Scale/Scope**: 1 new project, 1 small infrastructure module, Program.fs wiring, Discord removed
  in about 8 files, the bootstrap rewritten, Dockerfile, compose and docs.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. No data model change. The wipe is the owner's one-time action on dev data, which isn't real history. The code never deletes. |
| II. Boring tech | PASS, noted. Aspire is a dev orchestrator, not a runtime framework: production is a compose file. No broker or queue is added. |
| III. Vertical slices | PASS. No endpoint changes. `Infrastructure/Federation.fs` holds settings only. |
| IV. Ingestion | PASS. Not touched. |
| V. Tests | PASS. Settings validation, forwarded headers, Discord's absence and schema equality are all tested on real Postgres. The AppHost has no automated test (it needs Docker plus the Megáfono checkout); the quickstart's manual run is the check. |
| Constraints | PASS. The schema stays in `initializeSchema` and `SCHEMA.md`; CDN-only frontend. |

## Project Structure

```text
AppHost/Frunchy.AppHost.fsproj, AppHost/AppHost.fs, AppHost/appsettings.json   # NEW
FrunchyBackend.slnx                                                             # + AppHost
Infrastructure/Federation.fs                                                    # NEW: settings + validation
Program.fs                                                                      # federation settings, forwarded headers, no Discord
Infrastructure/AuthConfig.fs, Handlers/Auth/SignIn.fs, Views/Components.fs, FrunchyBackend.fsproj, appsettings.json  # Discord out
Infrastructure/Database.fs, SCHEMA.md                                           # clean bootstrap
Dockerfile, .dockerignore, deploy/compose.yaml, deploy/.env.example, deploy/postgres-init/01-databases.sql   # NEW
tests: FederationSettingsTests.fs, ForwardedHeadersTests.fs, SchemaBootstrapTests.fs (NEW); AuthConfigTests, ClaimTests, FakeProvider (Discord examples swapped)
docs: README.md, CLAUDE.md, FRONTEND.md, design/BRIEF.md, docs/fediverse.md, specs/014 quickstart
```

## Review points (main session)

- **Secrets**: no secret in a log line, an exception message or a committed file. AppHost user
  secrets only.
- **Internal endpoint**: Megáfono's internal endpoint isn't external, isn't a gateway destination,
  and isn't published in compose.
- **Forwarded headers**: honoured only from configured proxies.
- **Schema proof**: the equality test compares the full catalog, not just table names.

## Delegation

- **Agent 1**: Frunchy changes (settings, proxy, Discord, bootstrap) with tests.
- **Agent 2** (after agent 1): AppHost, Dockerfile, compose, docs.
- **Main session**: reviews, runs the AppHost once by hand, and runs the suite.

## Complexity Tracking

| Addition | Why | Simpler alternative rejected because |
|---|---|---|
| .NET Aspire AppHost (dev only) | The owner's direction. One command runs Postgres (2 databases), Megáfono, its gateway and Frunchy with shared secrets wired, which the integration (015) needs every day | Hand-run compose plus `dotnet run` means copying secrets and URLs by hand between 4 processes. Production stays a plain compose file |
