# Research: Aspire Platform Reset

## R1. F# AppHost (spike, 2026-10-17)

A spike outside the repository built an F# AppHost against Aspire 13.6.1 (the SDK and packages are
already in the NuGet cache):

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.6.1">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><UserSecretsId>…</UserSecretsId></PropertyGroup>
  <ItemGroup><Compile Include="AppHost.fs" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.6.1" />
    <PackageReference Include="FSharp.Aspire.Hosting.AppHost" Version="13.0.0" />
  </ItemGroup>
</Project>
```

```fsharp
open Aspire.Hosting
let builder = DistributedApplication.CreateBuilder(System.Environment.GetCommandLineArgs() |> Array.skip 1)
let pg = builder.AddPostgres("pg")
let db = pg.AddDatabase("frunchydb")
let mdb = pg.AddDatabase("megafono")
let token = builder.AddParameter("megafono-token", true)
let megafono =
    builder.AddDockerfile("megafono", "<checkout>", "deploy/Dockerfile.megafono")
        .WithHttpEndpoint(targetPort = 8080, name = "federation")
        .WithHttpEndpoint(targetPort = 8081, name = "internal")
        .WithEnvironment("Megafono__App__Token", token)
        .WithEnvironment("ConnectionStrings__Database", mdb.Resource.ConnectionStringExpression)
        .WaitFor(mdb)
let frunchy =
    builder.AddProject("frunchy", "<path>/FrunchyBackend.fsproj")
        .WithReference(db).WaitFor(db)
        .WithEnvironment("Megafono__Url", megafono.GetEndpoint("internal"))
