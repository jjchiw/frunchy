# Quickstart: validate feature 007

Contract: [review-pages](contracts/review-pages.md). Rules: [data-model](data-model.md). Decisions:
[research](research.md).

## 1. Automated validation

```bash
dotnet test
```

| Suite | Proves |
|---|---|
| `MalActionsParityTests` (10) | **CLI parity (SC-001):** for set, none, retry and auto, the CLI command and the shared `MalActions` call leave identical `cr_season_matches` rows (every column a decision sets), dubs included, and the same stored searches dropped. **State checks:** `Open` and `Manual` refuse the wrong states with `NotOpen` and write nothing; an unknown season is `UnknownSeason` whatever the expectation. **No client id:** `setMatch` is `Unavailable`, makes no request and writes nothing. `mal-auto` exits 2, 3 and 1. |
| `MalReviewAccessTests` (8) | **Access matrix (SC-003):** a visitor, a signed-in non-operator and a signed-in boss with no operators configured get 404 and the plain not-found page on every route and verb (4 GETs; 4 POSTs, each with a valid token and without one), with no redirect and the database unchanged; the operator gets 200 / 302, and 400 for a forged POST. Operators as an array, as a comma list (with and without spaces) and in any letter case; empty, blank and other names mean nobody; removing an operator at runtime closes the pages. **Rate limit:** 429 on an operator's 31st POST a minute, while 35 outsider POSTs all stay 404. **Account link:** only operators see `data-review-link`. **Security activity:** each action adds its `mal_*` event; refusals add none. |
| `MalReviewTests` (15) | **List (SC-002):** the open seasons in the CLI's order, cut to 50 with the "N more" note; the status filter and its empty state; a row's users, fully watched episodes, watcher link (the latest watcher) and candidates. **Actions:** picking a candidate, a typed id (one fetch) or link, none and retry; the dub follows; the web and the CLI leave the same rows. **Refusals:** bad input (before any request), unknown season, unknown anime, stale forms and a missing client id change nothing. **Manual view (US2):** newest first; back to automatic requeues the season and its dubs and drops it from the view. en and es. |
| `DesignSystemTests` | The review pages (as an operator) and the account link are on the design system and keep their hooks. |

## 2. On real data

Add yourself as an operator (never commit a username to `appsettings*.json`; use user-secrets, Bella
or the environment):

```bash
Frunchy__Operators__0=<your username> bella sdk run -- dotnet run --launch-profile http
open http://127.0.0.1:5099/review/mal
```

Pick a candidate or mark a season, check that it leaves the list and appears in
`/review/mal/manual`, send it back to automatic, and check `/account` for the security activity.
With another account (or signed out), `/review/mal` shows "Page not found".

## Validation log

- **2026-10-10**:
  - **Suite**: `dotnet test` gives **320 passed, 1 skipped** (the opt-in recording test), with 0
    build warnings.
  - **Delegation**: the foundation (T001–T007) and the pages (T008–T013) were reviewed by the main
    session before this polish. Review found one real defect: the `review-write` limiter answered an
    outsider's 31st POST with 429 instead of 404, which revealed the route. It now partitions
    operators only, and a test pins that 35 outsider POSTs stay 404.
  - **Access matrix** (SC-003): 3 kinds of caller x 8 requests, all 404 with the not-found page and no
    data change; the operator reaches all of them.
  - **Screenshots**: headless Chrome at 1440px and a 360px iframe of the list, an ambiguous row with
    two candidates and the manual view. Two fixes came from them: the secondary actions moved next to
    the id field, and the id form became full width on a phone.
  - **Real database**: not run in this session (the operator check on `frunchydb` is left to the
    operator, section 2).
