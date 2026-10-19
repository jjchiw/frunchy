# Research: Deploy Readiness

## R1. Keys in Postgres: a custom `IXmlRepository`

- **Decision**: `Infrastructure/DataProtectionStore.fs` holds `PostgresXmlRepository(db: DbConfig)`, implementing
  `Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository`:
  - `GetAllElements()`: `SELECT xml FROM data_protection_keys ORDER BY id`, each parsed with `XElement.Parse`, returned
    as a read-only collection;
  - `StoreElement(element, friendlyName)`: `INSERT … (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT
    (friendly_name) DO NOTHING`, with `element.ToString(SaveOptions.DisableFormatting)`.

  The interface is synchronous, so the repository uses Npgsql's synchronous API (`Open`, `ExecuteReader`,
  `ExecuteNonQuery`); the framework calls it rarely (startup, a refresh about every 24 hours, a new key every 90
  days). Wired with `services.AddDataProtection().SetApplicationName "frunchy"` and
  `services.AddOptions<KeyManagementOptions>().Configure<DbConfig>(fun o db -> o.XmlRepository <- PostgresXmlRepository db)`.
- **Rationale**: no EF Core in this repo (raw Npgsql is the rule), so `PersistKeysToDbContext` is out; the
  repository is about 30 lines. A fixed application name keeps the key ring independent of the content path.
