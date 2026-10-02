# Contract: Provider configuration and secrets

**Consumers**: the operator (deployments), developers (local runs), and the startup validation.

## Settings

| Key | Where | Notes |
|---|---|---|
| `Frunchy:Auth:Providers:Google:Enabled` | `appsettings.json` (committed, default `false`) | |
| `Frunchy:Auth:Providers:Google:ClientId` | **secrets only** | from Google Cloud Console → OAuth client (Web) |
| `Frunchy:Auth:Providers:Google:ClientSecret` | **secrets only** | |
| `Frunchy:Auth:Providers:Discord:Enabled` | `appsettings.json` (default `false`) | |
| `Frunchy:Auth:Providers:Discord:ClientId` | **secrets only** | Discord Developer Portal → OAuth2 |
| `Frunchy:Auth:Providers:Discord:ClientSecret` | **secrets only** | |
| `Frunchy:DataProtection:KeysPath` | environment (production) | a persistent directory for cookie-encryption keys; dev uses the default key ring |

**Secrets only** means:
- **Development**: `dotnet user-secrets set "Frunchy:Auth:Providers:Google:ClientSecret" "…"`. The
  values live in the user profile, outside the repo. The fsproj gets a `UserSecretsId`.
- **Production**: environment variables, e.g. `Frunchy__Auth__Providers__Google__ClientSecret`.
- **Never** in `appsettings*.json` or any committed file. A test fails if a committed
  `appsettings*.json` contains a non-empty `ClientId` or `ClientSecret`.

## Redirect URIs to register at each provider

- Dev: `http://localhost:5099/signin-google` and `http://localhost:5099/signin-discord`.
- Prod: `https://<host>/signin-google` and `https://<host>/signin-discord`.

## Startup validation (FR-017)

| Situation | Result |
|---|---|
| Provider `Enabled = true` and `ClientId` or `ClientSecret` empty | startup **fails**: `Provider 'Google' is enabled but Frunchy:Auth:Providers:Google:ClientSecret is missing (set it with dotnet user-secrets or an environment variable)` |
| Provider disabled | its handler isn't registered and its button doesn't appear |
| No provider enabled | the site runs; `/signin` says sign-in is unavailable; API and dashboards work as before |

The operator CLI commands don't need providers, so they skip this validation.
