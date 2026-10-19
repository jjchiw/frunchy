# Quickstart: Deploy Readiness

## Tests

```bash
dotnet test --filter-class "*HealthTests*"
dotnet test --filter-class "*DataProtectionTests*"
dotnet test                                   # the whole suite, as CI runs it
```

## Health

```bash
dotnet run --launch-profile http &
curl -i http://localhost:5099/healthz                       # 200, ok, Cache-Control: no-store
curl -i -H "Host: anything.example" http://localhost:5099/healthz   # the same
# Stop the Postgres container, then:
curl -i --max-time 3 http://localhost:5099/healthz          # 503, unavailable, within about a second
```

## Keys survive a restart

1. Under the AppHost (`bella sdk run -- aspire run`), sign in at http://localhost:5300.
2. Restart the `frunchy` resource from the Aspire dashboard.
3. Reload: still signed in. Before this feature, a restart without `KeysPath` signed you out.

## The image

```bash
docker build --build-arg VERSION=0.0.0-local -t frunchy:local .
docker run --rm -e Frunchy__DataProtection__EncryptionKey="$(openssl rand -base64 32)" \
  -e ConnectionStrings__Database="Host=host.docker.internal;Username=postgres;Password=postgres;Database=frunchydb" \
  -p 8089:8080 frunchy:local       # the log names version 0.0.0-local; curl localhost:8089/healthz
docker run --rm -e ConnectionStrings__Database=… frunchy:local
                                   # without the key: refuses to start, naming Frunchy:DataProtection:EncryptionKey
docker buildx build --platform linux/amd64,linux/arm64 --build-arg VERSION=0.0.0-local .   # both platforms build
```

## Compose

```bash
docker compose -f deploy/compose.yaml --env-file deploy/.env.example config > /dev/null
docker compose -f deploy/compose.yaml --env-file deploy/.env.example --profile federation config > /dev/null
```

## Workflows (after pushing)

- A push to any branch shows the CI check on the commit.
- `git tag v0.1.0 && git push origin v0.1.0` runs the tests, then pushes `ghcr.io/jjchiw/frunchy:0.1.0` for both
  platforms. The run's summary lists the digest.

## Validation log

(filled in at the end of implementation)
