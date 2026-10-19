# Releasing to Caravana with Bella Baxter (backlog)

How Umbral deploys a release to Caravana, shared by the Umbral session on 2026-10-19 so Frunchy can do the
same. It is live in Umbral: `.github/workflows/release.yml` (the `deploy` job) and `deploy/caravana-app.json` in
https://github.com/Cosmic-Chimps/umbral. Frunchy has the first half since feature 016: `.github/workflows/ci.yml` (tests on every push) and `release.yml` (on a `v*` tag, the tests, then a two-architecture image on GHCR with `image` and `digest` outputs). The deploy job below is not added yet.

## The flow

On a pushed `v*` tag: test, then build and push a multi-arch image to GHCR, then deploy:
1. The job's GitHub OIDC token is exchanged for a short-lived Bella key.
2. `bella run` reads the Caravana settings and writes them to step outputs, with the token masked.
3. `Cosmic-Chimps/caravana-action` deploys the exact image digest, defines the app on its first deploy and waits
   until it is healthy.

No deploy secret is stored in GitHub.

## Setup

**Bella Baxter**: a project and environment for the workflow (Umbral uses `umbral-workflow` / `production`).
- Secrets:
  - `CARAVANA_URL`: the console URL, `https://…`, no trailing slash.
  - `CARAVANA_TOKEN`: a deploy token scoped to the one app (Caravana 0.8.1+: Settings › API tokens › "Only for
    one app"). It can only deploy, define, promote and read that app.
  - `CARAVANA_DOMAIN`: the app's own public domain, never the console's (pitfall B).
  - Optional: `CARAVANA_APP_ID` (defaults to the app name) and `CARAVANA_BACKUP_SCHEDULE` (cron).
- Identity › Add trust domain › GitHub template:
  - issuer `https://token.actions.githubusercontent.com`, role Consumer, TTL 15, audiences empty;
  - claim rules: `repository_id` Equals the numeric repo id (`gh api repos/<owner>/<repo> --jq .id`) and
    `environment` Equals `production`.
- The tenant enforces ZKE: keyless needs the CLI/action `0.1.1-preview.137` or later.

**GitHub**:
- Repository variables (not environment variables: a job-level `if:` can't see those): `BELLA_BAXTER_TENANT`,
  `BELLA_BAXTER_PROJECT`, `BELLA_BAXTER_ENV`.
- Environment `production` › Deployment branches and tags › Selected › tag rule `v*`. With the trust domain's
  `environment` rule, only tag releases can read the secrets.

## The deploy job (Umbral's)

`IMAGE` and `DIGEST` come from the image job's outputs (build-push-action's digest and the lowercased
`ghcr.io/<owner>/<name>`). Use `concurrency: release` on the workflow.

```yaml
  deploy:
    needs: image
    if: ${{ !contains(github.ref_name, '-') && vars.BELLA_BAXTER_PROJECT != '' }}
    runs-on: ubuntu-latest
    environment: production
    permissions:
      contents: read
      id-token: write
    env:
      BELLA_BAXTER_URL: https://api.bella-baxter.io        # the API, not the console
      BELLA_BAXTER_TENANT: ${{ vars.BELLA_BAXTER_TENANT }}
      BELLA_BAXTER_PROJECT: ${{ vars.BELLA_BAXTER_PROJECT }}
      BELLA_BAXTER_ENV: ${{ vars.BELLA_BAXTER_ENV || 'production' }}
      IMAGE: ${{ needs.image.outputs.image }}
      DIGEST: ${{ needs.image.outputs.digest }}
    steps:
      - uses: actions/checkout@v4
      - uses: cosmic-chimps/bella-baxter-setup-action@cf993ff9e477ec89370abb7f166a4e48a2786693 # v0.1.1-preview.137
        with:
          version: 0.1.1-preview.137
          oidc: 'true'
      - name: Settings from Bella Baxter
        id: caravana
        run: |
          bella run -- bash -c '
            set -euo pipefail
            if [ -n "${CARAVANA_TOKEN:-}" ]; then echo "::add-mask::$CARAVANA_TOKEN"; fi   # Bella does not mask values
            [ -n "${CARAVANA_URL:-}" ] && [ -n "${CARAVANA_TOKEN:-}" ] || { echo "::error::CARAVANA_URL and CARAVANA_TOKEN are required."; exit 1; }
            definition=""
            if [ -n "${CARAVANA_DOMAIN:-}" ]; then
              definition="$RUNNER_TEMP/app.json"
              jq --arg image "$IMAGE@$DIGEST" --arg domain "$CARAVANA_DOMAIN" \
                 "walk(if type == \"string\" then (split(\"__IMAGE__\")|join(\$image)) | (split(\"__DOMAIN__\")|join(\$domain)) else . end)" \
                 deploy/caravana-app.json > "$definition"
            fi
            { echo "url=$CARAVANA_URL"; echo "token=$CARAVANA_TOKEN"
              echo "app=${CARAVANA_APP_ID:-frunchy}"; echo "definition=$definition"; } >> "$GITHUB_OUTPUT"'
      - name: Deploy on Caravana
        uses: Cosmic-Chimps/caravana-action@e05c7a3e11c0f33a7d268d6ea32eaae70d12516d # v1.1.0
        with:
          url: ${{ steps.caravana.outputs.url }}
          token: ${{ steps.caravana.outputs.token }}
          app: ${{ steps.caravana.outputs.app }}
          image: ${{ needs.image.outputs.image }}@${{ needs.image.outputs.digest }}
          definition: ${{ steps.caravana.outputs.definition }}
```

- `bella run` injects secrets only into its own child process, hence the step outputs; mask the token first.
- Don't pass `-p`/`-e` to `bella run`: the key already carries the project and environment.

## The app definition

`deploy/caravana-app.json` is sent only while the app is undefined; afterwards the action replaces only the image
and keeps the edits made in the console. Umbral's, as a model:

```json
{"source":{"kind":"image"},"services":[{"name":"umbral","image":"__IMAGE__","ports":[{"name":"http","containerPort":8080}],"health":{"path":"/healthz"},"domains":[{"host":"__DOMAIN__","port":"http"}],"volumes":[{"name":"umbral-data","path":"/data"}],"env":[{"name":"UMBRAL__PUBLIC_ORIGIN","value":"https://__DOMAIN__"},{"name":"UMBRAL__TRUSTED_PROXIES","value":"10.99.0.1"}]}]}
```

Check field names against Caravana's `api/openapi.yaml` (App/Service): an unknown field is a 400. `10.99.0.1` is the
address Traefik connects from on installer-made hosts.

## Pitfalls Umbral hit

- **A. Private GHCR package**: Caravana couldn't pull it and the rollout died at the 10-minute deadline. Make the
  package public (free, irreversible) or add ghcr.io credentials (`read:packages`) in Caravana › Registries.
- **B. The console's domain as `CARAVANA_DOMAIN`**: the app's router collided with the console's (502s on the
  console during the rollout). Use the app's own domain. Once the app is defined, change its domain in Caravana ›
  Configure, not in Bella.
