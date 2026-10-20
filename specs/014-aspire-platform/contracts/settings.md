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
- `domain`: default `frunchy.test`;
- `umbral-client-id` and `umbral-client-secret`: optional, from the app registered once in Umbral's admin.
  Declared only when both are set (an undeclared parameter is never prompted for); then Frunchy gets
  `Frunchy:Auth:Providers:Umbral:Enabled|Authority|ClientId|ClientSecret` and waits for `umbral`.

**Configuration** (`AppHost/appsettings.json` or user secrets):
- `Megafono:ImageTag`: the published release, `0.1.0`; empty or `latest` is refused unless building from source;
- `Megafono:BuildFromSource`: default `false`; when true, builds from `Megafono:SourcePath` (default `../../Megafono`
  from the repo root);
- `Umbral:Enabled`: default `true` in `appsettings.json`; `Umbral:ImageTag`: default `0.1.1` (empty or `latest` is
  refused); `Umbral:BuildFromSource`: default `false`; `Umbral:SourcePath`: default `../../Umbral` (source mode only; a
  missing Dockerfile skips Umbral with a warning); `Umbral:Port`: default 5380;
- `Gateway:Port`: default 5300, pinned so the redirect URI `http://localhost:5300/signin-umbral` stays valid.

**Resources**:

| Name | Kind | Endpoints | Waits for |
|---|---|---|---|
| `pg` | Postgres, volume `frunchy-pg` | — | — |
| `frunchydb`, `megafono-db` | databases (`frunchydb`, `megafono`) | — | `pg` |
| `megafono` | container | `federation` 8080 (internal to the network), `internal` 8081 (not external) | `megafono-db` |
| `gateway` | container | `http` 8080 on host port 5300, **the only external endpoint** | `megafono`, `frunchy` |
| `umbral` | container (optional, published image; source build is opt-in), volume `umbral-data` | `http` 8080 on host port 5380 | — |
| `frunchy` | project | `http` | `frunchydb`, and `umbral` once its client credentials are set |

## Self-host compose

- **Profiles**: none gives `postgres` and `frunchy`; `federation` adds `megafono` and `gateway`; `umbral` adds
  Umbral from `ghcr.io/cosmic-chimps/umbral:${UMBRAL_VERSION}` (0.1.1), published on `127.0.0.1:8100`.
- **Published ports**: without a profile, Frunchy's; with `federation`, the gateway's only. 8081 is
  never published.
- **Images**: `ghcr.io/cosmic-chimps/megafono:0.1.0` and `megafono-gateway:0.1.0` (pinned);
  Frunchy is built from `./Dockerfile`.
