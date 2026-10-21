# Deploying to Caravana

## Deploying with `aspire deploy` (feature 018)

The AppHost (`AppHost/AppHost.fs`) has two graphs. `aspire run` builds the local platform. `aspire deploy` runs in
publish mode and builds `publishGraph`: **frunchy** and **pg** (Postgres 18 with the `frunchy-pg` volume), published
as one Caravana app with the NuGet package `CosmicChimps.Aspire.Hosting.Caravana`. Federation is off (Megafono, its
gateway and its database are not in this graph) and Umbral is its own Caravana app. It builds the Frunchy image,
pushes it to the registry, sends the app to Caravana, waits until it is healthy and prints Caravana's warnings.
The image is linux/amd64 whatever machine builds it (`ContainerRuntimeIdentifier` in `FrunchyBackend.fsproj`,
read only by container publishing).

### Parameters

Aspire prompts for any that is missing. Secret ones are marked.

| Parameter | Secret | What |
|---|---|---|
| `caravana-url` | no | The Caravana console, `https://...`, no trailing slash |
| `caravana-token` | yes | An API token (`cvat_...`) |
| `registry-endpoint` | no | Where the image is pushed, e.g. `ghcr.io` |
| `registry-repository` | no | The owner or namespace, e.g. `jjchiw` |
| `frunchy-domain` | no | Frunchy's public host, no scheme. Never the Caravana console's domain |
| `db-password` | yes | Postgres password (not the dev `pg-password`) |
| `data-protection-key` | yes | Base64 of 32 bytes (`openssl rand -base64 32`); encrypts the data protection keys in Postgres |
| `umbral-authority` | no | `https://` URL of the production Umbral |
| `umbral-client-id` | no | Frunchy's app in Umbral |
| `umbral-client-secret` | yes | Same |
| `mal-client-id` | yes | The MyAnimeList client id |
| `root-operator` | no | The username of the root operator |

Two plain settings, in `AppHost/appsettings.json`: `Caravana:Project` (`frunchy`) and `Caravana:Environment`
(`production`).

Where to set them:
- **A local deploy**: AppHost user secrets, `dotnet user-secrets set "Parameters:caravana-token" "..." --project AppHost`
  (or answer the prompts).
- **From Bella**: a Bella environment holding the values as `Parameters__<name>` keys (`Parameters__caravana-token`,
  `Parameters__db-password`, ...), then `bella run -- aspire deploy`.
- **CI**: the same `Parameters__<name>` environment variables.

