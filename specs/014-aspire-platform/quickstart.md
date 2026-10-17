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
