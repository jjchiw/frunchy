# Feature Specification: Federation on Caravana

**Feature Branch**: `026-federation-caravana`

**Created**: 2026-10-24

**Status**: Implemented (the federated release waits for Megáfono 0.2.0, T011)

**Input**: "I'm ready to test the fediverse on frunchy.lacalabaza.xyz. Keep the domain and start it autonomous."
Today `aspire deploy` publishes only `frunchy` and `pg` to Caravana with federation off (feature 018). This feature
adds Megáfono, its gateway and Megáfono's database to the same Caravana app and turns federation on, so
`@user@frunchy.lacalabaza.xyz` and the series accounts exist on the fediverse.

## Clarifications

### Session 2026-10-24 (decided in the autonomous run; the owner can overturn any of them)

- Q: Which domain? → A: `frunchy.lacalabaza.xyz`, the domain Frunchy already serves, and it is **permanent**: actor
  ids are `https://frunchy.lacalabaza.xyz/ap/actors/{id}` and remote servers cache them with their keys. The owner
  confirmed it. The deploy doc says so before the first federated deploy.
- Q: One Caravana app or two? → A: One, the existing `frunchy` app, with three more services: `gateway`, `megafono`
  and `megafono-pg`. Mastodon needs the profile pages on the actors' domain, so the gateway owns the whole domain
  and splits the paths (Megáfono's routes to Megáfono, the rest to Frunchy), as it does under `aspire run`. Caravana
  confirmed that moving the domain from `frunchy` to `gateway` in one deploy is only a router change (the
  certificate is kept, about a second of mixed routing during the switch).
- Q: Megáfono's database? → A: Its own Postgres 18 service, `megafono-pg`, with its own volume and its own nightly
  backup (03:30 UTC, 14 kept). In Caravana a volume belongs to one service, and a Postgres backup dumps that
  service's `POSTGRES_DB`, so sharing `pg` would leave Megáfono's data out of the backups. Caravana supports two
  Postgres services with a backup each in one app.
- Q: Which Megáfono? → A: The published images `ghcr.io/cosmic-chimps/megafono` and `megafono-gateway` at the tag
  `Megafono:ImageTag` the AppHost already pins (`0.1.0`; `latest` and empty are refused, as in run mode). 0.1.0
  already has the gateway's `Gateway__TrustedProxies` and the `discoverable`/`indexable` actor fields.
- Q: The secrets? → A: Four new secret deploy parameters, kept in Bella `frunchy/production` as
  `Parameters__<name>` like the others: `megafono-master-key` (base64 of 32 bytes, checked before anything is
  built, exactly as in run mode), `megafono-token`, `megafono-webhook-secret` and `megafono-db-password`. They
  can't be generated and persisted by the AppHost in publish mode: the release runs on a fresh GitHub runner.
  **The master key is backed up apart from the database**: losing it loses every fediverse account for good.
- Q: The internal API (port 8081)? → A: Never given a domain and never a gateway destination. Caravana has no private
  network between containers (every container on the host can reach every other one, a known gap in its backlog,
  "Network isolation between apps"), so the bearer token is the only lock: it must be long and random
  (`openssl rand -hex 32`). The deploy doc says so.
- Q: Forwarded headers? → A: Traefik (`10.99.0.1`) → gateway → Megáfono or Frunchy. The gateway trusts only
  `10.99.0.1`. The gateway calls Megáfono and Frunchy by service name, container to container, so they see the
  gateway copy's own bridge address, which changes on every deploy inside Nomad's bridge range (`172.26.64.0/20` on
  installer-made hosts, where every app's copies live). Megáfono and Frunchy therefore trust that range, an AppHost
  setting `Caravana:ContainerNetwork` (default `172.26.64.0/20`). Frunchy no longer trusts `10.99.0.1`: Traefik
  stops reaching it directly. **Accepted risk** (research R1): a sibling app's container on the same host can set
  the client address Frunchy and Megáfono see, so it can pick its own rate-limit bucket and the IP in their logs;
  it gains no sign-in, session or data. That matches Caravana's documented rule (apps on one host trust each
  other). The narrower fixes are follow-ups: trusting a proxy by host name (`gateway`, resolved per request from
  the hosts file Caravana keeps current), in Frunchy and Megáfono, or Caravana's per-app network isolation.
- Q: A way back? → A: An AppHost setting, `Deploy:Federation` (default `true`). `false` publishes the 018 graph
  again (Frunchy owns the domain, federation off, no Megáfono services). Turning it off after federating stops the
  accounts answering; the data stays in `megafono-pg`'s volume. It is for a failed first deploy, not a routine toggle.
- Q: Tests? → A: The AppHost has no tests today. This feature adds an AppHost test project that builds the publish
  model (no Docker, no Caravana) and checks the services, the domain, the health checks, the backups, the
  environment wiring and that no secret is a plain value. A real deploy is the owner's release.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Frunchy's accounts are on the fediverse (Priority: P1)

After the release, someone on Mastodon searches `@<user>@frunchy.lacalabaza.xyz` for a user with a Public profile,
or a series handle, finds it and follows it. Frunchy's pages keep working at the same addresses.

**Independent Test**: after the deploy, `curl "https://frunchy.lacalabaza.xyz/.well-known/webfinger?resource=acct:<user>@frunchy.lacalabaza.xyz"`
answers 200 with the actor, `/healthz` and the home page still answer through the same domain, and a Mastodon
account can follow the user.

**Acceptance Scenarios**:

1. **Given** the release with federation on, **When** Caravana finishes the deploy, **Then** the app `frunchy` has
   five healthy services (`frunchy`, `pg`, `gateway`, `megafono`, `megafono-pg`) and the domain routes to `gateway`.
2. **Given** the deployed app, **When** a browser opens `https://frunchy.lacalabaza.xyz/`, sign-in or a profile,
   **Then** Frunchy answers through the gateway as before (sign-in with Umbral still works: the callback is on the
   same domain).
3. **Given** the deployed app, **When** WebFinger, nodeinfo or an actor id is requested, **Then** Megáfono answers
   through the gateway.
4. **Given** a user whose profile is Members or Private, **When** the actor is fetched, **Then** it is marked not
   discoverable and not indexable (feature 021, unchanged).

---

### User Story 2 - Comments go both ways (Priority: P1)

A signed-in user comments on an episode; the comment is published on the episode's thread on the fediverse. A
Mastodon reply to it shows on the episode page.

**Independent Test**: comment on an episode, run `fediverse-status` in Caravana's console (nothing waiting for
long, nothing failed), see the thread on Mastodon, reply, and see the reply on the episode page.

