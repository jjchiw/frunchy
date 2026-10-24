# Implementation Plan: Federation on Caravana

**Branch**: `026-federation-caravana` | **Spec**: [federation-caravana.spec.md](federation-caravana.spec.md) |
**Research**: [research.md](research.md) | **Contract**: [contracts/publish-graph.md](contracts/publish-graph.md)

## Summary

`AppHost/AppHost.fs`'s `publishGraph` grows from two services to five when `Deploy:Federation` is true (the
default): `megafono-pg`, `megafono` and `gateway` join `frunchy` and `pg`, the domain moves to `gateway`, and Frunchy
gets the federation settings. With `Deploy:Federation=false` it stays the 018 graph. The run graph doesn't change.
A new test project builds the publish model and checks the contract. Docs, CHANGELOG and CLAUDE.md follow.

## Technical Context

F# Aspire AppHost 13.6.1, `CosmicChimps.Aspire.Hosting.Caravana` 0.1.0, `Aspire.Hosting.PostgreSQL` 13.6.1,
`FSharp.Aspire.Hosting.AppHost` 13.0.0. Tests: xUnit v3 on Microsoft.Testing.Platform (as the main test project),
no Docker. No change to the Frunchy app itself: federation is already built (feature 015) and configured by
environment variables.

## Constitution Check

- Keep it simple: no new infrastructure beyond Megáfono, which feature 015 already chose; one Caravana app.
- Watch history is the core asset: `pg` and its backup are unchanged; Megáfono's data gets its own backup.
- Tests required: the publish model is tested; the real deploy is the owner's release.
- Secrets: only as secret parameters, never in appsettings, never printed.

## Design

1. **Shared Megáfono settings**. Move the `Megafono:ImageTag` reading (refuse empty and `latest`) into a helper
   used by both graphs. Publish mode always uses the published images: `Megafono:BuildFromSource=true` with
   federation on is refused in publish mode with a message.
2. **`publishGraph`** (public, so the test can call it). After the 018 parameters, when `Deploy:Federation`:
   - parameters `megafono-master-key`, `megafono-token`, `megafono-webhook-secret`, `megafono-db-password`
     (secret); `checkMasterKey` on `Parameters:megafono-master-key`;
   - `megafono-pg`: `AddPostgres("megafono-pg", password = megafonoDbPassword)`, `WithDataVolume("megafono-pg")`,
     `POSTGRES_DB=megafono`, Caravana health check (TCP), backup Postgres `30 3 * * *` keep 14, memory 128/256;
     `AddDatabase("megafono-db", "megafono")` for the connection string;
   - `megafono`: container `ghcr.io/cosmic-chimps/megafono:<tag>`, endpoints `federation` (8080) and `internal`
     (8081), the R2 environment with `ASPNETCORE_ENVIRONMENT=Production`, `Megafono__App__BaseUrl` = Frunchy's
     `http` endpoint, `Megafono__TrustedProxies` = `Caravana:ContainerNetwork`, Caravana health check `/health` on
     8081, memory 256/512, `WaitFor(megafono-db)`;
   - `gateway`: container `ghcr.io/cosmic-chimps/megafono-gateway:<tag>`, one http endpoint (8080), external,
     the two cluster addresses (Megáfono's `federation` endpoint, Frunchy's `http` endpoint),
     `Gateway__TrustedProxies=10.99.0.1`, `WithCaravanaDomain(caravana, frunchyDomain)`, Caravana health check TCP,
     memory 64/128, `WaitFor` both;
   - `frunchy`: everything 018 sets, except no domain and no external endpoint, `Frunchy__TrustedProxies` =
     `Caravana:ContainerNetwork`, plus `Frunchy__Federation__Enabled=true`, `Frunchy__Federation__Domain`,
     `Frunchy__Megafono__Url` = Megáfono's `internal` endpoint, token, webhook secret.

   When `Deploy:Federation` is false: exactly today's graph (and none of the four parameters is declared, so
   Aspire never prompts for them).
3. **Settings** in `AppHost/appsettings.json`: `Deploy:Federation: true`, `Caravana:ContainerNetwork:
   "172.26.64.0/20"`.
4. **Tests**: `tests/Frunchy.AppHost.Tests` (in the slnx, no Docker), per the contract.
5. **Docs**: `docs/deploy-caravana.md` (a federation section: the services, the four Bella keys and how to make
   them, the permanent domain, the master-key backup, the 8081 note, the trusted ranges and their accepted risk,
   the memory check, `Deploy:Federation=false` as the way back, how to check after the deploy, the discoverable
   gap), README line on the Caravana deploy, CLAUDE.md (commands section and AppHost note), CHANGELOG Unreleased
   (Added + Upgrade).

## Delegation

One Sonnet agent in this worktree, two commits: (1) AppHost and its tests, (2) docs. The main session reviews
each diff against the contract, runs both test projects and the AppHost build, and checks the generated publish
output if the package can write it without deploying.
