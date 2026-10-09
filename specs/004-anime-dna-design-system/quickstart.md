# Quickstart: validate feature 004

Contracts: [dna-page](contracts/dna-page.md), [design-system](contracts/design-system.md).
Rules: [data-model](data-model.md). Decisions: [research](research.md).

## Prerequisites

Everything from features 001–003: .NET 10, Docker, and Postgres in `postgres-postgres-1`. For the
token parity test, check out `../browser-extension`; without it, the test is skipped with a reason.

## 1. Automated validation (the gate)

```bash
dotnet test
```

| Suite | Proves |
|---|---|
| Every earlier suite, unchanged | routes, forms, redirects, status codes, security events and figures behave as before the redesign (FR-022, SC-003) |
| `DnaTests` (integration, 2026 sample + feature 003 fixtures) | each figure equals a hand count (SC-001); the totals equal the dashboard's; minutes weighting; dub dedup; explicit genres hidden; coverage; ties by name; empty and per-figure not-enough states; unknown user 404; en and es; identical after `user-rebuild` and re-sending (FR-016); dashboard ↔ DNA links |
| `DnaPaceTests` (`TimeProvider`) | 90-day window; steady vs in bursts at the 9-of-13-weeks edge; under 10 episodes → not enough |
| `DesignSystemTests` (unit) | token names and values match the extension's (SC-005); AA contrast for every text/background pair used (SC-004); the layout no longer loads DaisyUI; every hook in the design-system contract is still rendered |

## 2. The DNA page on real data

```bash
bella sdk run -- dotnet run --launch-profile http
open http://localhost:5099/u/jjchiw/dna
```

**Expected on 2026-10-08's data** (research R5–R9, measured in SQL):

| Figure | Expected |
|---|---|
| Totals | 2,367.6 h / 98.6 days, the same as `/u/jjchiw` |
| Favourite studio | Toei Animation (6.8%), runner-up MAPPA (4.1%) |
| Dominant decade | 2020s (80.7%) |
| Genres | Action 21.9%, Fantasy 21.7%, Adventure 13.8%, Comedy 11.6%, Drama 7.2%, Others |
| Format | TV 97.0%, ONA 2.2%, Movie 0.8%, … |
| Insignia anime | One Piece, 245 episodes |
| Pace | about 3.0 episodes per day, steady |
| Coverage | about 95% of watch time |

New syncs move these numbers; the totals must always equal the dashboard's.

**Timing**: the page must be ready in under 1 second (SC-002). Check it with the browser's network
panel or `curl -w '%{time_total}'`.

## 3. Design review (impeccable)

For each page in the contract's table, in English (default) and Spanish (`Accept-Language: es`),
at 1440px and at 360px:

1. **Audit** with the impeccable skill. No blocking finding is allowed (SC-004).
2. **Check**:
   - no DaisyUI look is left;
   - no clipped or overlapping text;
   - no horizontal scroll;
   - visible keyboard focus;
   - no animation with "reduce motion" on.
3. **Compare** with the page's mockup, when there is one.

## Revisit list (pages built before their mockup arrived)

_Filled in during implementation._

## Validation log

- **2026-10-09, foundation (T003–T007)**: on the new layout, the 228 earlier tests pass with no
  assertion changed. `DesignSystemTests` adds 6: token parity with the extension's `tokens.css`,
  WCAG AA contrast for every text/surface pair, the icon subset, and no DaisyUI.
- **2026-10-09, US1 (T008–T014)**: `DnaTests` (11) and `DnaPaceTests` (4) pass. The full suite is
  248 passed and 1 skipped (the opt-in recording test), with 0 build warnings.
- **Real data (`/u/jjchiw/dna`, worker off)**:
  - **Totals**: 2,367.6 h, 98.6 days and 5,921 episodes, identical to `/u/jjchiw`.
  - **Studio**: Toei Animation 7.2%, runner-up MAPPA 4.1%.
  - **Decade**: 2020s at 80.4%. The year falls back to `aired_from`, which shifts it slightly from
    the research prototype.
  - **Genres**: Action 21.9%, Fantasy 21.7%, Adventure 13.9%, Comedy 11.5%, Drama 7.2%, Others
    23.7%.
  - **Formats**: TV 97.0%, ONA 2.1%, Movie 0.7%.
  - **Insignia**: One Piece, 266 episodes.
  - **Pace**: 3.0 episodes per day, steady (12 of 13 weeks).
  - **Coverage**: 95.3%.
- **Timing (SC-002)**: 228 ms on the first request, then 87–92 ms.
- **Screens**: checked at 1440px and in a 360px frame. Nothing is clipped, and nothing scrolls
  sideways. Headless Chrome can't go below a 500px window, so phone widths are checked inside an
  iframe.
