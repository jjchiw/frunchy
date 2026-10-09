# Quickstart: validate feature 005

Contract: [year-page](contracts/year-page.md). Rules: [data-model](data-model.md). Decisions:
[research](research.md).

## 1. Automated validation

```bash
dotnet test
```

| Suite | Proves |
|---|---|
| `YearTests` (11) | every figure of 2022, 2024 and 2026 in the 2026 sample against an independent F# count (SC-001); the years add up to the dashboard within rounding (SC-002); coverage and per-section "not enough data"; the empty state at 14 and 15 episodes; 404 for unknown users and bad years; en and es; identical figures after `user-rebuild` and a re-send; no MyAnimeList call |
| `YearNavigationTests` (5) | dashboard and DNA links; `/year` redirects; a year without history redirects; the picker's years, order and `aria-current` (SC-004) |
| `DesignSystemTests` | the year page has no DaisyUI and keeps its hooks (SC-005) |

## 2. On real data

```bash
bella sdk run -- dotnet run --launch-profile http
open http://localhost:5099/u/jjchiw/year
```

## Validation log

- **2026-10-09**:
  - **Suite**: `dotnet test` gives **266 passed, 1 skipped** (the opt-in recording test), with 0
    build warnings. 16 tests are new, plus the year page in the every-page design check.
  - **`/u/jjchiw/year/2025` on the real database (worker off)**:
    - 398.8 h, 1,010 episodes, 91 series: 56 new and 35 continued.
    - Busiest month: January, 47.8 h. Longest streak: 38 days, 16 Feb to 25 Mar.
    - Most watched series: Fire Force (60 episodes).
    - Top genres: Fantasy 26.7%, Action 21.7%. Top studios: David Production, Toei Animation,
      LIDENFILMS.
    - Coverage 96.9%.

    These match the SQL prototype in research R4 and R5.
  - **Routes**: `/u/jjchiw/year` redirects to 2026, `/year/2019` to `/year`, and `/year/1999` and
    `/year/abc` return 404.
  - **Timing (SC-003)**: about 30 ms once warm.
  - **Screens**: 1440px in English and 360px in Spanish. The 360px pass found the 12 month labels
    overflowing; phones now show one letter per month, and the Spanish abbreviations lose their dot.
  - **impeccable detector**: two findings, both pinned by `DESIGN.md`: the secondary → primary
    share-bar gradient and Plus Jakarta Sans. Nothing else, and nothing in the source.
