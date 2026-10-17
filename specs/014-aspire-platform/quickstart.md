# Quickstart: validate feature 014

Contract: [settings](contracts/settings.md). Decisions: [research](research.md).

## 0. Wipe (owner, once)

Wipe the old local databases before the first run:

```bash
docker exec postgres-postgres-1 dropdb -U postgres frunchydb
docker exec postgres-postgres-1 dropdb -U postgres megafono   # if it exists
```

## 1. Automated validation (no Aspire, no Megáfono)

```bash
dotnet build && dotnet test
```

| Suite | Proves |
|---|---|
| `FederationSettingsTests` | Off: starts without settings. On: each of the 4 missing settings stops startup with its key named and no value printed; all present: starts and registers the settings |
| `ForwardedHeadersTests` | A trusted proxy's forwarded host and scheme reach sign-in redirects; an untrusted address's are ignored; none configured: ignored |
| `SchemaBootstrapTests` | The new bootstrap's catalog (columns, constraints, indexes) equals the old one's; it is idempotent |
| Sign-in tests | No Discord option; `/signin/discord` is not found |

## 2. AppHost (by hand)

```bash
cd AppHost
dotnet user-secrets set "Parameters:megafono-master-key" "$(openssl rand -base64 32)"
aspire run          # or: dotnet run
```

`Megafono:SourcePath` defaults to `../../Megafono` from the repository root. In a linked worktree that path
doesn't resolve, so set it:
`dotnet user-secrets set "Megafono:SourcePath" "/path/to/Megafono" --project AppHost`.

Expected: the dashboard shows `pg`, both databases, `megafono`, `gateway` and `frunchy` healthy.
Then check:
- the gateway's URL serves the home page;
- `<gateway>/.well-known/webfinger?resource=acct:nobody@frunchy.test` answers from Megáfono (404 JSON);
- Frunchy's log says federation is enabled.

## 3. Self-host compose

```bash
docker compose -f deploy/compose.yaml --env-file deploy/.env.example config
docker compose -f deploy/compose.yaml --env-file deploy/.env.example --profile federation config
```

## Validation log

**2026-10-17 (Phase 2, T008 to T011).**

- **`dotnet test`** from the root (builds the AppHost too): 512 tests, 510 passed, 0 failed, 2 skipped
  (the extension token parity check, which needs `../browser-extension`, and the opt-in MyAnimeList
  recording). 1 m 39 s. `dotnet build --no-incremental` of the whole solution: 0 warnings, 0 errors.
- **AppHost build** (`dotnet build AppHost/Frunchy.AppHost.fsproj`): succeeded, 0 warnings. `ASPIRE001`,
  `ASPIRE010` and `NU1902` are suppressed in the AppHost project (reasons in its `.fsproj`), so adding it
  to `FrunchyBackend.slnx` keeps the root build at 0 warnings and it needs neither Docker nor the Megafono
  checkout to compile. `FrunchyBackend.fsproj` excludes `AppHost/**` from the Web SDK's globs, so the
  AppHost's `appsettings.json` isn't copied into Frunchy's output.
- **Master key check** (AppHost started with a missing key, then with `abc`; no resource starts): it stops
  before `Build()` with "Parameter megafono-master-key is not set" / "must be the base64 of exactly 32
  bytes", plus the `dotnet user-secrets set` command, and never prints the value.
- **`docker compose config`** with `deploy/.env.example`: succeeds without the profile (services `frunchy`,
  `postgres`) and with `--profile federation` (adds `megafono`, `gateway`). 8081 is never published.
- **Minimal profile run**: `docker build` of the `Dockerfile` succeeded, and
  `docker compose up -d --build` (project `frunchytest`, `FRUNCHY_PORT=18080`) started `postgres` and `frunchy`.
  `GET http://127.0.0.1:18080/` answered **200**; both `frunchydb` and `megafono` databases existed
  (the init script ran); the container runs as uid 1654 and `/keys` is writable.
  `dotnet FrunchyBackend.dll user-list` works inside the container. Cleaned up with `down -v` and the image
  removed. Npgsql prints "Cannot load library libgssapi_krb5.so.2" on the CLI in the slim image: a harmless
  Kerberos probe.
- **Not validated here**: the federation profile was only checked with `config`.
  `ghcr.io/cosmic-chimps/megafono:0.1.1` answered "manifest unknown" to an unauthenticated
  `docker manifest inspect` (not published yet, or private; Megafono's repo has only a `v0.1.0` tag), so the
  profile can't be run until the images exist. `MEGAFONO_VERSION` in `.env` changes the pinned tag.

**For the main session's manual AppHost run (section 2), points no check here covered:**

- Set `Megafono:SourcePath` if running from a worktree (above), and the master key secret.
- The gateway is a container and Frunchy a host process. Frunchy's endpoint is given to the containers as an
  Aspire endpoint reference (Aspire resolves it to `host.docker.internal:<port>` for a container). If the
  gateway gets connection refused to Frunchy, check that Frunchy's proxied port listens beyond loopback, and
  fall back to a `host.docker.internal` address. Record the outcome here.
- Megafono's `Dockerfile`s use `ARG BUILDPLATFORM`: BuildKit (the default) sets it; a classic builder needs it
  passed as a build argument.
- In the AppHost, Frunchy trusts `127.0.0.1`, `::1` and the private ranges as proxies, and Megafono the
  private ranges.