builder.Build().Run()
```

What the spike showed:
- **F# support**: the build warns `ASPIRE001` (F# not fully supported: no `Projects.*` code
  generation), so projects are added by path. It also warns `ASPIRE010` (CLI bundle off), which is
  harmless.
- **Overloads**: without `FSharp.Aspire.Hosting.AppHost`, `WithReference(db)` fails, because F#
  has no covariance. The library supplies the overloads.
- **Connection-string names**: `WithReference(db, "Database")` (an explicit connection name) has no
  F# overload. Use `WithEnvironment("ConnectionStrings__Database", res.Resource.ConnectionStringExpression)`
  for both apps, so each gets exactly the name it reads.
- **Paths**: the AppHost resolves project and checkout paths relative to its own directory
  (`__SOURCE_DIRECTORY__`), never absolute paths.
- **`NU1902`**: a MessagePack advisory comes in transitively through Aspire. Note it; there's no
  fix on our side.

## R2. Layout

```text
FrunchyBackend.slnx                  # + AppHost/Frunchy.AppHost.fsproj
AppHost/Frunchy.AppHost.fsproj
AppHost/AppHost.fs
AppHost/appsettings.json             # Megafono:SourcePath (default "../../Megafono" from the repo root), Megafono:ImageTag (empty)
Dockerfile                           # Frunchy (multi-stage, sdk:10.0 → aspnet:10.0)
deploy/compose.yaml, deploy/.env.example
```

The AppHost isn't referenced by the tests. `dotnet test` from the root must not build the AppHost
if that slows tests or needs Docker. Use a filter or keep the AppHost out of the test graph; the
slnx can still list it.

## R3. AppHost resources (Megáfono brief)

| Resource | What |
|---|---|
| `pg` | `AddPostgres("pg").WithDataVolume("frunchy-pg")`, databases `frunchydb` and `megafono` |
| `megafono` | Dockerfile or image. Endpoints `federation` (8080) and `internal` (8081, not external). Env: `ConnectionStrings__Database`, `Megafono__Domain`, `Megafono__MasterKey`, `Megafono__App__BaseUrl` (Frunchy's endpoint), `Megafono__App__Token`, `Megafono__App__WebhookSecret`, `Megafono__TrustedProxies` (any private range in dev), `ASPNETCORE_ENVIRONMENT=Development`, `Megafono__Federation__AllowPrivateAddresses=true`. Health check on `/health` on the internal endpoint |
| `gateway` | Dockerfile or image. External http endpoint (8080). Env: `ReverseProxy__Clusters__megafono__Destinations__primary__Address` (Megáfono's federation endpoint) and `…__app__…` (Frunchy's endpoint) |
| `frunchy` | Project. Env: `ConnectionStrings__Database`, `Frunchy__Federation__Enabled=true`, `Frunchy__Federation__Domain`, `Frunchy__Megafono__Url` (internal endpoint), `…__Token`, `…__WebhookSecret`, `Frunchy__TrustedProxies` (private ranges in dev) |

**Parameters**:
- `megafono-token` and `megafono-webhook-secret`: generated, secret (`AddParameter` with a
  generated default, persisted to user secrets);
- `megafono-master-key`: secret, no default; the AppHost checks it decodes to 32 bytes before
  building;
- `domain`: default `frunchy.test`.

**Gateway reaching Frunchy**: Frunchy runs as a host process and the gateway as a container. The
container reaches the host through Aspire's endpoint resolution, so use Frunchy's endpoint reference
and let Aspire map the host. If the gateway can't reach the host process in practice, the fix is a
`host.docker.internal` address. Record the outcome in the quickstart.

## R4. Frunchy settings and proxy

- **`Infrastructure/Federation.fs`** (new, small):
  - reads `Frunchy:Federation:Enabled`, `Frunchy:Federation:Domain` and
    `Frunchy:Megafono:Url|Token|WebhookSecret`;
  - validates them in `configureServices` when federation is enabled. A missing setting raises an
    error naming the key (never the value), in the style of the provider-secret check;
  - registers a `FederationSettings` singleton (`Enabled: bool`, and the rest as options) for 015
    to use.
- **Forwarded headers**: `UseForwardedHeaders` with `ForwardedHeaders.All` minus nothing.
  `KnownNetworks` and `KnownProxies` are cleared, then filled from `Frunchy:TrustedProxies`
  (comma-separated IPs or CIDR). With nothing configured, the middleware isn't added, as today.
  It must come before authentication.

## R5. Discord removal

The places to change:
- `FrunchyBackend.fsproj` (the package);
- `Program.fs` (`open`, registration, comments);
- `Infrastructure/AuthConfig.fs` (`known`);
- `Handlers/Auth/SignIn.fs` (the icon);
- `Views/Components.fs` (`providerBtn`);
- `appsettings.json`;
- tests: `AuthConfigTests`, `ClaimTests`, `FakeProvider`, which use Discord as an example; switch
  them to Google or Umbral;
- docs: README, CLAUDE.md, BRIEF.md and FRONTEND.md.

Old specs (`specs/002…`) are history and stay as they are.

## R6. Clean bootstrap

- **One `CREATE TABLE`/`CREATE INDEX` script** with each table's final shape. The patches folded
  in are:
  - `users.sync_key_hash` and `sync_key_created_at` nullable;
  - `mal_anime.source` and `mal_searches.source`;
  - every other `ALTER`/`ADD COLUMN IF NOT EXISTS`.
- **The legacy check goes**: `LegacySchemaException` and its query.
- **Proof**: a test runs the **old** bootstrap (kept verbatim as a string in the test file, from
  git) on one fresh database and the new one on another. It compares `information_schema.columns`
  (table, column, type, nullability, default), `pg_constraint` (with `pg_get_constraintdef`) and
  `pg_indexes` (with `indexdef`), normalised for names. It also runs the new bootstrap twice and
  compares again.
- **SCHEMA.md**: unchanged except for removing the legacy note.

## R7. Self-host compose

- **`deploy/compose.yaml`** has these services:
  - `postgres` (17-alpine), whose init script creates `frunchydb` and `megafono`;
  - `frunchy`, built from `Dockerfile` (image `ghcr.io/cosmic-chimps/frunchy`), on port 8080;
  - with the `federation` profile, `megafono` and `gateway` at pinned `0.1.1`.
- **Ports**: the gateway publishes 8080, or 443 behind TLS. Without the federation profile, Frunchy
  publishes its own port.
- **Secrets**: `FRUNCHY_*` and `MEGAFONO_*` come from `.env`, documented in `.env.example`, with
  the generation commands (`openssl rand -base64 32` for the master key).
- **Validation**: `docker compose config` with and without the profile. Running the minimal
  profile once builds the image, which takes minutes; done in polish.
