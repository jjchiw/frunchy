# Tasks: Aspire Platform Reset

**Input**: [aspire-platform.spec.md](aspire-platform.spec.md), [plan.md](plan.md), [research.md](research.md),
[contracts/settings.md](contracts/settings.md), [quickstart.md](quickstart.md)

**Tests**: required (constitution V) for every Frunchy change. The AppHost is checked by build and the
quickstart's manual run.

**Delegation**: Sonnet agents implement; the main session reviews every diff, runs the suite and runs the
AppHost by hand.

## Phase 1: Frunchy (US2, US3)

- [x] T001 [US3] `SchemaBootstrapTests`:
  - copy the **current** `initializeSchema` SQL verbatim into the test as `oldBootstrap`;
  - run it on one fresh database and `Database.initializeSchema` on another;
  - compare `information_schema.columns` (table, column, data type, nullability, default), `pg_constraint` (table, type, `pg_get_constraintdef`) and `pg_indexes` (table, `indexdef`), sorted, as string lists;
  - run `initializeSchema` a second time and compare again.

  · tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [x] T002 [US3] Rewrite `initializeSchema` for an empty database: fold every `ALTER TABLE` and `ADD COLUMN IF NOT EXISTS` into the `CREATE TABLE`s, and remove `LegacySchemaException` and its query (research R6). T001 must pass. Update the legacy note in SCHEMA.md and the README reset paragraph · Infrastructure/Database.fs, SCHEMA.md
- [x] T003 [US3] Remove Discord (research R5):
  - the package, the provider registration and its comments;
  - `AuthConfig.known`, `providerBtn`, the sign-in icon and `appsettings.json`;
  - the texts, if any;
  - the tests that use Discord as an example switch to Google or Umbral.

  Add tests: the sign-in page has no Discord option (with every provider enabled in test config), and `/signin/discord` is a 404 · FrunchyBackend.fsproj, Program.fs, Infrastructure/AuthConfig.fs, Handlers/Auth/SignIn.fs, Views/Components.fs, appsettings.json, tests
- [ ] T004 [US2] `FederationSettingsTests`:
  - **off**: no settings needed;
  - **on**: each of the 4 missing keys fails startup with the key named, and the message never contains the secret values;
  - **all set**: it starts, and `FederationSettings` resolves from DI with the values.

  · tests/FrunchyBackend.Tests/FederationSettingsTests.fs
- [ ] T005 [US2] `Infrastructure/Federation.fs` (settings record, read, validate) and its wiring in `configureServices` (research R4), before the providers' check, in the same style · Infrastructure/Federation.fs, Program.fs, FrunchyBackend.fsproj
- [ ] T006 [US2] `ForwardedHeadersTests`, using the TestServer, where the remote address can be set via a test middleware or `HttpContext.Connection` feature:
  - with `Frunchy:TrustedProxies` set and the request from it, `X-Forwarded-Proto: https` and `X-Forwarded-Host: frunchy.test` reach the redirect URI of `/signin/test` (or `Request.Scheme`/`Host` as the app sees them);
  - from another address, they are ignored;
  - with none configured, they are ignored.

  · tests/FrunchyBackend.Tests/ForwardedHeadersTests.fs
- [ ] T007 [US2] Forwarded headers in `configureApp`, before authentication: only when `Frunchy:TrustedProxies` is set, with `KnownProxies` and `KnownNetworks` cleared and filled from it · Program.fs

**Checkpoint**: the full suite is green with no Aspire.

## Phase 2: AppHost and self-hosting (US1, US4)

- [ ] T008 [US1] `AppHost/` (research R1–R3, contract):
  - the project and `appsettings.json`;
  - `AppHost.fs` with the parameters (token and webhook secret generated, the master key required and checked to be 32 bytes, the domain);
  - Megáfono's source path or image tag;
  - the resources, endpoints, environment and waits, with Megáfono's health check on `internal` `/health`.

  Paths come from `__SOURCE_DIRECTORY__`. Add it to `FrunchyBackend.slnx`, and make sure `dotnet test` from the root still doesn't need Docker or the Megáfono checkout to build the tests (keep the AppHost out of the test build if needed) · AppHost/*, FrunchyBackend.slnx
- [ ] T009 [US4] A multi-stage `Dockerfile` (sdk:10.0 → aspnet:10.0, port 8080, non-root user), `.dockerignore`, `deploy/compose.yaml` with profiles, `deploy/.env.example`, and `deploy/postgres-init/01-databases.sql`. Validate with `docker compose config` with and without the profile · Dockerfile, .dockerignore, deploy/*
- [ ] T010 Docs:
  - **README**: running under Aspire, the wipe, self-hosting, and the CLI against the AppHost's database.
  - **CLAUDE.md**: commands, settings, AppHost, no Discord, and the federation switch.
  - **FRONTEND.md and design/BRIEF.md**: Discord mentions removed.
  - **docs/fediverse.md**: platform ready, 015 next.

  · README.md, CLAUDE.md, FRONTEND.md, design/BRIEF.md, docs/fediverse.md
- [ ] T011 Run the full `dotnet test`, the AppHost build, `docker compose config` both ways, and one build plus run of the minimal compose profile (the home page answers), then write the validation log. The AppHost run is done by the main session · specs/014-aspire-platform/quickstart.md

## Dependencies

- **Order**: T001 → T002, T003, T004 → T005, T006 → T007 (Phase 1, one agent), then T008–T011 (Phase 2, one agent).
- **Phase 2** needs Phase 1's settings names.
