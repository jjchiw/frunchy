# Tasks: Progress

**Input**: [progress.spec.md](progress.spec.md), [contracts/hooks.md](contracts/hooks.md), [design-brief.md](design-brief.md)

**Tests**: required; `data-*` hooks only; the full suite stays green.

## Phase 1: Data (US1, US2)

- [x] T001 `Infrastructure/Progress.fs` (after `Seasons.fs` in the fsproj): the constants, the entry record, a pure
  `classify now entries` (states, order, the completed cap), and `load connection userId now`: **one literal SQL
  statement** over the person's rows (dedup like Dna.fs, per-entry aggregates, long-runner flag, the next episode per
  entry, posters like Dna.fs's `series_art`, the unmatched count); parameters only (SqlGuardrailTests)
  · Infrastructure/Progress.fs, FrunchyBackend.fsproj
- [x] T002 Unit tests of `classify` (every rule and boundary: 30 days, 21 days, at-least total, unknown total, airing,
  long runner, order, cap) · tests/FrunchyBackend.Tests/ProgressUnitTests.fs

## Phase 2: The page (US1, US2, US3)

- [x] T003 `ProfileTab.Progress` and the fourth tab in `Components.profileTabs` (`data-tab="progress"`,
  `data-progress-link`, `t.ProgressLink`) · Views/Components.fs
- [x] T004 `Handlers/Users/Progress.fs` (vertical slice, after Year.fs in the fsproj; registered in Program.endpoints):
  username, lookup, `Profiles.gate` (SignInFirst → `WebAuth.redirectToSignInHere`, NotFound → the unknown-user 404),
  then `Progress.load`; the owner view (counts, lists, next episode, unmatched note, empty and all-caught-up states)
  and the visitor view (counts and a line, nothing else); `profileTop` with `ProfileTab.Progress`; first pass with
  existing components, working at 360 px · Handlers/Users/Progress.fs, Program.fs, FrunchyBackend.fsproj
- [x] T005 Texts en and es (`Progress*`) · Views/Texts.fs
- [x] T006 Tests on the real app (`tests/FrunchyBackend.Tests/ProgressTests.fs`): the user-story scenarios with
  synthetic pages and the scripted MyAnimeList catalog (MalFixtures), dub and original once, entries split across
  Crunchyroll seasons, the next-episode links, the visitor view has no list/entry/next hook and no started title,
  the gate (private 404 byte-identical, members redirect, operator not owner sees counts only), the tab on every
  profile page, both languages, order-independence (re-send and reorder give the same page)
- [x] T007 Commit 1: `feat(031): a Progress tab: completed, watching and unfinished shows`

## Phase 3: Docs and snapshots

- [ ] T008 CLAUDE.md route line; `changelog.d/031-progress.md` (Added); PageSnapshots shoots `progress` (owner) and
  `progress-visitor`, with the snapshot user having unfinished, watching and completed entries and a long runner
  · CLAUDE.md, changelog.d/031-progress.md, tests/FrunchyBackend.Tests/PageSnapshots.fs
- [ ] T009 Commit 2: `docs(031): progress`

## Phase 4: Review and design (main session)

- [ ] T010 Review, suite, screenshots to the designer, design fixes, spec Implemented.