- **C. Deploy by digest** on purpose, so each release runs exactly what CI built.
- **D. Webhook-only releases** should store a tag, never a digest. Doesn't apply with caravana-action.

## Several services in one app (the caravana session, 2026-10-19)

Checked against Caravana's `internal/app` model and `api/openapi.yaml` (API 0.19/0.20):
- **One app, one service per container**: one Nomad job, one task group per service. Siblings reach each other by
  service name (`postgres:5432`), within the same app only. Caravana has no managed database: Postgres is a service
  with a volume. A service with a volume runs one copy and deploys stop-then-start. A new Frunchy image redeploys
  only Frunchy's group; the volume (`frunchy_pgdata`) survives deploys and goes only with the app.
- **Internal services**: no `domains` means no public route. Every service with `ports` needs a `health`;
  without `path` it is a TCP check (Postgres). With federation, only `megafono-gateway` gets the domain.
- **Backups**: `"backup": {"engine": "postgres", "schedule": "0 3 * * *", "keep": 14}` on the Postgres service
  (cron in UTC; `pg_dump` uploaded to the installation's backup destination, which must be set first in
  Settings › Backup destination or the deploy is refused). It dumps only `POSTGRES_DB`. Once Megáfono's
  database joins the same server, it needs a second Postgres service, or `"engine": "volume"` (the whole data
  directory, stopping Postgres a few seconds per backup).
- **Secrets**: each service declares its own `secrets` (`{name, value}`), with no references between services and
  nothing generated by Caravana. Generate the database password once, keep it in Bella, inject it with jq into the
  first definition. Later deploys keep a stored secret sent with a name and no value.
- **The action**: with several services it needs `service: frunchy`, and replaces only that service's image;
  Postgres keeps its stored, digest-pinned image.
- **Postgres 18** keeps its data under `/var/lib/postgresql` (versioned subdirectories): mount the volume there,
  not at `…/data` (`deploy/compose.yaml` does the same since feature 016).
- Default memory is 256 MB reserved and 1024 MB max per service.

Sample `deploy/caravana-app.json`, adapted to Frunchy's real keys (the caravana sample used `ConnectionStrings__Default`;
Frunchy reads `ConnectionStrings__Database`). `__IMAGE__`, `__DOMAIN__`, `__DB_PASSWORD__` and `__DATA_PROTECTION_KEY__` (base64 of 32 bytes, kept in Bella and backed up apart from the database) are filled by jq in CI:

