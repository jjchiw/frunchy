# Feature Specification: Watch streaks and a weekly recap

**Feature Branch**: `029-streaks-and-recap`

**Created**: 2026-11-05

**Status**: Draft

**Input**: the follow-up task "Watch streaks and a weekly recap": everything so far is statistics and plumbing; the data
for streaks and weekly totals is already in `watch_events`. The current and longest daily streak, and a weekly recap
card on the dashboard (episodes, minutes, series, new vs continued), respecting private profiles (feature 017). Later
(not in this feature): an opt-in fediverse post of the recap.

## Clarifications

### Session 2026-11-05 (decided by the main session)

- Q: Which days? → A: **UTC calendar days**, like the year page (feature 005), the airing seasons (027) and the watch
  updates (028). "Today" is the UTC date of the request's time (`TimeProvider`, so tests can fix it). A viewer far
  from UTC may see a late-evening episode counted on the next day; the card says the days are UTC
  (`RecapDaysAreUtc`).
- Q: What makes a day a streak day? → A: A UTC day on which at least one episode of known length was **fully
  watched** (the dashboard's "Episodes watched" rule) by its `watched_at`. Frunchy stores only the latest watch of an
  episode (as the year page says), so rewatching an episode moves it to its new day; the streak follows what is
  stored.
- Q: The current streak? → A: The run of consecutive streak days that ends **today or yesterday** (a streak isn't
  broken until a whole UTC day passes without watching); otherwise 0. The longest streak is the longest run ever; a
  tie goes to the most recent run. The longest is never shorter than the current one.
- Q: The week? → A: **The last 7 days**, `watched_at >= now - 7 days`, the charts' week window (feature 012), so the
  card is never empty just because a week started. It is computed at request time; nothing is stored.
- Q: The recap's figures? → A: The dashboard's own rules restricted to the window (undeduplicated, like the dashboard
  and the year page's totals), so the seven-day figures read like the tiles above them:
  - **episodes**: fully watched episodes of known length;
  - **minutes**: fully watched episodes count their length, partly watched ones their playhead capped at the length
    (episodes of known length), rounded to whole minutes;
  - **series**: distinct series with a fully watched episode of known length in the window;
  - **new / continued**: of those series, *new* when the user has no watch event of any episode of the series (an
    unresolved one included) with `watched_at` before the window, *continued* otherwise. new + continued = series.
- Q: Nothing in the window? → A: The recap card still shows, with a plain line (`data-recap-empty`) instead of the
  figures; the streaks card shows the longest streak and a current streak of 0.
- Q: Who sees it? → A: Whoever may see the dashboard: it is part of `/u/{username}`, behind `Profiles.gate`, and its
  queries run after the gate inside the dashboard's load, so a hidden profile runs none of them and the 404 stays
  byte-identical to an unknown user. Owner and visitors see the same card; the text is in the third person neutral
  ("Last 7 days", "Current streak"), never "your".
- Q: When does the dashboard show the cards? → A: Only when it shows the figures today (`Figures = Some`); the empty
  dashboard keeps its empty state and runs no streak or recap query.
- Q: The fediverse post? → A: Out of scope: a later feature (opt-in, like 028).

## Requirements *(mandatory)*

- **FR-001**: The dashboard shows, between the stat tiles and the recent/genres grid, a "Last 7 days" card and a
  "Streaks" card side by side from `lg` (8 and 4 columns), stacked on phones.
- **FR-002**: The recap card: episodes, time (minutes shown as hours and minutes, `Texts.RecapTime`), series, and
  new vs continued, by the rules above.
- **FR-003**: The streaks card: the current streak in days, the longest streak in days and its first and last UTC
  dates (formatted with the page culture as plain dates, never a `data-fr-time` element, which would shift a UTC date
  into the viewer's time zone), or "No streak yet" when there is no streak day at all.
- **FR-004**: Hooks (tests read only these):
  - `section[data-recap]` with `data-recap-episodes`, `data-recap-minutes`, `data-recap-series`, `data-recap-new`,
    `data-recap-continued` (invariant integers) on the section; `[data-recap-empty]` inside it when the window has no
    watch event of known length;
  - `section[data-streaks]` with `data-streak-current` and `data-streak-longest` (integers, `0` when none), and,
    when a longest streak exists, `data-streak-longest-from` and `data-streak-longest-to` (`yyyy-MM-dd`);
    `[data-streak-none]` when there is no streak day.
- **FR-005**: The logic lives in a new `Infrastructure/Recap.fs`: the SQL (literal, parameters only, the guardrail
  test) and pure functions; the streak rule (`ofRuns today runs`) is pure and unit-tested. Two statements at most: the
  week's figures, and the streak runs (gaps-and-islands over the user's distinct UTC streak days, returning each run's
  first day, last day and length). The `ix_watch_events_user_watched` index serves both.
- **FR-006**: All copy is in `Views/Texts.fs`, English and Spanish. Only existing components and tokens (`fr-card`,
  `sectionHeading`, `metricTile`-like figures, `pill`, the existing `Theme.LocalFireDepartment` and
  `Theme.CalendarMonth` icons); no new design token, so the extension is untouched.
- **FR-007**: No schema change, no stored state, no change to `/api/*`, the DNA, year, compare or next pages.

## Success Criteria *(mandatory)*

- **SC-001**: A person who watched on three consecutive UTC days up to yesterday sees a current streak of 3; after a
  day without watching it shows 0 and the longest still shows 3 with its dates.
- **SC-002**: The recap's episodes, minutes and series equal the dashboard's figures computed over the last 7 days,
  and new + continued equals series.
- **SC-003**: A private profile's visitor gets the unknown-user 404, with no recap or streak hook in it.
