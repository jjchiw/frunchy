# Tasks: Taste Compatibility

**Input**: [taste-compatibility.spec.md](taste-compatibility.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/compatibility.md](contracts/compatibility.md), [quickstart.md](quickstart.md)

**Tests**: required (constitution V). Unit tests cover the score; integration tests use real
Postgres with the 2026 sample and the 003 fixtures (`AnimeTests.withSample` shows how).

**Delegation**: Sonnet agents implement; the main session reviews every diff and runs the suite.
Use the `oxpecker` skill for views and `impeccable` for UI.

## Phase 1: Setup

- [x] T001 Register empty modules:
  - `Infrastructure/Compatibility.fs` after the last `Infrastructure` module, and `Handlers/Users/Compare.fs` after `Anime.fs`, both in FrunchyBackend.fsproj;
  - the tests `CompatibilityScoreTests.fs` and `CompatibilityTests.fs` after the last test file, in the test project.

  `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj

## Phase 2: Foundational (the computation; blocks both stories)

- [x] T002 [P] `CompatibilityScoreTests`, unit tests, no database:
  - **Bands**: the level at 0, 0.1999, 0.2, 0.3999, 0.4, 0.6, 0.7999, 0.8, 1.
  - **Cosine**: identical vectors give 1, disjoint ones 0, and a known pair its expected value.
  - **Shared series**: S is measured against the smaller history; an empty side gives 0.
  - **Fallback**: the score is S alone when either genre map is empty.
  - **Symmetry**: on 200 seeded random pairs, `score a b = score b a`.
  - **Shared genres**: genres in both top 5, ordered by summed share, up to 3.
  - **Genre rows**: the union of the top 4, up to 6.

  · tests/FrunchyBackend.Tests/CompatibilityScoreTests.fs
- [x] T003 [P] Texts, en and es:
  - the card title "You and {name}";
  - the level names and "{level} compatibility";
  - the meter's accessible label "{level}: {n} of 5";
  - "{n} series in common", with plurals, and "No series in common yet";
  - the summary sentence, with and without a genre (FR-010);
  - the section titles: "Where your time goes", "Series you've both watched", "In {name}'s history, not yours", "Studios you share";
  - "and {n} more";
  - "{n} series" per studio;
  - the coverage note per person;
  - the not-enough states (viewer, owner, both);
  - "See how you compare";
  - one plain sentence explaining the level (shared series plus how alike your genre time is);
  - the page title.

  · Views/Texts.fs
- [x] T004 `Infrastructure/Compatibility.fs` (research R1–R4, data-model shapes):
  - **Pure functions**: `cosine`, `sharedSignal`, `score`, `level`, `sharedGenres`, `genreRows`, and the section builders.
  - **`load db viewer owner`**: statement 1 for both users (`= ANY(@users)`), statement 2 for the owner's series the viewer hasn't watched (top 8 ids, MyAnimeList score and up to 3 genres of each series' most-watched matched season).
  - **SQL**: literal, with parameters; it reuses the DNA page's dedup CTE shape (copy it; don't call Dna.fs's private code).

  Make T002 pass · Infrastructure/Compatibility.fs

## Phase 3: User Story 1 - The card (Priority: P1) 🎯 MVP

- [x] T005 [US1] `CompatibilityTests`, card part:
  - **Fixtures**: two users from the sample (A gets the whole sample; B gets a subset plus synthetic series), with 003 enrichment.
  - **Figures**: every card hook equals an independent SQL computation.
  - **Same level**: both sides see the same level.
  - **No card**: signed out, or on your own dashboard.
  - **Not enough history**: viewer, owner, both.
  - **No series in common.**
  - **Languages**: en and es.

  · tests/FrunchyBackend.Tests/CompatibilityTests.fs
- [x] T006 [US1]:
  - **`Components.levelMeter`**: 5 segments, `role="img"`, `aria-label`.
  - **`Components.shareBar`** gains a tone (primary | secondary); existing calls keep primary.
  - **The card**: in `Dashboard.fs`, only when `WebAuth.currentUserId` is set and isn't the owner. It uses `Compatibility.load`, the contract's hooks, and a link to the compare page.
  - **Design**: impeccable shape → build for the card, in Night Broadcast (amber for the viewer, rose for the owner, lilac tags).

  · Views/Components.fs, Handlers/Users/Dashboard.fs

## Phase 4: User Story 2 - The compare page (Priority: P1)

- [x] T007 [US2] `CompatibilityTests`, page part:
  - **Figures**: every page hook equals an independent query (summary, genre rows with both shares and coverage, shared series with both episode counts and "more", their series with MyAnimeList score, shared studios with series counts).
  - **Access**: signed out goes to sign-in with returnUrl; self redirects to the dashboard; unknown and invalid usernames get 404.
  - **Not enough history.**
  - **Speed**: 2,000 synthetic episodes each, under 1 s after a warm-up.
  - **Card to page**: the card's link opens the page.
  - **Languages**: en and es.

  · tests/FrunchyBackend.Tests/CompatibilityTests.fs
- [x] T008 [US2] `Handlers/Users/Compare.fs`:
  - **Slice**: view, handler and `GET [ routef "/u/{%s}/compare" … ]`.
  - **Order** (research R5): `WebAuth.requireSignedIn`, then the username, then the lookup, then self, then load.
  - **Program.fs**: add the endpoint.
  - **Design**: impeccable shape → build, from pitch B with C's heading. Genre rows put each person on their own line, in amber and rose. No percentage or score anywhere.

  · Handlers/Users/Compare.fs, Program.fs

## Phase 5: Polish

- [ ] T009 [P] Add the dashboard with the card (as a signed-in non-owner) and the compare page to the every-page design-system check, with the contract's hooks · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] T010 [P] Docs:
  - **CLAUDE.md**: a Routes bullet for 011, covering the route, the card's conditions, `Infrastructure/Compatibility.fs`, the score and the levels.
  - **FRONTEND.md**: the page.
  - **README.md**: roadmap row 011.

  · CLAUDE.md, FRONTEND.md, README.md
- [ ] T011 Run an impeccable audit of the card and the page (1280 and 360, en and es, with real sample data), the full `dotnet test`, then append the validation log · specs/011-taste-compatibility/quickstart.md

## Dependencies

- **Order**: T001 → (T002 ∥ T003) → T004 → T005 → T006 → T007 → T008 → (T009 ∥ T010) → T011.
- **Shared files**: `Components.fs` (T006) and `Dashboard.fs` (T006); `CompatibilityTests.fs` is split into its card part (T005) and page part (T007).
- **MVP**: Phases 1–3. US2 follows straight after; the card's link needs the page.
