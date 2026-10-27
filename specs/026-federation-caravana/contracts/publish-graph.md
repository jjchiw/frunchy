# Contract: the publish graph (feature 026)

What `publishGraph` must produce. The AppHost tests check every line marked **(test)**.

## Settings and parameters

| Name | Kind | Default | Notes |
|---|---|---|---|
| `Deploy:Federation` | setting (bool) | `true` | `false` = the 018 graph, exactly |
| `Caravana:ContainerNetwork` | setting | `172.26.64.0/20` | Megáfono's and Frunchy's trusted proxies when federating |
| `Megafono:ImageTag` | setting | `0.2.0` | shared with run mode; empty or `latest` refused **(test)** |
| `Megafono:BuildFromSource` | setting | `false` | `true` refused in publish mode with federation on **(test)** |
| `megafono-master-key` | secret parameter | | base64 of 32 bytes, checked before the graph; the error names the parameter, never the value **(test)** |
| `megafono-token`, `megafono-webhook-secret`, `megafono-db-password` | secret parameters | | declared only when federating **(test)** |

The 018 parameters are unchanged.

## Services with `Deploy:Federation=true` (test: exactly these five, no dashboard)

| Service | What | Domain | Health check | Backup | Memory MB |
|---|---|---|---|---|---|
| `frunchy` | the project | none **(test)** | HTTP `/healthz` | | 256/768 |
| `pg` | Postgres, volume `frunchy-pg`, `POSTGRES_DB=frunchydb` | | as today | Postgres `0 3 * * *`, 14 | 256/512 |
| `megafono-pg` | Postgres, password `megafono-db-password`, volume `megafono-pg`, `POSTGRES_DB=megafono` | | TCP | Postgres `30 3 * * *`, 14 **(test)** | 128/256 |
| `megafono` | `ghcr.io/cosmic-chimps/megafono:<tag>`, endpoints `federation` 8080 and `internal` 8081 | none **(test)** | HTTP `/health` on 8081 **(test)** | | 256/512 |
| `gateway` | `ghcr.io/cosmic-chimps/megafono-gateway:<tag>`, one http endpoint 8080 | `frunchy-domain` **(test)** | HTTP `/healthz` on 8080 **(test)** | | 64/128 |

Only `gateway` has a Caravana domain **(test)**.

## Environment (test: each key present; values that come from a secret parameter are parameter references, never literals)

`megafono`:
- `ASPNETCORE_ENVIRONMENT=Production`
- `ConnectionStrings__Database` = `megafono-db`'s connection string
- `Megafono__Domain` = `frunchy-domain`
- `Megafono__MasterKey` = `megafono-master-key`
- `Megafono__App__BaseUrl` = Frunchy's `http` endpoint
- `Megafono__App__Token` = `megafono-token`
- `Megafono__App__WebhookSecret` = `megafono-webhook-secret`
- `Megafono__TrustedProxies` = `Caravana:ContainerNetwork`
- **no** `Megafono__Federation__AllowPrivateAddresses` **(test)**

`gateway`:
- `Gateway__TrustedProxies=10.99.0.1`
- `ReverseProxy__Clusters__megafono__Destinations__primary__Address` = Megáfono's `federation` endpoint
  (never `internal`) **(test)**
- `ReverseProxy__Clusters__app__Destinations__primary__Address` = Frunchy's `http` endpoint

`frunchy` (in addition to every 018 entry, which keep their values):
- `Frunchy__Federation__Enabled=true`
- `Frunchy__Federation__Domain` = `frunchy-domain`
- `Frunchy__Megafono__Url` = Megáfono's `internal` endpoint
- `Frunchy__Megafono__Token` = `megafono-token`
- `Frunchy__Megafono__WebhookSecret` = `megafono-webhook-secret`
- `Frunchy__TrustedProxies` = `Caravana:ContainerNetwork` (not `10.99.0.1`) **(test)**

## `Deploy:Federation=false` (test)

Services `frunchy` and `pg` only; `frunchy` has the domain, `Frunchy__TrustedProxies=10.99.0.1`, no
`Frunchy__Federation__*`; none of the four Megáfono parameters is declared.

## Never

- A secret value as a literal in any environment entry, argument or label.
- A domain on `megafono` or on port 8081.
- `latest` for any image.