Keep `data-protection-key` and `db-password` in Bella. Losing the key signs everyone out (the sessions' keys are in the
database, encrypted with it, and the dump alone can't forge a session). Losing the password means no way to reach the
data. Caravana's backups do not include secrets.

### Before the first deploy

1. `docker login ghcr.io` (the push uses the ambient login).
2. **GHCR package visibility**: Caravana pulls the image from the same registry. Make the `frunchy` package public
   (free, irreversible) or add `read:packages` credentials in Caravana under Settings > Registries. A private package
   with no credentials makes the rollout die at its 10-minute deadline.
3. With a **deploy token** (scoped to one app), create the project `frunchy` and the environment `production` in
   Caravana first: it can't create them. A full token creates them.
4. Set the **backup destination** in Caravana (Settings > Backup destination); without it the deploy is refused
   (Postgres is backed up nightly at 03:00 UTC, 14 kept).
5. **Umbral in production**: register Frunchy as an app in the production Umbral with the redirect URI
   `https://<frunchy-domain>/signin-umbral`, and put its client id and secret in `umbral-client-id` and
   `umbral-client-secret`.
6. The Caravana host is linux/amd64. Once the image is pushed, check it: `docker buildx imagetools inspect <image>:<tag>`
   must list `linux/amd64` (an arm64 image fails there with "exec format error" and the deploy reverts).

Then `aspire deploy`. Run it again and the same app is updated (the AppHost is the source of truth: what you change in
the console for things the AppHost describes is replaced on the next deploy).

### The Aspire CLI and the image tag

- The repo pins the Aspire CLI (NuGet `Aspire.Cli`, 13.6.1, the same as `Aspire.Hosting`) in
  `.config/dotnet-tools.json`. After `dotnet tool restore`, `dotnet aspire deploy` works locally; the globally
  installed `aspire` works too. Bump both together.
- The pushed image is tagged with `Deploy:ImageTag` (env `Deploy__ImageTag`), through Aspire's
  `WithRemoteImageTag`. The release workflow sets it to the version plus `-amd64` (`v0.1.0` pushes
  `frunchy:0.1.0-amd64`): the self-host `image` job owns `frunchy:0.1.0` (multi-arch) in the same package and runs
  at the same time, so the deploy's image gets its own tag.
  A local deploy has none and gets `local-<UTC timestamp>` (`local-20261019195636`): unique and traceable, and never
  `latest`, which Aspire would otherwise use. Set `Deploy__ImageTag` yourself to deploy a chosen version; `latest` is refused.

### Releasing (and deploying from a tag)

**Preferred: the button.** In GitHub, Actions > Release > Run workflow, branch `main`, and type the version in
`version` (`0.2.2`, no `v`; a pre-release such as `0.3.0-rc.1` works too). The workflow itself prepares the changelog
on `main`, commits it as github-actions[bot] (`chore(release): 0.2.2`), tags that commit `v0.2.2`, pushes both in one
atomic push, and then runs the tests, the image, the deploy and the GitHub Release on the tagged commit. The tag
therefore always contains its own changelog section, and `main` is renamed before anything else can merge. A tag that
already exists is refused with the commands to delete it. Nothing is tagged locally.

**Still works: pushing a tag.** `git tag v0.2.2 && git push origin v0.2.2`, from `main`, runs the same workflow. The
changelog is then prepared only in the run's own checkout and recorded on `main` afterwards, and only if `main` hasn't
moved (see the job order below), so prefer the button.

Either way `release.yml` runs the tests, then the job `deploy`, which runs `bella run -- dotnet aspire deploy
--non-interactive` with GitHub's OIDC token exchanged for a short-lived Bella key. No deploy secret is stored in
GitHub; every parameter above is read from Bella. The multi-arch `image` job still runs beside it for self-hosters.

One-time setup, done by the owner:

1. **Bella Baxter**, project `frunchy`, environment `production`: every parameter as a `Parameters__<name>` key
   (`Parameters__caravana-url`, `Parameters__caravana-token`, `Parameters__registry-endpoint` = `ghcr.io`,
   `Parameters__registry-repository` = `jjchiw`, and the rest of the table). Then Identity > Add trust domain >
   GitHub template:
   - issuer `https://token.actions.githubusercontent.com`, role Consumer, TTL 15, audiences empty;
   - claim rules: `repository_id` Equals the numeric id (`gh api repos/jjchiw/frunchy --jq .id`) AND `environment`
     Equals `production`.
2. **GitHub repository variables** (variables, not secrets: a job-level `if:` can't see environment ones):
   `BELLA_BAXTER_TENANT` (the tenant slug; the local `.bella` file names the org `code4fun-17d1c3`),
   `BELLA_BAXTER_PROJECT` = `frunchy`, `BELLA_BAXTER_ENV` = `production`. The deploy job is skipped while
   `BELLA_BAXTER_PROJECT` is empty.
3. **GitHub environment** `production` > Deployment branches and tags > Selected > two rules: the tag rule `v*` (a
   pushed tag) **and the branch rule `main` (Run workflow)**. GitHub checks the rule against the ref the run is on,
   and a Run workflow run is on `main`, so with only the tag rule the `deploy` job is refused ("not allowed to deploy
   to production due to environment protection rules") and the release stops there, after the tag was pushed. With the
   trust domain's `environment` rule, only a run of this workflow on a rule-matching ref can read the secrets. Bella's
   claim `environment = production` is the environment's name, so the trust domain doesn't change. If `main` is a
   protected branch, github-actions[bot] must be allowed to push to it (or the bot's changelog commit is refused and
   the run fails before anything is tagged).
4. **GHCR**: after the first push, make the `frunchy` package public, or add ghcr.io credentials (a `read:packages`
   token) in Caravana > Settings > Registries (the rollout otherwise dies at its 10-minute deadline). The workflow
   pushes with `GITHUB_TOKEN`; the first push creates the package under the repository, so check its "Manage Actions
   access" if a later push is refused.
5. Also the "Before the first deploy" list above (project and environment in Caravana, backup destination, Umbral).

The order of the jobs:
1. `changelog` first. It decides the version and the tag and hands them to every later job as outputs (`version`,
   `tag`, `prerelease`; no job reads `GITHUB_REF_NAME`, which is `main` on a dispatch). A release turns
   `## Unreleased` in `CHANGELOG.md` into `## X.Y.Z - <date>` and fails the run when Unreleased is empty, so nothing
   builds or deploys without notes. On a dispatch it also commits that to `main`, tags the commit and pushes both.
2. `test` (the same tests as every push, on the tagged commit), then `image` (self-host) and `deploy` (Caravana),
   each checking out the tag.
3. `release` last: the GitHub Release (the version's section plus GitHub's commit list). On a tag push it also
   commits the renamed `CHANGELOG.md` to `main` as github-actions[bot], only if `main` is still the tagged commit;
   otherwise a warning tells you to run `.github/scripts/changelog.sh prepare X.Y.Z` on `main`. It runs only when the
   deploy succeeded or was skipped, so a failed rollout leaves the tag without a release.

If a run fails after the tag was pushed (red tests, a failed rollout), the changelog commit and the tag already
exist. Re-run the failed jobs of that run (the tag and notes are kept), or delete the tag and run the workflow again
with the same version (the renamed changelog is reused, it is not renamed twice).

The changelog is written by hand (the same script and flow as Umbral and Caravana): every branch that changes
something for whoever runs Frunchy adds its bullets under `## Unreleased` in the same branch; nobody edits a
released section. **The trap**: a feature branch made before a release carries the old `## Unreleased` text, and
merging it after the release can put its entries under the wrong heading or resurrect a released section's entries.
Before releasing, and when merging `main` into a branch made earlier, check that the branch's entries are under
`## Unreleased` and the released sections are untouched. The button reduces the risk (`main` is renamed before
anything else merges) but doesn't remove it.

- The tests gate the deploy: a red test run means no deploy (and no self-host image).
- A pre-release (`0.2.0-rc.1`, by the button or by tag) builds the self-host image but does not deploy; the button
  tags `main`'s current commit for it and leaves the changelog alone.
- Re-running the jobs of a run redeploys the same tag (`aspire deploy` is idempotent).
- A tag pushed by the workflow doesn't start another run (GitHub doesn't trigger workflows from `GITHUB_TOKEN`
  pushes), so the button never releases twice.

### Pitfalls

- `POSTGRES_DB=frunchydb` applies only while the data volume is empty (the image creates the database on first start).
  On an existing volume, create it once on the pg copy: `psql -U postgres -c 'create database frunchydb'`.
- The Frunchy image must be reachable by Caravana (step 2 above).
- `Frunchy__TrustedProxies` is `10.99.0.1`, Traefik's address on installer-made hosts.
- To see what would be sent without deploying: `aspire publish -o <dir>` writes `docker-compose.yaml` and `.env` and
  contacts nothing. The domain, health checks, backup and memory are not in that file: the publisher sends them in the
  request.

---

# History: before `aspire deploy`

Until feature 018 the plan was Umbral's way (shared by the Umbral session and the caravana session on 2026-10-19): a
hand-written `deploy/caravana-app.json` (frunchy plus a Postgres service, filled by jq in CI) deployed by
`Cosmic-Chimps/caravana-action` with the image digest, the Caravana settings read from Bella through GitHub OIDC.
Caravana's `PublishToCaravana` (`CosmicChimps.Aspire.Hosting.Caravana`) replaced the file and the action; the
Bella keyless setup, the GHCR visibility pitfall, the trusted proxy `10.99.0.1` and the data protection keys in
Postgres (feature 016) carried over into the sections above. Umbral's `release.yml` and `deploy/caravana-app.json`
(https://github.com/Cosmic-Chimps/umbral) and this file's git history keep the details.
