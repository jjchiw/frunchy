# Feature Specification: Aspire Platform Reset

**Feature Branch**: `014-aspire-platform`

**Created**: 2026-10-17

**Status**: Draft

**Input**: "Before the Megáfono integration, reset the platform: develop and run Frunchy under .NET Aspire (Postgres with Frunchy's and Megáfono's databases, Megáfono and its gateway in front of one domain), start from a wiped database, remove Discord sign-in, and give self-hosters a compose file where federation is optional. Megáfono's brief of 2026-10-17 settles the topology, its settings and its secrets."

## Clarifications

### Session 2026-10-17 (decided in the autonomous run with the owner's direction and Megáfono's brief; the owner can overturn any of them)

- Q: AppHost language? → A: F#, with `FSharp.Aspire.Hosting.AppHost`, which adds the F# overloads Aspire's C# API needs. Checked by a spike: Aspire 13.6.1, Postgres with two databases, a Dockerfile-built container with two endpoints, parameters, and a project with its connection string all build. Aspire warns that F# is not fully supported, so project paths are written by hand.
- Q: Where does the AppHost live, and how does it get Megáfono? → A: In Frunchy's repository, since Megáfono must not depend on Frunchy. Megáfono and its gateway run as containers:
  - **Until v0.1.1 is published**: built from the local Megáfono checkout's Dockerfiles. The path comes from AppHost configuration, defaulting to the sibling `../../Megafono`.
  - **Afterwards**: from `ghcr.io/cosmic-chimps/megafono` and `megafono-gateway` at a pinned version, never `latest`, when the AppHost setting `Megafono:ImageTag` is set.
- Q: The domain in development? → A: An AppHost parameter, default `frunchy.test`. Nothing federates in 014. Local interop with real servers waits for Megáfono's dev-fediverse kit, which brings TLS with a private CA for `*.test`.
- Q: Federation on or off? → A: A Frunchy setting, `Frunchy:Federation:Enabled`, default **off**.
  - **Under Aspire** it is on, and the Megáfono settings come from the AppHost.
  - **When on**, Frunchy refuses to start without its Megáfono URL, token, webhook secret and domain, as it does today for an enabled sign-in provider without secrets.
  - **When off**, nothing about Megáfono is read: the minimal install is Frunchy plus Postgres.
- Q: The wiped database? → A: The schema bootstrap is rewritten for an empty database:
  - the patches added over features 001–013 (`ALTER TABLE …`) are folded into the table definitions;
  - the pre-001 legacy check goes.

  The owner wipes both databases (`frunchydb` and `megafono`) before first running 014. The code never drops anything.
- Q: Discord? → A: Removed completely: the package, provider, configuration, sign-in button and texts. Google and Umbral stay. Umbral joins the AppHost later, once it has an image.
- Q: Self-hosting? → A: A hand-written `deploy/compose.yaml` with images:
  - Frunchy, built from the repository's new Dockerfile;
  - Megáfono and its gateway, as pinned images;
  - Postgres, with one database each.

  The federation services sit in a `federation` profile, so the default install is Frunchy plus Postgres. `deploy/.env.example` documents every variable.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One command runs everything (Priority: P1)

The developer runs the AppHost (`aspire run`, or `dotnet run` in the AppHost project) and gets:
- Postgres with `frunchydb` and `megafono`;
- Megáfono and its gateway, built from the local checkout;
- Frunchy, with its connection string and Megáfono settings wired in.

The shared secrets come from AppHost parameters, so nothing is copied by hand. The Aspire dashboard shows every resource healthy, and Frunchy answers through the gateway.

**Why this priority**: it is the feature: the platform 015's integration runs on.

**Independent Test**: on a machine with Docker and the Megáfono checkout:
1. Set the master key parameter and run the AppHost.
2. Every resource reaches healthy.
3. The home page loads through the gateway's address.
4. Megáfono's `/health` on its internal endpoint answers 200.
5. Frunchy's logs show federation enabled with every setting present.

**Acceptance Scenarios**:

1. **Given** the AppHost with the master key set, **When** it runs, **Then** Postgres, both databases, Megáfono, the gateway and Frunchy start, and each waits for what it depends on.
2. **Given** the gateway, **When** a browser opens its address, **Then** Frunchy's pages load. **When** `/.well-known/webfinger` is requested, **Then** Megáfono answers.
3. **Given** Megáfono's internal API, **When** the AppHost wires it, **Then** only Frunchy is given its address, and it is never routed through the gateway.
4. **Given** a missing master key parameter, **When** the AppHost runs, **Then** it stops and says which parameter to set.

