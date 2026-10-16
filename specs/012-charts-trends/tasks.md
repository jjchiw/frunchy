# Tasks: Charts and Trends

**Input**: [charts-trends.spec.md](charts-trends.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/charts.md](contracts/charts.md), [quickstart.md](quickstart.md)

**Tests**: required (constitution V). Fix "now" with `TimeProvider` (see `createAppWith` and the DNA
pace tests) and set `watched_at` directly in SQL when a test needs exact dates.

**Delegation**: Sonnet agents implement; the main session reviews every diff and runs the suite.
Use the `oxpecker` skill for views and `impeccable` for UI.

## Phase 1: Setup

- [x] T001 Register empty modules:
  - in FrunchyBackend.fsproj: `Infrastructure/Charts.fs` after `Compatibility.fs`, and `Handlers/Charts/Charts.fs` after `Handlers/Users/Compare.fs`;
  - in the test project: `ChartsUnitTests.fs` and `ChartsTests.fs`, after the last test file.

  · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj

## Phase 2: Foundational (blocks every story)

- [x] T002 [P] Add `ix_watch_events_watched` to the schema and to `SCHEMA.md` (research R3) · Infrastructure/Database.fs, SCHEMA.md
- [x] T003 [P] `ChartsUnitTests`:
  - **`seasonOf`**: every quarter and the boundaries (31 Mar 23:59:59Z → Winter; 1 Apr 00:00Z → Spring), in UTC.
  - **`minPeople`**: missing → 3; 0, 1 and negative → 2; 5 → 5.
  - **The movement function**: previous rank map plus current rank → Up n, Down n, Same or New.
  - **The 8 trend blocks**: their bounds, oldest first.

  · tests/FrunchyBackend.Tests/ChartsUnitTests.fs
- [x] T004 [P] Texts, en and es:
  - the nav link "Charts";
  - the page title;
  - the tabs: "This week", "This season", "All time";
  - the season label (reuse `DnaSeason` when it fits);
  - the row line "{n} people · {m} episodes", with plurals;
  - movement: "Up {n}", "Down {n}", "Same", "New";
  - the trend's accessible label "Last 8 weeks: {values}";
  - the latest-watch note and the minimum note "Series appear once {n} people have watched them";
  - the empty state;
  - "Watched by {n} people on Frunchy";
  - "Watch on Crunchyroll" (reuse it if it exists).

  · Views/Texts.fs
- [x] T005 `Infrastructure/Charts.fs`, per research R1, R2, R5 and the data-model shapes:
  - the pure functions, so T003 passes;
  - `chart` (the window statement, the previous-week statement for movement, the trend statement);
  - `seriesPeople` and `episodePeople`.

  The SQL is literal and parameterised, the minimum is in `HAVING`, and the viewer is only a parameter for `mine` · Infrastructure/Charts.fs

## Phase 3: User Story 1 - The charts (P1) 🎯 MVP

- [x] T006 [US1] `ChartsTests`, chart part. The fixture is 6 users with overlapping series and dates set across the last 20 weeks, the current season and earlier seasons. It checks:
  - **Figures**: every row, rank, people and episodes count for week, season and all time equals an independent query.
  - **Minimum**: for minimums 2–5, no row below it.
  - **Ties**: ordered by episodes, then title.
  - **Links**: `mine` only for a signed-in viewer who watched the series; Crunchyroll only with a slug.
  - **States**: an empty chart; a season name at a boundary date.
  - **Navigation**: the link on the home page, a dashboard and the charts page.
  - **Languages**: en and es.

  · tests/FrunchyBackend.Tests/ChartsTests.fs
- [x] T007 [US1] The page and its navigation:
  - **`Handlers/Charts/Charts.fs`**: view, handler and `GET` for the three routes. It reads the minimum from configuration and "now" from `TimeProvider`.
  - **`Layout.fs`**: the "Charts" link for everyone (`data-nav="charts"`).
  - **`Program.fs`**: the endpoints.
  - **Design**: impeccable shape → build in Night Broadcast (rank in mono, thumbnail, title, counts, solid amber bar, tabs with `aria-current`).

  · Handlers/Charts/Charts.fs, Views/Layout.fs, Program.fs

## Phase 4: User Story 2 - Movement and trend (P2)

- [x] T008 [US2] `ChartsTests`, movement and trend: with two weeks of known ranks, check up, down, same and new (including a series that was below the minimum last week, which counts as new), and each row's 8 trend values against a query. The 2M-row speed test for all three charts also goes here (SC-003) · tests/FrunchyBackend.Tests/ChartsTests.fs
- [x] T009 [US2] Show movement (icon plus number, `data-move`, `data-move-by`) and the inline SVG trend (`data-trend`, `role="img"`, `aria-label`) on the week chart (research R6). Add icon cases to `Theme.Icon` only if needed, keeping the subset test green · Handlers/Charts/Charts.fs, Views/Theme.fs

## Phase 5: User Story 3 - People counts (P2)

- [x] T010 [US3] `ChartsTests`, counts: the anime and episode pages show `data-people-count` equal to a query (the episode count covers the dub group) only at or above the minimum, and are absent below it · tests/FrunchyBackend.Tests/ChartsTests.fs
- [x] T011 [US3] Add the count line to the anime page (`Charts.seriesPeople`) and the episode page (`Charts.episodePeople` with the group ids already computed) · Handlers/Users/Anime.fs, Handlers/Episodes/Episode.fs

## Phase 6: Polish

- [ ] T012 [P] Add the three chart pages to the every-page design-system check, with their hooks · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] T013 [P] Docs:
  - **CLAUDE.md**: a Routes bullet, the `Frunchy:Charts:MinPeople` setting, and `Infrastructure/Charts.fs`.
  - **FRONTEND.md**: the page.
  - **README.md**: roadmap row 012 and the tree line.

  · CLAUDE.md, FRONTEND.md, README.md
- [ ] T014 Run an impeccable audit of the three charts and the two count lines at 1280 and 360, en and es, with real data rendered through the test harness. Then run the full `dotnet test` and append the validation log, including the measured times · specs/012-charts-trends/quickstart.md

## Dependencies

- **Order**: T001 → (T002 ∥ T003 ∥ T004) → T005 → T006 → T007 → T008 → T009 → T010 → T011 → (T012 ∥ T013) → T014.
- **Shared files**: `ChartsTests.fs` is split by part (T006, T008, T010). `Charts.fs` is touched by T007 and T009.
- **MVP**: Phases 1–3.
