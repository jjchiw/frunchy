# Research: Public Series Page

## R1. Slice and reuse

- **`Handlers/Series/Series.fs`** (new): view, private SQL, handler, then `GET [ routef "/anime/{%s}" … ]`.
- **Route clash**: `/anime/{s}/episode/{e}` already exists and has more segments, so the routes don't
  clash.
- **Shared UI**: the season name line and the compact MyAnimeList block are already shared
  (`Components.seasonNameLine`, `malCompact`). The anime page's `malDetails` is private, so it moves
  to `Views/Components.fs` (as `malDetails`) because two slices now use it (constitution III). The
  anime page calls the moved version, and its output and hooks don't change.
- **Counts**:
  - `Charts.seriesPeople` (all time);
  - `Charts.minPeople`, for the gate;
  - a new `Charts.seriesPeopleSince db seriesId from minPeople` for "this week", or the same
    function with an optional `from`.

## R2. One statement for the body

A row per known episode (`duration_ms IS NOT NULL`) of the series, carrying:
- its season (title, number, audio locale);
- its season's matched MyAnimeList entry and `method = 'dub'`;
- its comment count.

The comment count is a correlated count over `comments` with the thread's visibility: `deleted_at IS
NULL AND audience IN ('public','unlisted')`. That's per *episode*, not per dub group; the episode page
shows the group thread, but the list counts what was written on that episode.

The ordering is the anime page's: season number nulls last, season id, episode number nulls last,
title, id. F# folds the rows into seasons, the anime page's way.

- **Poster**: the MyAnimeList image of the matched season with the most distinct people (ties: the
  smaller `mal_id`), else the newest thumbnail. It's a small extra query.
- **Performance**: One Piece has about 1,100 episodes, giving 1,100 rows and 1,100 indexed comment
  counts (`ix_comments_episode`). That's well under a second, and a test with 1,100 synthetic
  episodes checks it (SC-002).

## R3. Long seasons

A season with more than 30 known episodes renders inside `<details>`, closed. The summary shows the
season's count ("1,122 known episodes"), and the episode list is inside. Shorter seasons render
open, without `<details>`. `data-season-collapsed` marks the collapsed ones.

## R4. Usernames

- **`Username.tryCreate`**: unchanged, it's the lookup rule (FR-009).
- **`Username.tryCreateNew`** (new): applies `^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$` plus the reserved
  names, lengths 3–30. Used by:
  - sign-up (`Handlers/Auth/SignUp.fs:198`);
  - `user-create` (`Operations/CreateUser.fs`).
- **Operator commands**: `operator-add`/`operator-remove` and the operators page look up existing
  users, so they keep `tryCreate`; an existing user ending in `-` must still be manageable.
  - **Spec correction**: FR-008's "the usernames typed into operator commands and the operators
    page" is narrowed to *creation* paths only (sign-up, `user-create`). Recorded in the spec.
- **The sign-up hint text** (`UsernameHint`, en and es) adds "ending in a letter, digit or _".
- **The bad-username error** reuses the existing error.

## R5. Links

- **`Charts.fs` row**:
  - **Title**: `a href="/anime/{seriesId}"` with `data-series-page-link`, for everyone.
  - **Viewer's own anime page**: when `mine` and signed in, a separate small link (`t.ChartsYourHistory`)
    keeps `data-chart-mine`.
- **`Episode.fs` header**: the series title wraps in `a href="/anime/{seriesId}"` with
  `data-series-page-link`.
- **`Anime.fs` header**: a link to the series page (`t.SeriesPageLink`, e.g. "About this series"),
  with `data-series-page-link`.
- **Contracts**: 012's contract changes (the `data-chart-mine` element moves from the title to a
  separate link). Its tests are updated to match, the only existing tests that change.