```json
{
  "source": {"kind": "image"},
  "services": [
    {
      "name": "frunchy",
      "image": "__IMAGE__",
      "ports": [{"name": "http", "containerPort": 8080}],
      "domains": [{"host": "__DOMAIN__", "port": "http"}],
      "health": {"path": "/healthz", "interval": "10s", "timeout": "2s"},
      "env": [{"name": "Frunchy__TrustedProxies", "value": "10.99.0.1"}],
      "secrets": [
        {"name": "ConnectionStrings__Database", "value": "Host=postgres;Port=5432;Database=frunchydb;Username=frunchy;Password=__DB_PASSWORD__"},
        {"name": "Frunchy__DataProtection__EncryptionKey", "value": "__DATA_PROTECTION_KEY__"}
      ],
      "resources": {"memoryMB": 256, "memoryMaxMB": 768}
    },
    {
      "name": "postgres",
      "image": "postgres:18",
      "ports": [{"name": "pg", "containerPort": 5432}],
      "health": {"interval": "10s", "timeout": "2s"},
      "env": [{"name": "POSTGRES_USER", "value": "frunchy"}, {"name": "POSTGRES_DB", "value": "frunchydb"}],
      "secrets": [{"name": "POSTGRES_PASSWORD", "value": "__DB_PASSWORD__"}],
      "volumes": [{"name": "pgdata", "path": "/var/lib/postgresql"}],
      "backup": {"engine": "postgres", "schedule": "0 3 * * *", "keep": 14},
      "resources": {"memoryMB": 256, "memoryMaxMB": 512}
    }
  ]
}
```

## PublishToCaravana (the caravana session, 2026-10-19)

- **Planned, not built**: Caravana's feature 026 (Phase 2.75), modeled on CosmicChimps' `PublishToDokploy`, after
  Phase 2.6 freezes the API at 1.0. The publisher is C# in CosmicChimps.Aspire; Caravana adds Aspire-style service
  discovery env (`services__<name>__<endpoint>__0`) and fixtures of real Aspire compose output. The maintainer
  decides when.
- **Until then**: the hand-written `deploy/caravana-app.json` plus caravana-action. Moving to `aspire deploy` later
  replaces that file and nothing else. The AppHost would also need production settings apart from its dev ones
  (Umbral from source with the fake connector, Megáfono's private-address allowance, plain http).
- **Keyless**: Caravana accepts only its own tokens (`cvat_…`). GitHub OIDC → Bella → the Caravana deploy token is the
  path that works today. Caravana trusting GitHub OIDC directly isn't planned (suggested to the maintainer).

Notes on Frunchy's services:
- **Data protection keys in Postgres, not on a volume** (done in feature 016): a service with a volume runs one
  copy and stops before it starts, so every release would have a gap. With the keys in the database (table
  `data_protection_keys`, an `IXmlRepository` over Npgsql), frunchy has no volume, deploys blue/green and the keys
  are in the nightly dump. They are encrypted with `Frunchy__DataProtection__EncryptionKey`, which Production
  requires: a `secrets` entry on frunchy, kept in Bella and backed up apart from the dump (the dump alone can't
  forge a session; losing the key signs everyone out). `Frunchy:DataProtection:KeysPath` is retired.
- **Bella at runtime**: `BELLA_BAXTER_API_KEY` (or the access token and private key) as a `secrets` entry on
  frunchy, never `env`. Caravana's own keyless Bella provider (its 027) is planned, not built.
- **`/healthz`** (done in feature 016): 200 over plain http on 8080, called on the copy's address with no Host
  header of the domain and no https. It checks the database (1 second) and nothing else, so Megáfono being down
  doesn't take the site off.
- **Later, federation**: megafono has two ports and no domain, so its `health` must name the port; `Megafono__MasterKey`
  is a secret whose source copy stays in Bella (Caravana's backups don't include secrets); moving the domain from
  frunchy to the gateway is one Configure edit, deployed in one go.

## What is different for Frunchy (to settle before building it)

- **More than one service**: Frunchy and Postgres in one app (above); with federation on, Megáfono and the gateway
  join it, and the backup question for Megáfono's database comes back. The health path is `/healthz`.
- **CI** (feature 016): `ci.yml` runs the tests (Docker for Testcontainers is on `ubuntu-latest`), and `release.yml`
  pushes `ghcr.io/<owner>/frunchy:<version>` for amd64 and arm64. The deploy job waits for the publisher.
- **Settings**: Frunchy's runtime secrets (Google, MyAnimeList, the data-protection encryption key, the Megáfono token and webhook secret, the master key)
  come from Bella at runtime, a different project/environment than the workflow's Caravana settings.
- **Trusted proxies**: `Frunchy__TrustedProxies` would be `10.99.0.1` behind Caravana's Traefik.
- **Data protection keys**: in Postgres, encrypted with the encryption key above, so sessions survive a redeploy with no volume.
