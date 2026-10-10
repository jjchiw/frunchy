# Implementation Plan: Review MyAnimeList Matches on the Web

**Branch**: `007-mal-review` | **Date**: 2026-10-10 | **Spec**: [mal-review.spec.md](mal-review.spec.md)

## Summary

The feature has three parts:
- **Shared actions**: the MyAnimeList actions move into one module, `Infrastructure/MalActions.fs`,
  that the CLI and the new web pages both call. It holds set, none, retry, and a new "back to
  automatic". Each action takes a guard against stale forms.
- **Review slice**: `Handlers/Review/MalReview.fs` serves the list, the manual view and the four
  actions at `/review/mal`.
- **Access**: only usernames listed in `Frunchy:Operators` get in. Everyone else gets a 404, checked
  before the forgery check so the endpoints stay hidden.

There's no new table and no new package.

## Project Structure

```text
Infrastructure/MalActions.fs        # NEW: setMatch, setNone, retry, backToAutomatic (+ expect guard)
Infrastructure/WebAuth.fs           # username in Items; currentUsername, isOperator, requireOperator
Domains/Identity.fs                 # SecurityEventType: mal_set, mal_none, mal_retry, mal_auto
Operations/MalSet.fs, MalNone.fs, MalRetry.fs   # call MalActions (Any); behaviour unchanged
Operations/MalAuto.fs               # NEW CLI: mal-auto <crSeasonId>
Handlers/Review/MalReview.fs        # NEW slice: list, manual view, 4 POST actions
Handlers/Account/AccountPage.fs     # operator-only link
Views/Texts.fs                      # review texts, event labels (en + es)
Program.fs, FrunchyBackend.fsproj   # endpoints, CLI command, rate limit "review-write"
tests/FrunchyBackend.Tests/MalReviewTests.fs, MalReviewAccessTests.fs, MalActionsParityTests.fs   # NEW
```

**Structure Decision**: actions shared by the CLI and the web go in `Infrastructure/`, as
`AccountActions.fs` does for accounts. The pages are one slice file with an explicit verb per
endpoint.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. Only `cr_season_matches` (derived) and `mal_searches` (cache) change, through the existing writers' rules; manual rows stay operator input, as in feature 003. |
| II. Boring tech | PASS. No package, no queue change; actions reuse the Postgres queue fields. |
| III. Vertical slices | PASS. One slice for the pages; shared actions in `Infrastructure/` (used by the CLI and the web). |
| IV. Idempotent ingestion | PASS. External calls only in `setMatch` (as `mal-set` today), paced by the existing client. |
| V. Tests | PASS. Integration tests on real Postgres with `FakeMal`: access matrix, each action, CLI/web parity, stale forms, rate limit, CSRF. |
| Constraints | PASS. Texts en + es; CDN-only; no schema change; pinned APIs. |

## Security review points (main session)

- **Access ordering**: `requireOperator` runs before `readValidatedForm` on every POST.
- **No leaks**: a 404 for everyone else, with no redirect to sign-in.
- **Validation**: season ids and MyAnimeList inputs are validated before any query or request.
- **Writes**: guarded writes do the check and the write in one transaction (`FOR UPDATE`).
- **Rate limit**: on every POST.

## Delegation

Sonnet subagents write the code. The main session reviews every diff, the security points above
first, and runs the tests before merging.