**Acceptance Scenarios**:

1. **Given** federation on, **When** a comment is written, **Then** the outbox sends it to Megáfono at its internal
   address and the item is done.
2. **Given** a remote reply, **When** Megáfono calls Frunchy's webhook, **Then** Frunchy receives it at its private
   address (not through the domain) and shows it on the episode page.

---

### User Story 3 - The owner can deploy, back up and roll back (Priority: P2)

The owner adds the four new secrets to Bella once, runs the release as usual, and Megáfono's data is in the
nightly backups. If the first federated deploy fails, Caravana keeps the old copies, and `Deploy:Federation=false`
deploys the old shape again.

**Acceptance Scenarios**:

1. **Given** any of the four new parameters is missing, **When** `aspire deploy` runs non-interactively, **Then**
   it stops before deploying and names the parameter (never a value); a master key that isn't base64 of 32 bytes
   is refused the same way.
2. **Given** the deployed app, **When** the backups run, **Then** `pg` is dumped at 03:00 and `megafono-pg` at
   03:30 UTC, 14 kept each.
3. **Given** `Deploy:Federation=false`, **When** `aspire deploy` runs, **Then** the published app is the 018 shape:
   `frunchy` owns the domain, federation is off and the Megáfono parameters aren't asked for.

### Edge Cases

- The domain changes later: not supported (it orphans every follower); the doc says it's permanent.
- Megáfono down: Frunchy keeps serving (its `/healthz` never checks Megáfono, feature 016); comments wait in the
  outbox and are sent when Megáfono is back.
