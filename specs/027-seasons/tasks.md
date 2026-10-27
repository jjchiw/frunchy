# Tasks: Seasons

**Input**: [seasons.spec.md](seasons.spec.md), [plan.md](plan.md), [contracts/hooks.md](contracts/hooks.md),
[design-brief.md](design-brief.md)

**Tests**: required; `data-*` hooks only; the full suite stays green.

## Phase 1: Air dates (US4)

- [ ] T001 Parser: `AiredAt` from `panel.episode_metadata.episode_air_date`; parser tests (present, absent, invalid)
  · Infrastructure/CrunchyrollPage.fs, tests
- [ ] T002 Schema: `aired_at` in `CREATE TABLE cr_episodes`; the guarded upgrade with the backfill from stored pages
  (contract); `SCHEMA.md`; `SchemaUpgradeTests` (previous shape + a stored page → dated; second run no-op; newest
  capture wins); `SchemaBootstrapTests` still pass · Infrastructure/Database.fs, SCHEMA.md, tests
- [ ] T003 Import: `upsertEpisode` writes `aired_at` under the `source_at` rule; tests: import sets it, order
  independence and `user-rebuild` equality still hold · Workers/ImportProcessor.fs, tests
- [ ] T004 `Infrastructure/Seasons.fs`: airing season of an instant (UTC quarter), key `fall-2026`, ordering, named
  constants `LongRunnerEpisodes = 100`, `LongRunMinimum = 24`, `WhileAiringDays = 7`; `Charts.seasonOf` uses it;
  unit tests · Infrastructure/Seasons.fs, Infrastructure/Charts.fs, FrunchyBackend.fsproj, tests
- [ ] T005 Fixtures: synthetic pages can carry `episode_air_date` and episode numbers above 100 · tests/Fixtures.fs
- [ ] T006 Commit 1: `feat(027): store each episode's air date, filled in from the stored pages`

## Phase 2: Figures and UI (US1, US2, US3)

- [ ] T007 DNA statement and fold: watched seasons; studio/genre (and genre top series)/decade/format by watched
  seasons with coverage; long runners; flagship without them; the long-run row; the seasons rows; F# fold (top
  season, newest 8, earlier count) · Handlers/Users/Dna.fs
- [ ] T008 DNA views: the taste cards' new values and coverage copy; flagship absent case; the long-run card; "Your
  seasons" (existing components: figureSection, poster, seriesLink, chips); every hook in the contract; texts in
  en and es (`Views/Texts.fs`), season names via the existing `Texts` season formatter · Handlers/Users/Dna.fs,
  Views/Texts.fs, Views/Components.fs if a helper is shared
- [ ] T009 Year page: the same counting rule and hooks for its studio/genre/decade/format figures · Handlers/Users/Year.fs
- [ ] T010 Tests for US1–US3 scenarios (spec), both languages, empty states, the DNA = dashboard totals test still
  green; update existing DNA/year expectations whose shares change (say which and why in the commit)
  · tests/FrunchyBackend.Tests/DnaSeasonsTests.fs (new), DnaTests.fs, YearTests.fs
- [ ] T011 Commit 2: `feat(027): taste figures count seasons; flagship and long run; your seasons`

## Phase 3: Docs and snapshots

- [ ] T012 `CLAUDE.md` (DNA route line: watched seasons, long runners, the seasons section), CHANGELOG Unreleased
  (Added: Your seasons; Changed: taste figures by seasons, flagship and long-run card; Upgrade: air dates filled in
  automatically on start, nothing to run). PageSnapshots: make the snapshot user's history show a long runner and
  several airing seasons if it doesn't already (One Piece-like episodes numbered above 100) · CLAUDE.md,
  CHANGELOG.md, tests/FrunchyBackend.Tests/PageSnapshots.fs
- [ ] T013 Commit 3: `docs(027): seasons`

## Phase 4: Review and design (main session)

- [ ] T014 Review, suite, screenshots to the designer; design fixes; spec Implemented.
