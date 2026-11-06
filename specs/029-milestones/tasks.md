# Tasks: Milestones on the dashboard

**Input**: [milestones.spec.md](milestones.spec.md)

**Tests**: `data-*` hooks only, never classes. The full suite must stay green.

- [x] T001 `Infrastructure/Milestones.fs` (FR-001, FR-002): track definitions, `WatchRow`, `Track`, pure `compute`,
  `load` (one literal SQL statement, `@user_id`). Add to `FrunchyBackend.fsproj` in the Infrastructure section
  · Infrastructure/Milestones.fs, FrunchyBackend.fsproj
- [x] T002 Texts `en` and `es` (FR-005), icons in `Theme.Icon` if needed (FR-006)
  · Views/Texts.fs, Views/Theme.fs
- [x] T003 Dashboard: model field, query after the gate and only with figures, the section and its hooks (FR-003,
  FR-004, FR-006), through the `impeccable` craft floor
  · Handlers/Users/Dashboard.fs
- [x] T004 `MilestonesUnitTests` (pure, no Postgres): each track's value, tier, next and date; thresholds exact
  (9 vs 10 episodes, 23.99 vs 24 hours in ms); hours counts partial playhead, the others don't; streak across a
  gap and two runs (the date is the first run's N-th day); marathon by UTC day (23:30 and 00:30 UTC are two days);
  long-run with a dub and its original counted once and the named series by most keys then title then id; the date
  ordering ties by episode id; empty input → six tracks, value 0, no tier
  · tests/FrunchyBackend.Tests/MilestonesUnitTests.fs, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T005 `MilestonesTests` (Postgres): the 2022 sample's `episodes`/`hours`/`series` card values equal the stat
  tiles (`data-stat` values); expected tiers for the sample; re-sent and reordered imports and `user-rebuild` give an
  identical `data-milestones` section; empty history → no section; private profile → the 404 body unchanged;
  Spanish text; `long-run` links the series (`data-series-link`)
  · tests/FrunchyBackend.Tests/MilestonesTests.fs, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T006 `contracts/hooks.md`; CLAUDE.md line under the dashboard route; `changelog.d/029-milestones.md` (Added);
  `.github/scripts/changelog.sh check`; full `dotnet test`
  · specs/029-milestones/contracts/hooks.md, CLAUDE.md, changelog.d/029-milestones.md

## Notes from implementation

- The DNA key joins `cr_season_matches` with `m.status = 'matched'`, exactly as `Dna.fs` and `Fediverse.fs` do (the spec
  said "any status" but also "match how they join"; the DNA join was followed so a dub groups the same way everywhere).
  The key falls back to the episode id when the episode has no number or no season, like `Dna.fs`.
- `streak` `ReachedAt` is the first fully watched watch of the run's N-th UTC day (a real instant), not a bare date, so
  the `<time data-fr-time>` localizes sensibly (a midnight-UTC instant would show the previous evening in the Americas).
- The card shows a value line for every track ("96 episodes", "16 days in a row", "31 episodes of <series>"), which is
  where the singular forms are used; progress to the next tier is `value / next`.
- Icons added to `Theme.Icon`: `Bolt`, `Layers`, `Movie`, `VideoLibrary`.
- 2022 sample (96 episodes, 39.3 hours, 29 series): episodes 50 (next 100), hours 24 (next 100), series 25 (next 50),
  streak 16 days = tier 14 (next 30), marathon 22 = tier 12 (next 24), long-run 31 episodes of Bungo Stray Dogs = tier 24
  (next 50); 12 of 36 reached.
