# Quickstart: validate feature 015

Contract: [frunchy-megafono](contracts/frunchy-megafono.md). Decisions: [research](research.md).

## 1. Automated

```bash
dotnet build && dotnet test
```

Every suite in tasks.md runs against `FakeMegafono` (no real Megáfono, no Aspire).

## 2. Under the AppHost (SC-006)

```bash
dotnet run --project AppHost/Frunchy.AppHost.fsproj
```

1. Sign up a user (Google, configured in Bella or user secrets), import a history and open a series.
2. Through the gateway with `Host: frunchy.test`: `/.well-known/webfinger?resource=acct:{username}@frunchy.test` and `acct:{series-handle}@frunchy.test` answer 200 from Megáfono.
3. Comment on an episode; within seconds Megáfono's database has the thread (series actor) and the reply (user actor).

## Validation log
