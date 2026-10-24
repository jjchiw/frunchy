# Tasks: Federation on Caravana

**Input**: [federation-caravana.spec.md](federation-caravana.spec.md), [plan.md](plan.md), [research.md](research.md),
[contracts/publish-graph.md](contracts/publish-graph.md)

**Tests**: required. The AppHost tests build the publish model only (no Docker, no Caravana, no network). The full
suite must stay green.

**Delegation**: one Sonnet agent, two commits. The main session reviews each diff against the contract.

## Phase 1: The publish graph (US1, US2, US3)

- [x] T001 Shared Megáfono image tag: one private helper reading `Megafono:ImageTag` (trimmed; empty or `latest`
  refused with the existing message), used by `runGraph` (unchanged behaviour) and `publishGraph`
  · AppHost/AppHost.fs
- [x] T002 `publishGraph` with `Deploy:Federation` (default `true`) per plan "Design" 2 and the contract: the four
  secret parameters (declared only when federating), `checkMasterKey`, refusal of `Megafono:BuildFromSource=true`,
  `megafono-pg`, `megafono`, `gateway`, the domain on `gateway` only, Frunchy's federation environment and trusted
  proxies; `false` keeps today's graph byte for byte. Make `publishGraph` public (tests call it). Update the module
  and function comments (018 → 018 and 026). Use the endpoints' references (`GetEndpoint`) for every service
  address, never hand-written `http://name:port` strings
  · AppHost/AppHost.fs
- [x] T003 Settings: `Deploy:Federation: true` and `Caravana:ContainerNetwork: "172.26.64.0/20"` · AppHost/appsettings.json
- [x] T004 Test project `tests/Frunchy.AppHost.Tests` (F#, xUnit v3 on Microsoft.Testing.Platform like
  `tests/FrunchyBackend.Tests`, a project reference to the AppHost, added to `FrunchyBackend.slnx` under /tests/).
  Each test builds a publish-mode builder with the parameters and settings in configuration, calls `publishGraph`
  and reads `builder.Resources`. Cover every line marked **(test)** in the contract, plus: the run graph isn't
  touched (no test needed if `runGraph` is unchanged apart from T001). Find how the Caravana package records its
  calls (annotations; reflect on the types in its DLL if they aren't public) and how Aspire 13.6.1 evaluates
  environment callbacks for the publish operation; check secret entries reference the parameter (e.g. a
  `ParameterResource`/`ReferenceExpression` value), never a literal string
  · tests/Frunchy.AppHost.Tests/*, FrunchyBackend.slnx
- [x] T005 Run `dotnet build` (root), the new tests, and the full suite (`dotnet test` from the root). Commit 1:
  `feat(026): the Caravana deploy publishes Megáfono, its gateway and its database, with federation on`

## Phase 2: Docs

- [x] T006 `docs/deploy-caravana.md`: a "Federation (feature 026)" section after the parameters table: the five
  services and who owns the domain; the four new Bella keys and how to make them (`openssl rand -base64 32` for
  the master key, `openssl rand -hex 32` for the others); **the domain is permanent** and Megáfono's database is
  never wiped after the first follow, and no test instance uses that domain; **back up the master key apart from
  the database**; port 8081 has no domain and the token is its only lock on Caravana (no private networks between
  apps); the trusted addresses (R1) and the accepted risk; the memory check before the first federated release;
  `Deploy:Federation=false` as the way back; after the deploy: WebFinger with curl, follow from Mastodon (Public
  profiles only are meant to be discoverable), comment and reply, `fediverse-status` in Caravana's console
  (`dotnet /app/FrunchyBackend.dll fediverse-status`); the discoverable gap of Megáfono 0.1.0 and the 0.2.0 pin.
  Update the intro (federation is now on) and the parameters table
  · docs/deploy-caravana.md
- [x] T007 README line 237 ("federation stays off") and CLAUDE.md (the `dotnet aspire deploy` command line:
  "frunchy + pg only, federation off" becomes the five services with federation on, `Deploy:Federation`)
  · README.md, CLAUDE.md
- [x] T008 CHANGELOG `## Unreleased`: under `### Added`, the fediverse on the Caravana deploy (what users get, the
  permanent domain); a new `### Upgrade` subsection (after Fixed, the file's order): add the four Bella keys before
  releasing (names, how to make them), back up the master key, check the host's memory, the domain is permanent,
  `Deploy:Federation=false` to stay off. Keep one subsection per kind · CHANGELOG.md
- [x] T009 Commit 2: `docs(026): federation on Caravana: keys, permanent domain, trust, checks`

## Phase 3: Review (main session)

- [x] T010 Review both diffs against the contract; run the AppHost tests and the suite; check the publish output
  if the package can write it without deploying; mark the spec Implemented.
- [ ] T011 When Megáfono 0.2.0 is tagged: bump `Megafono:ImageTag` (AppHost and `deploy/compose.yaml`), switch the
  gateway's check to HTTP `/healthz`, drop the discoverable-gap note.
