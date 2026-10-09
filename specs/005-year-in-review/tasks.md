# Tasks: Year in Review

**Input**: [year-in-review.spec.md](year-in-review.spec.md), [plan.md](plan.md),
[research.md](research.md), [data-model.md](data-model.md), [contracts/year-page.md](contracts/year-page.md)

**Tests**: required by constitution V. Integration tests run against real Postgres with the 2026
sample and the feature 003 MyAnimeList fixtures, on a fixed clock. Expected figures are computed
in the test, independently of the slice's SQL.

**Every UI task**: load the `oxpecker` skill, run impeccable (`.claude/skills/impeccable/`)
shape → build → audit, texts only from `Views/Texts.fs` (en + es), test hooks exactly as in
`contracts/year-page.md`.

## Phase 1: Setup

- [x] **T001** Register `Handlers/Users/Year.fs` after `Dna.fs` in the app project, and `YearTests.fs` and `YearNavigationTests.fs` after `DnaPaceTests.fs` in the test project, as minimal modules; `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj, Handlers/Users/Year.fs, tests/FrunchyBackend.Tests/YearTests.fs, tests/FrunchyBackend.Tests/YearNavigationTests.fs

## Phase 2: Foundational (blocks both stories)

**Wave 1, independent (different files):**

- [x] **T002** [P] Add the year texts in `en` and `es` (research R9): title, heading with the display name, rewatch note, section titles (top series, months, new versus continued, streak), "busiest month" phrasing, "new" / "continued" counts, streak length and its date range, the 12-month chart's accessible label, the empty state with its count out of 15, the picker label, and the link labels to and from the page · Views/Texts.fs
- [x] **T003** [P] Add `CalendarMonth` and `LocalFireDepartment` to `Theme.Icon` with their ligature names · Views/Theme.fs

**⟶ Wait for Wave 1 to finish, then:**

- [x] **T004** Move `section`, `sectionHeading`, `coverageNote`, `notEnough`, `shareLine`, `inv` and `pctText` from `Dna.fs` into `Components.fs` unchanged, and add `yearPicker` (year links as pills, `aria-current="page"` on the shown year, `data-year-picker` / `data-year-option`) · Views/Components.fs

**⟶ Wait for T004, then:**

- [x] **T005** Use the moved helpers in `Dna.fs` (behaviour unchanged, `DnaTests` green), and add the header link to `/u/{username}/year` with `data-year-link` · Handlers/Users/Dna.fs

**Checkpoint**: build at 0 warnings, the existing suite green, the DNA page unchanged except for the new link.

## Phase 3: User Story 1 - My year in anime (Priority: P1) 🎯 MVP

Files: Handlers/Users/Year.fs, Program.fs, tests/FrunchyBackend.Tests/YearTests.fs

### Tests

- [ ] **T006** [US1] Write `YearTests` to fail first, on the 2026 sample with the feature 003 fixtures and a fixed clock (contract `year-page.md`):
  - every figure for a sample year against an independent F# count over raw rows: totals, top series, months and the busiest one, top genres plus Others (summing to 100), top 3 studios, formats, new versus continued, streak with its dates;
  - SC-002: every year's hours add up to the dashboard's, within 0.1 per year;
  - coverage drops when a season is unmatched, and with none matched each MyAnimeList section has `data-not-enough`;
  - a year with 14 fully watched episodes is empty with `data-value` 14, and 15 shows the figures;
  - unknown user, non-numeric year, year 1999 and next year are 404;
  - en and es (month names localized, MyAnimeList names not);
  - identical figures after `user-rebuild` and a re-send;
  - no MyAnimeList request while viewing;
  - the rewatch note is present.

  · tests/FrunchyBackend.Tests/YearTests.fs

### Implementation

- [ ] **T007** [US1] Data section: the user lookup, the years-with-history query, and one statement per research R2–R6. That's totals (undeduplicated), deduplicated MyAnimeList figures, top series with its poster, 12 months, the streak (UTC days, `d - row_number()`) and new versus continued (the earliest held watch per series). Year bounds and `@now` come from `TimeProvider`, and values go only as parameters · Handlers/Users/Year.fs
- [ ] **T008** [US1] View, handler and endpoints:
  - **Header**: the heading, the rewatch note, links to the dashboard and the DNA page, and the copy link.
  - **Picker**.
  - **Totals tiles**.
  - **Top series card**.
  - **12-month chart**: bars with `role="img"`, month names from `t.Culture`, the busiest month marked.
  - **Genre, studio and format sections**, with coverage.
  - **New versus continued** and the **streak**.
  - **Empty state**.
  - **Routes**: `GET /u/{username}/year` redirects to the newest year with history, or the current year. `GET /u/{username}/year/{year}` validates the year and redirects a year without history (other than the current one) to the address without a year.
  - **Program.fs**: register the endpoints after the DNA endpoint.
  - **impeccable**: shape → build → audit.

  · Handlers/Users/Year.fs, Program.fs

**⟶ Wait for T008, then:**

- [ ] **T009** [US1] Run `YearTests` and the suite, then check `/u/jjchiw/year/2025` on the real database: totals equal the 2025 slice of the dashboard rule (398.8 h, 1,010 episodes, 91 series), ready in under 1 s, at 1440 and 360px, in en and es · Handlers/Users/Year.fs

**Checkpoint**: the year page works on its own.

## Phase 4: User Story 2 - Move between years and pages (Priority: P2)

Files: Handlers/Users/Dashboard.fs, tests/FrunchyBackend.Tests/YearNavigationTests.fs

### Tests

- [ ] **T010** [US2] Write `YearNavigationTests`:
  - the dashboard and the DNA page link to `/u/{username}/year`;
  - `/u/{username}/year` redirects to the newest year with history, and to the current year for a user without history;
  - a year with no history redirects to `/u/{username}/year`;
  - the picker lists exactly the years with history, newest first, with `aria-current` on the shown one.

  · tests/FrunchyBackend.Tests/YearNavigationTests.fs

### Implementation

- [ ] **T011** [US2] Add the year link (`data-year-link`) to the dashboard header next to the DNA link; view only · Handlers/Users/Dashboard.fs

**Checkpoint**: a viewer reaches any year from the dashboard in 2 clicks (SC-004).

## Phase 5: Polish

**Wave 1, independent (different files):**

- [ ] **T012** [P] Add the year page to the every-page design-system check with its hooks · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] **T013** [P] Add a "Year in review" prompt to the designer brief, following section 5.2's format and the shared context block · design/BRIEF.md
- [ ] **T014** [P] Document the route, the data notes and the roadmap entry (005) · README.md, CLAUDE.md, FRONTEND.md, SCHEMA.md

**⟶ Wait for Wave 1 to finish, then:**

- [ ] **T015** Run the full `dotnet test` with 0 build warnings, and validate against the Success Criteria (SC-001 to SC-005). Record the real-data figures, timing and audit in a new validation log · specs/005-year-in-review/quickstart.md

## Dependencies & Execution Order

- **Order**: Setup → Foundational → US1 → US2 → Polish. US2's tests need US1's routes.
- **Foundational**: T002 and T003 in parallel, then T004 (needs T003's icons and T002's texts for the picker), then T005.
- **US1**: T006, then T007, then T008, then T009.
- **US2**: T010, then T011.
- **Polish**: T012, T013 and T014 in parallel, then T015.
