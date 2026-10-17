# Tasks: Public Series Page

**Input**: [series-page.spec.md](series-page.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/series-page.md](contracts/series-page.md), [quickstart.md](quickstart.md)

**Tests**: required (constitution V). Integration tests on real Postgres with the 2026 sample and the
003 fixtures (`AnimeTests.withSample` shows how). Fix "now" with `TimeProvider` for the weekly count.

**Delegation**: Sonnet agents implement; the main session reviews every diff and runs the suite.
Use the `oxpecker` skill for views and `impeccable` for UI.

## Phase 1: Setup and foundation

- [x] T001 Register the new files:
  - in FrunchyBackend.fsproj, `Handlers/Series/Series.fs` after `Handlers/Charts/Charts.fs`;
  - in the test project, `SeriesPageTests.fs` and `SeriesLinkTests.fs` after the last test file.

  · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T002 [P] Move `malDetails` from `Handlers/Users/Anime.fs` to `Views/Components.fs`. Its output is identical, and AnimeTests and DesignSystemTests stay green unchanged · Views/Components.fs, Handlers/Users/Anime.fs
- [x] T003 [P] Texts, en and es:
  - the page title;
  - "{n} known episodes" (plural);
  - "Watched by {n} people this week" (plural);
  - "Other episodes" (reuse `AnimeOtherEpisodes`);
  - the known-episodes note (reuse the anime page's wording if one exists);
  - "Your history";
  - the series-page link text ("About this series");
  - a comment-count label "{n} comments" (plural, for `aria-label`);
  - `UsernameHint` updated to say the name ends in a letter, digit or _.

  · Views/Texts.fs
- [x] T004 [P] `Charts.seriesPeopleSince db seriesId (from: DateTimeOffset) minPeople : Task<int option>`: distinct users with a watched episode of known length at or after `from`, None below the minimum. Add unit or smoke tests for it · Infrastructure/Charts.fs, tests/FrunchyBackend.Tests/ChartsTests.fs

## Phase 2: User Story 1 - The page (P1) 🎯 MVP

- [x] T005 [US1] `SeriesPageTests`, every bullet of the quickstart row, against independent SQL:
  - the header;
  - seasons and their order;
  - MyAnimeList shown once per entry, and the unmatched line;
  - episodes, their order, links and comment counts (visibility: public or unlisted, not deleted; hidden ones count);
  - both people counts at and below the minimum;
  - the 404s;
  - seasonless and numberless episodes;
  - a movie without a series row;
  - the collapse above 30 episodes;
  - 1,100 synthetic episodes under 1 s;
  - en and es.

  · tests/FrunchyBackend.Tests/SeriesPageTests.fs
- [x] T006 [US1] `Handlers/Series/Series.fs`:
  - **The slice**: view, the body statement (research R2), the poster query, the handler (id check before any query, then a 404) and `GET [ routef "/anime/{%s}" … ]`.
  - **`Program.fs`**: the endpoint.
  - **Design**: impeccable shape → build in Night Broadcast, reusing `Components.seasonNameLine`, `malDetails` and `malCompact`.

  · Handlers/Series/Series.fs, Program.fs

## Phase 3: User Story 2 - Links (P2)

- [ ] T007 [US2] `SeriesLinkTests`:
  - the chart row title links to the series page for everyone, and "Your history" (`data-chart-mine`) shows only for a viewer who watched the series;
  - the episode page's series title links to the series page;
  - the anime page links to the series page.

  Update the 012 tests that read `data-chart-mine` on the title to its new element · tests/FrunchyBackend.Tests/SeriesLinkTests.fs, tests/FrunchyBackend.Tests/ChartsTests.fs
- [ ] T008 [US2] Add the links, per research R5 · Handlers/Charts/Charts.fs, Handlers/Episodes/Episode.fs, Handlers/Users/Anime.fs

## Phase 4: User Story 3 - Usernames (P2)

- [x] T009 [US3] Tests:
  - sign-up refuses `abc-` and creates nothing; it accepts `abc_` and `a-b`;
  - `user-create abc-` exits with the bad-username code and creates nothing;
  - a user inserted with `abc-` keeps a working dashboard and is found by `operator-add`;
  - unit tests for `tryCreateNew` and `tryCreate` side by side.

  · tests/FrunchyBackend.Tests (the existing sign-up, CLI and username test files)
- [x] T010 [US3] Add `Username.tryCreateNew` and use it in sign-up and `user-create` · Domains/Identity.fs, Handlers/Auth/SignUp.fs, Operations/CreateUser.fs

## Phase 5: Polish

- [ ] T011 [P] Add the series page to the every-page design-system check with its hooks · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] T012 [P] Docs:
  - **CLAUDE.md**: the route, the links and the username rule.
  - **FRONTEND.md**: the page.
  - **README.md**: roadmap 013 and the tree line.
  - **docs/fediverse.md**: the series profile URL is `/anime/{seriesId}`; the username ending rule is done; Megáfono 003 and 004 are on its main.

  · CLAUDE.md, FRONTEND.md, README.md, docs/fediverse.md
- [ ] T013 Run an impeccable audit of the series page (a multi-season series with a dub, a long series, the not-enough state), the changed chart row and the episode and anime headers, at 1280 and 360, en and es. Then run the full `dotnet test` and append the validation log · specs/013-series-page/quickstart.md

## Dependencies

- **Order**: T001 → (T002 ∥ T003 ∥ T004) → T005 → T006 → T007 → T008 → T009 → T010 → (T011 ∥ T012) → T013.
- **US3** (T009–T010) is independent of US1 and US2 and can run in parallel in another agent; it touches different files.