---

### User Story 2 - Frunchy knows about federation, off by default (Priority: P1)

Frunchy has a federation switch and Megáfono settings, and runs behind the gateway. It trusts forwarded headers only from the configured proxy, so links, sign-in callbacks and secure cookies use the public host and scheme.

**Why this priority**: 015 builds on these settings, and sign-in must keep working behind the gateway.

**Independent Test**:
- with federation off and no Megáfono settings, the app starts and every test passes;
- with federation on and a setting missing, it refuses to start, naming the setting;
- behind a simulated proxy sending `X-Forwarded-Proto: https` and `X-Forwarded-Host`, sign-in redirects use the public URL;
- from an untrusted address, the forwarded headers are ignored.

**Acceptance Scenarios**:

1. **Given** federation off, **When** the app starts without Megáfono settings, **Then** it runs as today.
2. **Given** federation on and any of URL, token, webhook secret or domain missing, **When** the app starts, **Then** it stops with a message naming the missing setting, and no secret value appears in the message.
3. **Given** a request through the trusted proxy with forwarded host and scheme, **When** a sign-in challenge redirects, **Then** the callback URL uses the forwarded host and scheme.
4. **Given** forwarded headers from an address that isn't a trusted proxy, **When** the request is handled, **Then** they are ignored.

---

### User Story 3 - No Discord, a clean database (Priority: P2)

Sign-in offers Google and Umbral only, when they're configured. The schema bootstrap creates the whole current schema on an empty database in one pass.

**Why this priority**: the owner asked for both, and the fresh start is the moment to do them.

**Independent Test**:
- the sign-in page shows no Discord button, and `/signin/discord` is not found;
- the bootstrap on an empty database produces the same tables, columns, constraints and indexes as the old bootstrap did, compared by a catalog query;
- it can run twice without change.

**Acceptance Scenarios**:

1. **Given** any configuration, **When** the sign-in page renders, **Then** no Discord option exists, and Discord configuration keys are ignored.
2. **Given** an empty database, **When** the app starts, **Then** the schema matches the old bootstrap's result exactly, and a second start changes nothing.

---

### User Story 4 - Self-hosting with one file (Priority: P3)

A self-hoster copies `deploy/compose.yaml` and `.env.example`, sets the values, and runs:
- `docker compose up` for Frunchy plus Postgres;
- `docker compose --profile federation up` to add Megáfono and the gateway.

**Why this priority**: the owner wants Frunchy to be self-hostable. It isn't needed for 015 work.

**Independent Test**: `docker compose config` validates both ways; the minimal profile starts and serves the home page.

**Acceptance Scenarios**:

1. **Given** the compose file without a profile, **When** it is started, **Then** Frunchy and Postgres run and the home page answers.
2. **Given** `--profile federation`, **When** it is validated, **Then** the gateway, Megáfono and Frunchy get matching token and webhook secret variables. Megáfono's internal port is published nowhere, and only the gateway's port is.

### Edge Cases

- **Megáfono checkout missing**: the AppHost says where it looked and how to set `Megafono:SourcePath` or `Megafono:ImageTag`.
- **Master key**: it must be base64 of exactly 32 bytes. The AppHost checks it before starting Megáfono.
- **Running Frunchy alone**: `dotnet run` with a local Postgres and federation off still works, so tests, CLI commands and quick runs don't need Aspire.
- **The CLI under Aspire**: operator commands keep working with `dotnet run -- <command>` against the database's connection string. The runbook says how to get it from the dashboard.
- **Bella**: Frunchy still loads Google and Umbral secrets from Bella or user secrets. The Megáfono secrets come from the AppHost.

## Requirements *(mandatory)*

### Functional Requirements

**AppHost**

- **FR-001**: The repository MUST contain an F# Aspire AppHost that runs:
  - Postgres, with databases `frunchydb` and `megafono` and a persistent data volume;
  - Megáfono (two endpoints: federation, and internal);
  - the gateway;
  - Frunchy.

  Each resource waits for what it depends on.
