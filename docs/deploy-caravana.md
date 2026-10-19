# Releasing to Caravana with Bella Baxter (backlog)

How Umbral deploys a release to Caravana, shared by the Umbral session on 2026-10-19 so Frunchy can do the
same. It is live in Umbral: `.github/workflows/release.yml` (the `deploy` job) and `deploy/caravana-app.json` in
https://github.com/Cosmic-Chimps/umbral. Frunchy has no `.github/` yet: no CI, no image, no release.

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

## What is different for Frunchy (to settle before building it)

- **More than one service**: Frunchy needs Postgres (its own volume, perhaps `backup: {engine: postgres}`), and
  with federation on also Megáfono, its own database and the gateway as the one public entrance
  (`deploy/compose.yaml` is the reference topology). Ask the caravana session for a multi-service definition.
- **No CI yet**: a test job needs Docker for Testcontainers (available on `ubuntu-latest`).
- **Settings**: Frunchy's runtime secrets (Google, MyAnimeList, the Megáfono token and webhook secret, the master key)
  come from Bella at runtime, a different project/environment than the workflow's Caravana settings.
- **Trusted proxies**: `Frunchy__TrustedProxies` would be `10.99.0.1` behind Caravana's Traefik.
- **Data protection keys**: `Frunchy:DataProtection:KeysPath` on a volume, so sessions survive a redeploy.
