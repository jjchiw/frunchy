# Tasks: Watch streaks and a weekly recap

**Input**: [streaks-and-recap.spec.md](streaks-and-recap.spec.md)

**Tests**: `data-*` hooks only. The suite must stay green.

- [x] T001 `Infrastructure/Recap.fs` (after `Seasons.fs` in the fsproj):
  - `type Run = { From: DateOnly; To: DateOnly; Days: int }`, `type Streaks = { Current: int; Longest: Run option }`;
  - pure `ofRuns (today: DateOnly) (runs: Run list) : Streaks`: current = the length of the run whose `To` is `today`
    or `today - 1`, else 0; longest = the longest run, a tie to the latest `To`; `None` when there is no run;
  - `type Week = { Episodes: int64; Minutes: int64; Series: int64; New: int64; Continued: int64; Any: bool }`
    (`Any` = at least one watch event of known length in the window);
  - `weekWindow = TimeSpan.FromDays 7.0`;
  - `loadWeek (connection: NpgsqlConnection) (userId: Guid) (now: DateTimeOffset) : Task<Week>`, one statement
    (`@from = now - 7 days`, `watched_at >= @from`), the dashboard's rules; new = a series of the window with no
    watch event of the user on any episode of the series with `watched_at < @from`;
  - `loadStreaks (connection) (userId) (now: DateTimeOffset) : Task<Streaks>`, one statement: distinct
    `(watched_at AT TIME ZONE 'UTC')::date` of fully watched episodes of known length, islands by
    `day - row_number()`, rows `(min(day), max(day), count(*))`; then `ofRuns (DateOnly.FromDateTime now.UtcDateTime)`
  · Infrastructure/Recap.fs, FrunchyBackend.fsproj
- [x] T002 Dashboard: `DashboardModel` gets `Week: Recap.Week option` and `Streaks: Recap.Streaks option`, loaded in
  `tryLoad` after the gate and only when `Figures` is `Some` (same connection); `tryLoad` takes `now`
  (`ctx.GetService<TimeProvider>().GetUtcNow()`). View: between `stats` and the recent grid, a
  `grid grid-cols-1 gap-6 lg:grid-cols-12` with the recap card (`lg:col-span-8`) and the streaks card
  (`lg:col-span-4`), hooks of FR-004, plain UTC dates for the longest run, the UTC note. Texts en and es
  (`RecapTitle`, `RecapEpisodes`, `RecapTime: int64 -> string`, `RecapSeries`, `RecapNewContinued`, `RecapEmpty`,
  `RecapDaysAreUtc`, `StreaksTitle`, `StreakCurrent`, `StreakLongest`, `StreakDays: int -> string`,
  `StreakRange: DateOnly -> DateOnly -> string`, `StreakNone`, as needed)
  · Handlers/Users/Dashboard.fs, Views/Texts.fs
- [x] T003 Tests:
  - `RecapRulesTests` (pure): no runs; a run ending today; ending yesterday (alive); ending two days ago (0);
    longest tie goes to the latest; longest ≥ current;
  - `RecapTests` (Postgres, a fixed `TimeProvider`): episodes, minutes (a partial watch adds minutes, not an
    episode), series, new vs continued (a series watched before the window is continued, one first watched in it is
    new; an unresolved earlier event also makes it continued); an event older than 7 days is outside; the empty
    window; three consecutive UTC days up to yesterday give current 3, a gap gives 0 with the longest kept and its
    `from`/`to`; 23:30 and 00:30 UTC are two days; partial watches don't make a streak day; a private profile's
    visitor gets the 404 with no `data-recap`/`data-streaks`, the owner sees them; the empty dashboard has neither;
    Spanish
  · tests/FrunchyBackend.Tests/RecapRulesTests.fs, tests/FrunchyBackend.Tests/RecapTests.fs,
  tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T004 `changelog.d/029-streaks-and-recap.md` (Added), the CLAUDE.md dashboard route line, full suite