- **Concurrency**: two copies starting together on an empty table may each create a key (different friendly
  names, so both rows stay). A copy that receives a payload protected with a key it doesn't know refreshes its key
  ring once and finds it (the framework's key-not-found refresh). The test pins this.
- **Alternatives**: a volume (`PersistKeysToFileSystem`), refused because it forces one copy and stop-then-start
  (Caravana); Redis, not in the stack.

## R2. Encryption at rest: AES-256-GCM `IXmlEncryptor`

- **Decision**: `AesGcmXmlEncryptor(key: byte[])` implements `IXmlEncryptor`:
  - it serializes the element (`DisableFormatting`, UTF-8);
  - encrypts it with `System.Security.Cryptography.AesGcm`: a random 12-byte nonce, a 16-byte tag, and the
    associated data `"frunchy-data-protection-v1"`;
  - returns `EncryptedXmlInfo(<encryptedKey alg="AES-256-GCM" v="1"><value>base64(nonce‖tag‖ciphertext)</value></encryptedKey>, typeof<AesGcmXmlDecryptor>)`.

  `AesGcmXmlDecryptor(services: IServiceProvider)` implements `IXmlDecryptor` and gets the key from the
  `DataProtectionEncryptionKey` singleton registered beside it; the framework activates it through that
  constructor. It's registered with `services.Configure<KeyManagementOptions>(fun o -> o.XmlEncryptor <- AesGcmXmlEncryptor key)`.
- **Key**: `Frunchy:DataProtection:EncryptionKey`, the base64 of exactly 32 bytes, checked like
  `Megafono:MasterKey` in the AppHost.
  - Invalid → `InvalidOperationException` naming the key, never the value.
  - Missing in Production → the same.
  - Missing elsewhere → no encryptor; the framework stores plain XML and logs its own warning.
- **Rationale**: a leaked backup otherwise holds the keys that sign every session cookie (account takeover of
  everyone). The framework's built-in encryptors need a certificate or Windows DPAPI; a 32-byte secret in Bella
  matches how the platform already handles Megáfono's master key.
- **Alternatives**: `ProtectKeysWithCertificate` (a certificate to issue and rotate); no encryption (refused, see
  above).

## R3. Where the checks live

- **Decision**: `DataProtectionStore.register (environment: IHostEnvironment) (config: IConfiguration) (services: IServiceCollection)`,
  called from `Program.buildApp` right after `configureServices` (it needs `builder.Environment`). Warnings that
  need a logger (`KeysPath` retired) are logged in `buildApp` right after `Build()`.
- **Tests**: test hosts run in the Production environment (nothing sets one), so `PostgresFixture.createAppWith`
  adds a fixed test key (`TestKeys.DataProtection`, base64 of 32 bytes) to its configuration. Every test then runs
  with encryption on. A test passes an empty value to check the Production refusal.

## R4. `/healthz`

- **Decision**: `Handlers/Health.fs`, `GET [ route "/healthz" handler ]`, always mapped (federation or not).
  - It opens a connection and runs `SELECT 1` under one `CancellationTokenSource(TimeSpan.FromSeconds 1.0)` that
    covers `OpenAsync` too.
  - Success → 200 `ok`. Any exception or timeout → 503 `unavailable`, with one warning log line naming the
    exception type and message (Npgsql messages don't carry the password).
  - Headers: `Content-Type: text/plain; charset=utf-8` and `Cache-Control: no-store`.
  - No rate-limiting policy (there's no global limiter), no auth, no form, so antiforgery never runs.
- **Rationale**: Caravana calls the copy's own address with no domain Host header; `AllowedHosts` is `*` and
  forwarded headers only matter for proxies, so nothing redirects or refuses it.

## R5. Version stamping

- **Decision**: the Dockerfile takes `ARG VERSION=0.0.0-dev` and publishes with `/p:Version=$VERSION`. On startup
  `Program.main` logs `Frunchy {Version} starting` from the entry assembly's `AssemblyInformationalVersionAttribute`
  (the build appends `+<commit>` when it knows it).

## R6. Multi-arch image without emulation

- **Decision**: the build stage runs on the runner's own platform (`FROM --platform=$BUILDPLATFORM …sdk:10.0`) and
  cross-publishes with `-a $TARGETARCH` (restore too). The final stage (`aspnet:10.0`, per target platform) only
  copies files: no `RUN`, so no QEMU is needed. The old `RUN mkdir /keys` goes with the keys volume.
- **Rationale**: the official .NET multi-platform pattern; emulated `dotnet publish` is slow and flaky.

## R7. Workflows

- **CI** (`.github/workflows/ci.yml`):
  - **Triggers**: `push` on branches `**` (tags excluded: the release workflow runs the tests itself),
    `pull_request` and `workflow_call`.
  - **Runs**: `concurrency: ci-${{ github.ref }}` with cancel-in-progress, `permissions: contents: read`, a
    30-minute timeout.
  - **Steps**: checkout, `actions/setup-dotnet` with `dotnet-version: 10.0.x`, `dotnet build`, `dotnet test --no-build`.
- **Release** (`.github/workflows/release.yml`):
  - **Trigger**: `push` on tags `v*`, with `concurrency: release`.
  - **Job `test`**: `uses: ./.github/workflows/ci.yml`.
  - **Job `image`**: `needs: test`, `permissions: contents: read, packages: write`.
    - It computes `IMAGE=ghcr.io/${owner,,}/frunchy` and `VERSION=${GITHUB_REF_NAME#v}`.
    - It runs `docker/setup-buildx-action`, then `docker/login-action` to `ghcr.io` with `GITHUB_TOKEN`.
    - `docker/build-push-action` builds `linux/amd64,linux/arm64`, with `tags: $IMAGE:$VERSION`,
      `build-args: VERSION`, and the label `org.opencontainers.image.source` set to the repository URL, which links
      the package to the repo.
    - Outputs: `image` and `digest`.
- **Pinning**: first-party actions (`actions/*`, `docker/*`) at their major version. No third-party actions. No
  `latest` tag is written.

## R8. Compose

- **Decision**:
  - Postgres: `postgres:18-alpine`, volume at `/var/lib/postgresql`.
  - Frunchy: the `frunchy-keys` volume and `Frunchy__DataProtection__KeysPath` are removed, and
    `Frunchy__DataProtection__EncryptionKey: ${FRUNCHY_DATA_PROTECTION_KEY:?set FRUNCHY_DATA_PROTECTION_KEY}` is added.
  - Image: `${FRUNCHY_IMAGE:-ghcr.io/jjchiw/frunchy}`.
  - `.env.example` documents `FRUNCHY_DATA_PROTECTION_KEY` with `openssl rand -base64 32`, and says to back it up
    apart from the database.
