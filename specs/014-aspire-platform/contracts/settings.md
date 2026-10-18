# Contract: settings and topology

## Frunchy settings

| Key | Default | Rule |
|---|---|---|
| `Frunchy:Federation:Enabled` | `false` | When true, the four below are required, or startup fails naming the missing key, never a value |
| `Frunchy:Federation:Domain` | — | The public host, no scheme (e.g. `frunchy.test`). Permanent once federating |
| `Frunchy:Megafono:Url` | — | Megáfono's **internal** API base URL (port 8081), never through the gateway |
| `Frunchy:Megafono:Token` | — | Bearer token: sent on `/internal/*` calls, and checked on Megáfono's calls to `/megafono/*` (015) |
| `Frunchy:Megafono:WebhookSecret` | — | HMAC key for `Megafono-Signature` (015) |
| `Frunchy:TrustedProxies` | empty | Comma-separated IPs or CIDRs whose `X-Forwarded-For/-Proto/-Host` are honoured; empty means none |
| `ConnectionStrings:Database` | local dev string | unchanged |

## AppHost

**Parameters** (AppHost user secrets `Parameters:<name>`):
- `megafono-token` and `megafono-webhook-secret`: generated;
- `megafono-master-key`: required, base64 of 32 bytes;
- `domain`: default `frunchy.test`.

**Configuration** (`AppHost/appsettings.json` or user secrets):
- `Megafono:SourcePath`: default `../../Megafono` from the repo root;
- `Megafono:ImageTag`: empty means build from source.

**Resources**:

| Name | Kind | Endpoints | Waits for |
|---|---|---|---|
| `pg` | Postgres, volume `frunchy-pg` | — | — |
| `frunchydb`, `megafono-db` | databases (`frunchydb`, `megafono`) | — | `pg` |
| `megafono` | container | `federation` 8080 (internal to the network), `internal` 8081 (not external) | `megafono-db` |
| `gateway` | container | `http` 8080, **the only external endpoint** | `megafono`, `frunchy` |
| `frunchy` | project | `http` | `frunchydb` |

## Self-host compose

- **Profiles**: none gives `postgres` and `frunchy`; `federation` adds `megafono` and `gateway`.
- **Published ports**: without a profile, Frunchy's; with `federation`, the gateway's only. 8081 is
  never published.
- **Images**: `ghcr.io/cosmic-chimps/megafono:0.1.0` and `megafono-gateway:0.1.0` (pinned);
  Frunchy is built from `./Dockerfile`.
