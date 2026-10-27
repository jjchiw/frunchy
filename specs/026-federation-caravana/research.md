# Research: Federation on Caravana

Sources: Caravana's session (answers of 2026-10-24, from its code and installer), Megáfono's session (review of the
env list against v0.1.0), Megáfono's `docs/operating.md` ("Behind a TLS proxy (Caravana)") and
`CosmicChimps.Aspire.Hosting.Caravana` 0.1.0's README.

## R1 Forwarded headers and trusted addresses

- **Decision**: the gateway trusts `10.99.0.1` (`Gateway__TrustedProxies`). Megáfono (`Megafono__TrustedProxies`)
  and Frunchy (`Frunchy__TrustedProxies`) trust `Caravana:ContainerNetwork`, default `172.26.64.0/20`.
- **Why**: Traefik runs in host networking and reaches a copy through its host port on `caravana0`, so its requests
  come from `10.99.0.1`. Inside one Caravana app, every service is its own Nomad group; a `names` sidecar writes
  each group's `/etc/hosts` from the app's internal registrations, so `http://megafono:8080` goes container to
  container on Nomad's bridge, and Megáfono sees the gateway copy's own `172.26.64.x` address. That address changes
  on every deploy. Installer-made hosts keep Nomad's default bridge subnet, `172.26.64.0/20`.
- **Accepted risk**: every app's copies share that bridge, so a sibling app's container can reach Megáfono or Frunchy
  directly and set `X-Forwarded-For`: it chooses its own rate-limit bucket and the IP in the logs. No session,
  sign-in or data comes with it. Caravana's documented rule is that apps on one host trust each other; its backlog
  has "Network isolation between apps (Phase 3)", where our ask (a port only the same app's services reach) is
  now noted. Never trust `172.26.64.1`: sibling traffic through a host port arrives masqueraded as it (it is inside
  the range, so this only matters for anyone narrowing the range later).
- **Alternatives**: trusting the proxy by host name (resolve `gateway` per request from the hosts file the sidecar
  keeps current; a sibling can't be `gateway` in our hosts file). Narrower, but Megáfono 0.1.0 only takes addresses
  and ranges, and it is new code in Frunchy's forwarded-headers path; recorded as a follow-up for both.

## R2 Megáfono in Production (v0.1.0)

- **Required**: `Megafono__Domain` (bare host), `ConnectionStrings__Database`, `Megafono__App__BaseUrl` (absolute
  http or https), `Megafono__App__Token`, `Megafono__App__WebhookSecret`, `Megafono__MasterKey` (base64 of 32
  bytes). Nothing else (the User-Agent is built in; `Megafono__Software__Version` defaults to `0.1.0`).
- `Megafono__App__BaseUrl=http://frunchy:8080` is fine in Production: the SSRF guard covers fediverse traffic only;
  calls to the app use a plain client. The only https rule is the actor's `profileUrl`/`iconUrl` (absolute https),
  which Frunchy already sends.
- `Megafono__Federation__AllowPrivateAddresses` stays unset (refused in Production).
- Health: `GET /health` on 8081 (200 when its database answers, 503 otherwise).
- **Gap found**: 0.1.0 writes `discoverable: true` and `indexable: false` on every user actor, whatever Frunchy's
  actor lookup sends (feature 021). Until a Megáfono release passes them on, Members and Private Frunchy users are
  findable in Mastodon's search (their posts are not indexed). Megáfono's session is building it (absent fields keep
  today's values; a change goes out with the next profile Update, which Frunchy's `refresh_profile` already
  triggers), with a gateway `GET /healthz`, for a release it recommends tagging **0.2.0** (main also holds its feed,
  feature 005, whose `post.*` events only fire for accounts a local user follows: nothing Frunchy does yet, so the
  pin is safe). **Done**: 0.2.0 was released on 2026-10-24 (image revision `915221c`, checked: actors take both flags from the
  lookup, and the gateway maps its own `/healthz`); the pin moved to 0.2.0 (and the gateway's check to
  HTTP `/healthz`) when the tag exists, before the federated release. The owner decides whether to release on
  0.1.0 with the gap documented.

## R3 The gateway

- Image `megafono-gateway:0.1.0`, plain http on 8080 without a certificate (it listens on 443 only when
  `Kestrel:Certificates:Default:Path` is set). Clusters: `ReverseProxy__Clusters__megafono__Destinations__primary__Address`
  → Megáfono's federation port, `ReverseProxy__Clusters__app__Destinations__primary__Address` → Frunchy.
- 0.1.0 had no health endpoint of its own (any HTTP path is proxied, to Frunchy by default), so the first build
  used a **TCP** check. 0.2.0's gateway answers `GET /healthz` itself (200 `ok`, never proxied), so the check is
  HTTP `/healthz` on 8080. Before that:
  on 8080. Megáfono offered a gateway `/healthz` in a 0.1.x.

## R4 Caravana

- Moving the domain from `frunchy` to `gateway` in one deploy is a router-tag change; Traefik keeps the certificate
  (its ACME store is per name). Blue/green keeps the old copies serving until the new ones pass their checks; for a
  second or two both routers claim the host. No ordering needed.
- Two Postgres services each with `WithCaravanaBackup(Postgres)` are supported (since Caravana 010): one job per
  service, each dumps its own `POSTGRES_DB` with its own credentials.
- Memory: the app reserves 256+256 MB today; with `megafono` 256/512, `gateway` 64/128 and `megafono-pg` 128/256 it
  reserves 960 MB with ceilings up to 2176 MB. The owner checks the host first (`nomad node status -self`, Reserved
  vs Allocated, or each app's Running tab).
- Every per-resource call (`WithCaravanaDomain`, `WithCaravanaHealthCheck`, `WithCaravanaBackup`,
  `WithCaravanaMemory`) takes any resource, containers included. A service with more than one port (Megáfono)
  names the port of its health check.

## R5 Secrets in publish mode

- Run mode generates and persists the token and webhook secret in user secrets; publish mode can't (the release
  runs on a fresh runner), so they are plain secret parameters read from Bella `frunchy/production`
  (`Parameters__megafono-token`, …), like `db-password`. The publisher moves every environment entry whose value
  contains a secret parameter into Caravana secrets (never in the Compose file or a log).
- Generation for the owner: `openssl rand -base64 32` (master key), `openssl rand -hex 32` (token, webhook secret,
  database password: hex keeps them safe in a connection string).
- The master key is checked before the graph is built (the run graph's `checkMasterKey`), naming the parameter and
  never the value.

## R6 Testing the publish graph

- No Docker or Caravana in tests: build the model only. `publishGraph` becomes callable from a test (public), the
  test creates a builder in publish mode (`DistributedApplication.CreateBuilder` with `--operation publish`
  args), sets the parameters and settings in `builder.Configuration`, calls `publishGraph`, and reads
  `builder.Resources`: names, images and tags, endpoints, environment (evaluated for the publish operation:
  secret entries must be parameter references, never literal values), and the Caravana annotations (domain, health
  check, backup, memory) by whatever the package exposes.
- F# AppHosts get no `Projects.*` classes, so `Aspire.Hosting.Testing`'s `CreateAsync<TEntryPoint>` isn't needed;
  calling the graph function directly is simpler and needs no running app.
