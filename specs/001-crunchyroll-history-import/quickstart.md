# Quickstart: validate feature 001 end to end

How to show the feature works. Contracts: [import-api](contracts/import-api.md),
[dashboard-ui](contracts/dashboard-ui.md), [operator-cli](contracts/operator-cli.md). Data rules:
[data-model](data-model.md).

## Prerequisites

- .NET SDK 10, Docker Desktop running (for the tests' Testcontainers), and a local PostgreSQL for
  manual runs (`appsettings.json` → `ConnectionStrings:Database`).
- **One-time reset of the dev database.** The new schema replaces the legacy placeholder tables,
  and the app refuses to start on the legacy layout (research R14). There's no local `psql`;
  Postgres runs in Docker, so:
  `docker exec postgres-postgres-1 dropdb -U postgres frunchydb && docker exec postgres-postgres-1 createdb -U postgres frunchydb`.
  Any other empty database works too, via `ConnectionStrings__Database`.

## 1. Automated validation (the gate)

```bash
dotnet test            # from the repo root (FrunchyBackend.slnx)
```

Expected: all green. These suites map to the spec:

| Suite | Proves |
|---|---|
| Parsing unit tests | entry classification against both fixtures (R8/R9/R10) |
| Import API tests | 202/400/401/409/413 rules, nothing stored on rejection, binding (US1 1, 7, 8, 10–16; SC-009, SC-012) |
| Processor tests | counts add up; idempotent ×3; out-of-order; unresolved resolved later; 2022 → 29/96/39.3 and 2026 → 67/298/118.2 (US1 2–6, 9; SC-003–SC-005) |
| Dashboard tests | figures, recent list, empty state, 404, en/es via `Accept-Language` (US2; SC-008, SC-011) |
| Operator tests | rebuild gives identical figures; delete removes everything and leaves other users intact (US3; SC-006, SC-010) |
| Scale test | 64 synthetic pages (~6,400 entries) processed well under SC-002; dashboard query for 10k events under SC-007 |

## 2. Manual run

```bash
dotnet run --launch-profile http                    # http://localhost:5099
dotnet run -- user-create animenerd "AnimeNerd"     # prints the sync key once → export KEY=frk_...
```

Submit the 2026 fixture and poll its status. The fixture is the extension's export envelope; add the
required `profileId` (any id for a manual run):

```bash
python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); d["profileId"]="profile-main"; print(json.dumps(d))' \
  JsonSamples/crunchyroll-history-2026-sample.json > /tmp/frunchy-sample.json
curl -s -X POST http://localhost:5099/api/imports \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  --data-binary @/tmp/frunchy-sample.json
# → 202 {"importId":"…","status":"pending",…,"entriesReceived":400}

curl -s http://localhost:5099/api/imports/<importId> -H "Authorization: Bearer $KEY"
# → status "completed"; counts { received 400, processed 309, unresolved 91, unsupported 0, failed 0 }

curl -s http://localhost:5099/api/me -H "Authorization: Bearer $KEY"
# → username, displayName, dashboardUrl, lastSyncAt, historyOwnerBound: true
```

Open `http://localhost:5099/u/animenerd`:
- English browser: 67 series, 298 episodes, 118.2 hours, 4.9 days; the first recent item is
  "Clevatess", Episode 13.
- Set the browser to Spanish, or `curl -H "Accept-Language: es"`: the same figures in Spanish
  ("118,2").
- `http://localhost:5099/u/nobody` → 404 "user not found" page.

Negative checks (each must store nothing):
- Repeat the POST with a wrong key → 401.
- `user-create other "Other"`, then POST the same fixture with the other key → 409
  `history_owner_taken`.
- POST `{"pages":[]}` → 400 `invalid_submission`.

Re-submit the same file 2 more times. The dashboard figures don't change (SC-005).

## 3. Operator flows

```bash
dotnet run -- user-rebuild animenerd                # figures identical after the worker finishes (SC-006)
dotnet run -- user-regenerate-key animenerd         # old KEY → 401, new key works
dotnet run -- user-reset-binding animenerd          # next accepted POST binds again
dotnet run -- user-delete animenerd --yes           # /u/animenerd → 404; key → 401; other users unchanged
```

## 4. With the real extension

Coordinate with the extension session (see plan.md, "Extension coordination"):
1. Point the extension at `http://localhost:5099` and paste the key. `GET /api/me` validates it.
2. Visit crunchyroll.com/history. Pages flow in as `POST /api/imports`, one page per request.
3. `/u/animenerd` reflects the full history after the last page (6,363 entries in the reference
   capture → 327 series, 5,921 episodes, ≈ 2,367.6 h).

## Validation log

- **2026-10-02 (T057)**: sections 1–3 run on a fresh database (`frunchy_quickstart`, dropped
  afterwards). All results as expected:
  - 88/88 tests;
  - 2026 sample: 202 in ~0.2 s, then `{400, 309, 91, 0, 0}`; dashboard 67 / 298 / 118.2 / 4.9 (es
    "118,2", "Episodio 13"), first item "Clevatess" Episode 13;
  - 401 / 409 `history_owner_taken` / 400 as listed; figures unchanged after 2 re-submits and after
    `user-rebuild`;
  - key regeneration revoked the old key; delete removed 3 imports and 400 watch events, then 404 /
    401.

  Deviation from the original text: the reset command uses `docker exec` (no local `psql`).
- **2026-10-02 (T052, section 4)**: real sync from the extension into `frunchydb` (68 pages). The
  dashboard shows 327 / 5,921 / 2,367.6 h / 98.6 days and the database has 6,363 watch events and
  290 unresolved entries across 19 series, all matching the reference capture.