- Gateway down: the whole domain is down, Frunchy included. The gateway gets a health check so Caravana restarts
  it and never promotes a copy that doesn't answer.
- A sibling app on the same host: it can reach port 8081 like any port; the token is the lock (documented risk).
- Memory: the app grows by about 448 MB reserved; the owner checks the host before the release (deploy doc).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: With `Deploy:Federation` true (the default), the publish graph MUST contain `frunchy`, `pg`,
  `gateway`, `megafono` and `megafono-pg`, published as the one Caravana app `frunchy`.
- **FR-002**: The public domain (`frunchy-domain`) MUST be routed to `gateway` and to no other service. Port 8081
  MUST have no domain and MUST NOT be a gateway destination.
- **FR-003**: `gateway` MUST run the pinned `megafono-gateway` image, plain http on 8080, with
  `Gateway__TrustedProxies=10.99.0.1` and its two clusters pointing at `megafono`'s federation port and `frunchy`'s
  http port by service name, and a Caravana health check.
- **FR-004**: `megafono` MUST run the pinned `megafono` image in `Production`, with its database on `megafono-pg`,
  `Megafono__Domain` = the domain, the master key, `Megafono__App__BaseUrl` = Frunchy's private http address, the
  token, the webhook secret and `Megafono__TrustedProxies` = `Caravana:ContainerNetwork` (R1), never
  `AllowPrivateAddresses`, health check `/health` on 8081.
- **FR-005**: `megafono-pg` MUST be Postgres with its own password parameter, its own data volume, `POSTGRES_DB`
  `megafono`, a Postgres backup at 03:30 UTC keeping 14, and a memory limit.
- **FR-006**: `frunchy` MUST get `Frunchy__Federation__Enabled=true`, `Frunchy__Federation__Domain` = the domain,
  `Frunchy__Megafono__Url` = Megáfono's internal address, the token and webhook secret, and
  `Frunchy__TrustedProxies` = `Caravana:ContainerNetwork` only. Every other setting of feature 018 stays.
- **FR-007**: The four new parameters MUST be secret; the master key MUST be checked (base64 of 32 bytes) before
  the graph is built, naming the parameter and never the value.
- **FR-008**: `Deploy:Federation=false` MUST publish exactly the 018 graph and MUST NOT declare the Megáfono
  parameters.
- **FR-009**: The run graph (`aspire run`) MUST NOT change.
- **FR-010**: `docs/deploy-caravana.md`, the README, CLAUDE.md and CHANGELOG (Added and Upgrade) MUST describe the
  new services, the four Bella keys, the permanent domain, the master-key backup, the 8081 risk, the memory check,
  the rollback setting and how to check federation after the deploy.

### Key Entities

- **Deploy parameters**: the 018 ones plus `megafono-master-key`, `megafono-token`, `megafono-webhook-secret`,
  `megafono-db-password` (all secret).
- **AppHost settings**: `Deploy:Federation` and `Caravana:ContainerNetwork` (new), `Megafono:ImageTag` (shared with run mode).

## Success Criteria *(mandatory)*

- **SC-001**: A Mastodon account can find and follow a Public Frunchy user and a series on
  `frunchy.lacalabaza.xyz` after one release.
- **SC-002**: A comment written on Frunchy appears on Mastodon, and a Mastodon reply appears on Frunchy.
- **SC-003**: Frunchy's pages, sign-in and `/healthz` behave as before the release.
- **SC-004**: The AppHost publish-model tests pass and the full suite stays green.
- **SC-005**: Both databases appear in Caravana's backups.

## Assumptions

- Caravana 0.9+ and `CosmicChimps.Aspire.Hosting.Caravana` 0.1.0 (already used) support container resources with a
  domain, a health check, a backup and memory limits (its README: every per-resource call takes any resource).
- The Megáfono images are public on GHCR.
- The release workflow needs no change: `bella run` injects every `Parameters__*` key of `frunchy/production`.
