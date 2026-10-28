# Deploying to Caravana

## Deploying with `aspire deploy` (feature 018)

The AppHost (`AppHost/AppHost.fs`) has two graphs. `aspire run` builds the local platform. `aspire deploy` runs in
publish mode and builds `publishGraph`, published as one Caravana app with the NuGet package
`CosmicChimps.Aspire.Hosting.Caravana`. With federation on (the default, `Deploy:Federation`, feature 026) the app has
five services: **frunchy**, **pg** (Postgres 18 with the `frunchy-pg` volume), **megafono-pg** (Megáfono's own Postgres),
**megafono** and **gateway**. With `Deploy:Federation=false` it has the first two only, as before feature 026. Umbral is
its own Caravana app. It builds the Frunchy image, pushes it to the registry, sends the app to Caravana, waits until it
is healthy and prints Caravana's warnings. The image is linux/amd64 whatever machine builds it
(`ContainerRuntimeIdentifier` in `FrunchyBackend.fsproj`, read only by container publishing).

### Parameters

Aspire prompts for any that is missing. Secret ones are marked.

| Parameter | Secret | What |
|---|---|---|
| `caravana-url` | no | The Caravana console, `https://...`, no trailing slash |
| `caravana-token` | yes | An API token (`cvat_...`) |
| `registry-endpoint` | no | Where the image is pushed, e.g. `ghcr.io` |
| `registry-repository` | no | The owner or namespace, e.g. `jjchiw` |
| `frunchy-domain` | no | Frunchy's public host, no scheme. Never the Caravana console's domain. **Permanent once federation is on** (see below) |
| `db-password` | yes | Postgres password (not the dev `pg-password`) |
| `data-protection-key` | yes | Base64 of 32 bytes (`openssl rand -base64 32`); encrypts the data protection keys in Postgres |
| `umbral-authority` | no | `https://` URL of the production Umbral |
| `umbral-client-id` | no | Frunchy's app in Umbral |
| `umbral-client-secret` | yes | Same |
| `mal-client-id` | yes | The MyAnimeList client id |
| `root-operator` | no | The username of the root operator |
| `megafono-master-key` | yes | Federation only. Base64 of 32 bytes (`openssl rand -base64 32`); encrypts every actor's private key in Megáfono |
| `megafono-token` | yes | Federation only. `openssl rand -hex 32`; the bearer token between Frunchy and Megáfono |
| `megafono-webhook-secret` | yes | Federation only. `openssl rand -hex 32`; signs Megáfono's webhooks to Frunchy |
| `megafono-db-password` | yes | Federation only. `openssl rand -hex 32`; Megáfono's Postgres password (hex keeps it safe in a connection string) |

The four `megafono-*` parameters are declared only while `Deploy:Federation` is true, so a deploy without federation is
never asked for them. The master key is checked before anything is built: a missing or malformed one stops the deploy,
naming the parameter and never printing the value.

Plain settings, in `AppHost/appsettings.json`: `Caravana:Project` (`frunchy`), `Caravana:Environment` (`production`),
`Deploy:Federation` (`true`), `Caravana:ContainerNetwork` (`172.26.64.0/20`, see "Trusted addresses") and
`Megafono:ImageTag` (the pinned Megáfono release, shared with `aspire run`; empty or `latest` is refused).
`Megafono:BuildFromSource=true` is for `aspire run` only: with federation on, `aspire deploy` refuses it, because the
deploy always uses the published images.

Where to set them:
- **A local deploy**: AppHost user secrets, `dotnet user-secrets set "Parameters:caravana-token" "..." --project AppHost`
  (or answer the prompts).
- **From Bella**: a Bella environment holding the values as `Parameters__<name>` keys (`Parameters__caravana-token`,
  `Parameters__db-password`, ...), then `bella run -- aspire deploy`.
- **CI**: the same `Parameters__<name>` environment variables.

Keep `data-protection-key`, `db-password` and the four `megafono-*` secrets in Bella. Losing the key signs everyone out (the sessions' keys are in the
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
   (Postgres is backed up nightly at 03:00 UTC, 14 kept; with federation, Megáfono's database at 03:30).
5. **Umbral in production**: register Frunchy as an app in the production Umbral with the redirect URI
   `https://<frunchy-domain>/signin-umbral`, and put its client id and secret in `umbral-client-id` and
   `umbral-client-secret`.
6. The Caravana host is linux/amd64. Once the image is pushed, check it: `docker buildx imagetools inspect <image>:<tag>`
   must list `linux/amd64` (an arm64 image fails there with "exec format error" and the deploy reverts).

Then `aspire deploy`. Run it again and the same app is updated (the AppHost is the source of truth: what you change in
the console for things the AppHost describes is replaced on the next deploy).

### Federation (feature 026)

With `Deploy:Federation=true` the app is five services, and **the gateway owns the domain**:

| Service | What | Domain |
|---|---|---|
| `gateway` | `ghcr.io/cosmic-chimps/megafono-gateway`, plain http on 8080, health `/healthz` | `frunchy-domain` |
| `frunchy` | the app | none |
| `megafono` | `ghcr.io/cosmic-chimps/megafono`; 8080 is the federation port, 8081 the internal API Frunchy calls | none |
| `megafono-pg` | Postgres with the `megafono` database and volume | none |
| `pg` | Postgres with `frunchydb` | none |

Traefik sends the whole host to the gateway. The gateway sends Megáfono's routes (WebFinger, the actor and inbox URLs)
to Megáfono's federation port and everything else to Frunchy. Megáfono's port 8081 never goes through the gateway.

Both images are pinned to `Megafono:ImageTag` (never `latest`) and come from GHCR (`ghcr.io/cosmic-chimps`), the
same ones `deploy/compose.yaml` pulls; `aspire deploy` pushes only the Frunchy image. Each Postgres has its own nightly dump (Frunchy's at 03:00 UTC, Megáfono's at 03:30,
14 kept each).

**Before the first federated release**

1. **Add the four Bella keys** to `frunchy/production`: `Parameters__megafono-master-key` (`openssl rand -base64 32`),
   and `Parameters__megafono-token`, `Parameters__megafono-webhook-secret`, `Parameters__megafono-db-password` (each
   `openssl rand -hex 32`). The release stops at once if the master key is missing or is not base64 of 32 bytes.
2. **Back up the master key apart from the database.** It encrypts every actor's private key in Megáfono's database.
   Caravana's backups do not include secrets, so without a copy of the key a restored dump cannot sign anything.
3. **The domain is permanent.** Once an instance has been followed, every actor is `@name@<frunchy-domain>` and remote
   servers store that. Changing it later breaks every follow and every thread; the old identities cannot be moved.
   Never wipe Megáfono's database after the first follow, and never use the production domain for a test instance
   (a second instance on the same domain would sign with different keys).
4. **Check the host's memory.** The app reserves about 960 MB with ceilings up to about 2.2 GB (frunchy 256/768, pg
   256/512, megafono-pg 128/256, megafono 256/512, gateway 64/128). Compare with what the host has free (`nomad node
   status -self`, Reserved against Allocated, or each app's Running tab) before releasing.

**Trusted addresses**

- The gateway trusts `10.99.0.1` (`Gateway__TrustedProxies`): Traefik runs in host networking and reaches the gateway
  through its host port, so its requests come from Caravana's own address.
- Megáfono (`Megafono__TrustedProxies`) and Frunchy (`Frunchy__TrustedProxies`) trust `Caravana:ContainerNetwork`,
  `172.26.64.0/20`. Inside one app the services reach each other by name over Nomad's bridge, so Megáfono sees the
  gateway's own container address, which changes on every deploy and always sits in that range. Installer-made hosts keep
  Nomad's default bridge subnet; if yours does not, change the setting.
- **Accepted risk:** every app on the host shares that bridge, so a container of another app can reach Megáfono or
  Frunchy directly and send its own `X-Forwarded-For`. It gets to pick its rate-limit bucket and the address in the
  logs; no session, sign-in or data comes with it. Caravana's rule is that apps on one host trust each other, and it has
  network isolation between apps on its backlog. Never add `172.26.64.1` to the range: traffic from other apps through a
  host port arrives masqueraded as that address.
- **Port 8081 has no domain, and its bearer token (`megafono-token`) is its only lock on Caravana**: there are no private
  networks between apps yet, so another app on the host could reach it. Keep the token long and secret.

**Staying off.** `Deploy:Federation=false` (for example `Deploy__Federation=false` in the environment of the deploy) brings
back the graph from before feature 026: `frunchy` with the domain and `pg`, no Megáfono parameters needed. Frunchy's
federation switch stays off and every federation route answers the plain 404. Check in Caravana that the `megafono-pg`
volume is still there before turning federation on again: its identities (and the master key that unlocks them) must
come back with the same domain.

**After the deploy**

1. WebFinger answers through the gateway:
   `curl -s "https://<frunchy-domain>/.well-known/webfinger?resource=acct:<username>@<frunchy-domain>"` returns JSON
   with a `self` link.
2. From a Mastodon account, search `@<username>@<frunchy-domain>` and follow. Only **Public** profiles are meant to be
   found there (Members and Private ones should not be discoverable).
3. Comment on an episode page as that user and check that the post shows up in Mastodon; reply from Mastodon and check
   that the reply shows on the episode page.
4. `fediverse-status` shows the outbox: in Caravana's console for the `frunchy` service, run
   `dotnet /app/FrunchyBackend.dll fediverse-status`. Items stuck `waiting` or listed as `failed` say what is wrong.

**Megáfono 0.2.0 or later.** From 0.2.0 an actor's `discoverable` and `indexable` come from Frunchy's actor lookup
(feature 021: both true for Public profiles and series, both false for Members and Private), and a change of visibility
goes out with the next profile update. 0.1.0 marked every user as discoverable, so never pin it with federation on.
The gateway's Caravana check is HTTP `/healthz` on 8080, which 0.2.0's gateway answers itself without proxying (so
`https://<frunchy-domain>/healthz` is the gateway's answer, not Frunchy's; Caravana checks Frunchy on its own port).

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
`version` (`0.2.2`, no `v`; a pre-release such as `0.3.0-rc.1` works too). The workflow runs the tests on `main` as
it is; only when they pass does it gather the notes in `changelog.d/` into the changelog, commit that (the new
section and the deleted note files) as github-actions[bot] (`chore(release): 0.2.2`), tag that commit `v0.2.2` and
push both in one atomic push (refused, and nothing tagged, if `main` moved during the tests). Then the image, the
deploy and the GitHub Release run on the tagged commit. The tag therefore always contains its own changelog section
and red tests never leave a tag behind.
A tag that already exists is refused with the commands to delete it. Nothing is tagged locally.

**By hand: prepare, commit, then tag.** `.github/scripts/changelog.sh prepare 0.2.2`, commit it
(`chore(release): 0.2.2`), tag that commit (`v0.2.2`, annotated), then push `main` and the tag together with
`--atomic`. The workflow refuses a pushed tag whose commit doesn't hold its own `## 0.2.2` section, so every release
tag records its notes.

**Notes are files, and released notes are protected.** Nothing is written in `CHANGELOG.md` for a change: each
change adds one file in `changelog.d/` (see its README), and the release gathers them. CI runs three things on every
push:

- `.github/scripts/changelog.sh check`: every note file is well formed (a lowercase-dashes `.md` name, only
  `### Added|Changed|Fixed|Security|Upgrade` subsections of `- ` bullets, not empty), and `CHANGELOG.md` has no
  `## Unreleased` section. The error names the file and line.
- `.github/scripts/changelog.sh released`: each `## X.Y.Z` section must read exactly as in its tag. Releases from
  before this rule (0.1.0, 0.2.0) don't hold their section in their tag and aren't checked.
- `.github/scripts/changelog-test.sh`: tests for the script itself, in a temporary tree.

`changelog.sh unreleased` prints what the next release would say.

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
   `tag`, `prerelease`; no job reads `GITHUB_REF_NAME`, which is `main` on a dispatch). A release gathers
   `changelog.d/*.md` into `## X.Y.Z - <date>` in `CHANGELOG.md` (one subsection per kind) and fails the run when
   there are no notes in `changelog.d`, so nothing builds or deploys without notes. On a dispatch it also commits
   that (the section and the deleted files) to `main`, tags the commit and pushes both.
2. `test` (the same tests as every push, on the tagged commit), then `image` (self-host) and `deploy` (Caravana),
   each checking out the tag.
3. `release` last: the GitHub Release (the version's section plus GitHub's commit list). On a tag push it also
   commits the changed `CHANGELOG.md` to `main` as github-actions[bot], only if `main` is still the tagged commit;
   otherwise a warning tells you to run `.github/scripts/changelog.sh prepare X.Y.Z` on `main`. It runs only when the
   deploy succeeded or was skipped, so a failed rollout leaves the tag without a release.

If a run fails after the tag was pushed (the image or a failed rollout; red tests stop a button run before tagging),
the changelog commit and the tag already exist. Re-run the failed jobs of that run (the tag and notes are kept), or delete the tag and run the workflow again
with the same version (the version's section is reused, it is not made twice).

The notes are written by hand (the same script as Umbral and Caravana): every branch that changes something for
whoever runs Frunchy adds one file in `changelog.d/`, in the same branch (`changelog.d/027-seasons.md`; the branch
or feature name is a good file name); nobody edits `CHANGELOG.md` or a released section. **The old trap is gone**:
notes used to sit under a shared `## Unreleased` heading, which a release renamed, so a branch started before a
release and merged after it could have git put its bullets inside the released section. Now no two changes edit the
same lines, and `CHANGELOG.md` has no heading for a stale branch to land under. A branch that still carries the
old `## Unreleased` section (started before this rule) fails `changelog.sh check` in CI with what to do: move its
bullets into a new `changelog.d/<name>.md` and delete the section. Nothing needs checking by hand before a release.

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
- `Frunchy__TrustedProxies` is `10.99.0.1` (Traefik's address on installer-made hosts) only without federation; with it,
  Frunchy is behind the gateway and trusts `Caravana:ContainerNetwork` (see "Trusted addresses").
- `POSTGRES_DB=megafono` has the same first-start rule as Frunchy's: on an existing `megafono-pg` volume, create the
  database once on that copy.
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