- **FR-002**: Megáfono and the gateway MUST be built from the local Megáfono checkout's `deploy/Dockerfile.megafono` and `deploy/Dockerfile.gateway`, unless `Megafono:ImageTag` is set; then they use the published images at that tag.
- **FR-003**: The AppHost MUST declare the Megáfono token and webhook secret as generated secret parameters, and the master key and domain as parameters the developer sets (the domain defaults to `frunchy.test`). The same token and webhook secret go to both Frunchy and Megáfono.
- **FR-004**: The AppHost MUST configure Megáfono with:
  - its connection string, domain, master key, token and webhook secret;
  - Frunchy's private address as the app base URL;
  - the trusted proxy;
  - the development settings Megáfono's brief lists.

  It MUST configure the gateway's two destinations: Megáfono's federation endpoint and Frunchy.
- **FR-005**: The AppHost MUST give Frunchy:
  - its connection string as `ConnectionStrings:Database`;
  - federation on;
  - Megáfono's internal URL, the token, the webhook secret and the domain;
  - the gateway as its trusted proxy.

  Megáfono's internal endpoint MUST NOT be exposed externally or routed through the gateway.

**Frunchy**

- **FR-006**: Frunchy MUST read `Frunchy:Federation:Enabled` (default false). When it is true, Frunchy MUST require `Frunchy:Megafono:Url`, `Frunchy:Megafono:Token`, `Frunchy:Megafono:WebhookSecret` and `Frunchy:Federation:Domain`, and refuse to start without them, naming what is missing and never printing a secret.
- **FR-007**: Frunchy MUST honour `X-Forwarded-For`, `-Proto` and `-Host` only from the proxies in `Frunchy:TrustedProxies` (addresses or networks). With none configured, forwarded headers are ignored, as today.
- **FR-008**: Discord sign-in MUST be removed: the package, provider registration, configuration key, sign-in button, texts and tests. Google and Umbral are unchanged.
- **FR-009**: The schema bootstrap MUST create the full current schema on an empty database with no `ALTER TABLE` patches and no legacy-layout check. The result MUST be identical in tables, columns, types, defaults, constraints and indexes to what the old bootstrap produced, and running it again MUST change nothing.

**Self-hosting and docs**

- **FR-010**: The repository MUST contain a Dockerfile for Frunchy and a `deploy/compose.yaml` with `deploy/.env.example`:
  - without a profile, Frunchy plus Postgres;
  - with the `federation` profile, Megáfono and its gateway added (pinned images, gateway the only published port, internal API unpublished, the matching secrets).
- **FR-011**: The README and runbook MUST explain:
  - running under Aspire (parameters, the Megáfono checkout, the dashboard, the CLI against the AppHost's database);
  - wiping the old databases before first running 014;
  - self-hosting with compose;
  - that the domain is permanent once federating.

  `CLAUDE.md` MUST describe the new layout.

### Key Entities

- **AppHost**: the development orchestrator and its resources and parameters.
- **Federation settings**: the switch, the domain, and Megáfono's URL, token and webhook secret.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From a clean clone with Docker, the Megáfono checkout and the master key set, one command brings every resource to healthy, and the home page answers through the gateway, all within 5 minutes on the owner's machine, first build included.
- **SC-002**: The full test suite passes without Aspire, Megáfono or any federation setting, at today's speed.
- **SC-003**: Frunchy refuses to start with federation on and a missing setting (each of the 4 checked), and starts with federation off and none of them.
- **SC-004**: The new bootstrap's schema equals the old one's, by a catalog comparison in a test.
- **SC-005**: No reference to Discord remains in code, configuration or user-facing text.
- **SC-006**: `docker compose config` validates the self-host file with and without the federation profile, and the minimal profile serves the home page.

## Assumptions

- Docker is available on the developer's machine. Aspire runs containers through it.
- Megáfono's images get published as v0.1.1; until then the local checkout builds them.
- Nothing federates in 014; the integration is 015.
- The owner wipes the local `frunchydb` and `megafono` databases (and the Aspire Postgres volume if one exists) before first running 014.

## Verbatim Constraints

- Aspire 13.6.1, `FSharp.Aspire.Hosting.AppHost` 13.0.0
- Databases `frunchydb` and `megafono` on one Postgres server
- Settings: `Frunchy:Federation:Enabled`, `Frunchy:Federation:Domain`, `Frunchy:Megafono:Url|Token|WebhookSecret`, `Frunchy:TrustedProxies`
- Megáfono settings and ports per its brief: 8080 federation, 8081 internal, `/health` on 8081
