# Tasks: Anime Page

**Input**: [anime-page.spec.md](anime-page.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/anime-page.md](contracts/anime-page.md)

**Tests**: required by constitution V. Integration tests run against real Postgres, using the
2026 sample and the feature 003 MyAnimeList fixtures. Expected figures are computed in the test,
independently of the slice's SQL.

**Every UI task**: load the `oxpecker` skill and the `impeccable` skill (shape → build → audit).
Texts come only from `Views/Texts.fs` (en + es). Test hooks are exactly the ones in
`contracts/anime-page.md`.

**Delegation**: story phases are built by Sonnet subagents. The main session reviews each diff and
runs the tests before merging.

## Phase 1: Setup

- [x] **T001** Register `Handlers/Users/Anime.fs` after `Year.fs` in the app project, and `AnimeTests.fs` and `AnimeLinkTests.fs` after `YearNavigationTests.fs` in the test project, as minimal modules; `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj, Handlers/Users/Anime.fs, tests/FrunchyBackend.Tests/AnimeTests.fs, tests/FrunchyBackend.Tests/AnimeLinkTests.fs

## Phase 2: Foundational (blocks both stories)

**Wave 1, independent (different files):**

- [x] **T002** [P] Add the anime page texts in `en` and `es` (research R4–R6):
  - page title, rewatch note, first and latest watch labels, "Watch on Crunchyroll";
  - "of N known episodes", "Other episodes", the dub mark, the studio label;
  - the MyAnimeList episode count, the three air statuses (with a fallback to the stored value);
  - `AudioLanguage: string -> string` (the locale map with a fallback to the code);
  - `TopAnimePrefix` ("Top:" / "Destacado:"), which replaces `DnaTopAnime`.

  · Views/Texts.fs
- [x] **T003** [P] In `Components.fs`:
  - `shareLine`'s `detail` becomes `HtmlElement option`;
  - add `seriesLink (username: string) (seriesId: string) (title: string)`: an `a` to `/u/{username}/anime/{seriesId}` with `fr-link` and `data-series-link` = the series id.

  · Views/Components.fs

**⟶ Wait for Wave 1 to finish, then:**

- [x] **T004** Make `Dna.fs` and `Year.fs` compile against the new `shareLine` and `TopAnimePrefix` with no visible change yet. A genre without a top series shows no "Top:" line, and "Others" is an explicit flag instead of `Top = None` plus the genre-name fallback (research R7). `DnaTests` and `YearTests` stay green · Handlers/Users/Dna.fs, Handlers/Users/Year.fs

**Checkpoint**: build at 0 warnings, the suite green.

## Phase 3: User Story 1 - What I watched of one anime (Priority: P1) 🎯 MVP

Files: Handlers/Users/Anime.fs, Program.fs, tests/FrunchyBackend.Tests/AnimeTests.fs

### Tests

- [x] **T005** [US1] Write `AnimeTests` against the contract: the 2026 sample with the feature 003 fixtures, expected values computed in F# from raw rows.
  - **Figures**: for a series with several seasons (JUJUTSU KAISEN), one with a dub (The Weakest Tamer: `GRK5CNJ14` and `GYDQCGK5Q`) and one unmatched, every total, season (order, watched, known, audio, dub, MyAnimeList id or missing) and episode (order, fully, percent) equals the expected value.
  - **Totals**: equal the dashboard's rule restricted to the series (SC-002).
  - **Not found**: 404 for an unknown user, a lowercase or 25-character id, and a series the user never watched.
  - **Crunchyroll link**: has the stored slug, and is absent when the slug is null.
  - **Languages**: en and es (audio language names localized, MyAnimeList names not).
  - **Stability**: identical hooks after `user-rebuild` and a re-send.
  - **No external calls**: no MyAnimeList request while viewing.

  · tests/FrunchyBackend.Tests/AnimeTests.fs

### Implementation

- [x] **T006** [US1] `Anime.fs`, following `Year.fs`'s layout:
  - **Model.**
  - **View**:
    - header: title, poster, Crunchyroll link, dashboard link, copy link, rewatch note;
    - totals tiles;
    - one card per season, with a `shareBar` for watched ÷ known, the MyAnimeList pills and link, or the missing line;
    - the episode list rows, with `Layout.timeElement`.
  - **Data**: the user lookup, then one statement (research R2–R5) with LEFT JOINs to `cr_series` and the matches. Values go only as parameters.
  - **Handler**: id validation before any query, then the 404s.
  - **Endpoint**: `GET [ routef "/u/{%s}/anime/{%s}" handler ]`, registered in `Program.fs` after the year endpoints.

  · Handlers/Users/Anime.fs, Program.fs

**⟶ Wait for T006, then:**

- [x] **T007** [US1] Run `AnimeTests` and the suite. Then check `/u/jjchiw/anime/GRMG8ZQZR` (One Piece) on the real database: 266 episodes and 106.4 h, ready in under 1 s, at 1440px and 360px, in en and es. Record it in the quickstart · Handlers/Users/Anime.fs

**Checkpoint**: the anime page works on its own.

## Phase 4: User Story 2 - Reach it from the other pages (Priority: P2)

Files: Handlers/Users/Dashboard.fs, Handlers/Users/Dna.fs (titles only), Handlers/Users/Year.fs (titles only), tests/FrunchyBackend.Tests/AnimeLinkTests.fs

### Tests

- [x] **T008** [US2] Write `AnimeLinkTests`. With the 2026 sample:
  - every recent card on the dashboard, and the header's newest episode, have a `data-series-link` to `/u/animenerd/anime/{id}`, where id is that card's series;
  - so do the DNA insignia and every genre top anime except "Others", and the year page's top series and its genre top anime;
  - each link answers 200.

  · tests/FrunchyBackend.Tests/AnimeLinkTests.fs

### Implementation

- [x] **T009** [US2] Dashboard: the recent query adds `e.series_id` as its last column, and `RecentEpisode` gains `SeriesId`. The card title and the header's newest-episode title use `seriesLink`. The figures and other hooks are unchanged · Handlers/Users/Dashboard.fs
- [x] **T010** [US2] DNA and year pages: the insignia and top series titles, and each genre's top anime, use `seriesLink`; view lines only · Handlers/Users/Dna.fs, Handlers/Users/Year.fs

**Checkpoint**: one click from the dashboard, the DNA page or the year page reaches an anime page (SC-004).

## Phase 5: Polish

**Wave 1, independent (different files):**

- [ ] **T011** [P] Add the anime page to the every-page design-system check · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] **T012** [P] Add the anime page to the designer brief (5.4, real data only) · design/BRIEF.md
- [ ] **T013** [P] Document the route, the data note and roadmap entry 006 · README.md, CLAUDE.md, FRONTEND.md, SCHEMA.md

**⟶ Wait for Wave 1 to finish, then:**

- [ ] **T014** Run the full `dotnet test` with 0 build warnings, and validate SC-001 to SC-005. Record the result in a new validation log · specs/006-anime-page/quickstart.md

## Dependencies & Execution Order

- **Order**: Setup → Foundational → US1 → US2 → Polish. US2's tests open the anime page, so they need US1.
- **Foundational**: T002 and T003 in parallel, then T004.
- **US1**: T005, then T006, then T007.
- **US2**: T008, then T009, then T010. `Dna.fs` and `Year.fs` are shared with T004 in Foundational, so T010 runs after it.
- **Polish**: T011, T012 and T013 in parallel, then T014.
