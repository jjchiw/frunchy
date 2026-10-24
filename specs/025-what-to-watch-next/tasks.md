# Tasks: What to watch next

**Input**: [what-to-watch-next.spec.md](what-to-watch-next.spec.md)

**Tests**: `data-*` hooks only. The suite must stay green.

- [x] T001 `Compatibility.fs`: make `loadPeople` public (it takes any number of users) and let `loadTheirDetails`
  take the users as an array (`Guid[]`; `load` passes `[| owner |]`). No change in behaviour
  · Infrastructure/Compatibility.fs
- [x] T002 `Infrastructure/Recommendations.fs` (after `Compatibility.fs` and `Profiles.fs` in the fsproj):
  - constants `maxCandidates = 50`, `minimumLevel = 3`, `maxMatches = 10`, `maxSeries = 12`, `shownFinishers = 3`;
  - SQL 1, candidates: other users with ≥ `Compatibility.minimumEpisodes` fully watched episodes of known length who
    share a series (episode of known length) with the viewer, with `profile_settings.visibility` and
    `share_link_id` (LEFT JOIN) and the shared-series count; filtered in F# with `Profiles.canView (Some viewer)
    grants`, ordered by shared count desc then username, `maxCandidates`;
  - `Compatibility.loadPeople` for the viewer and the candidates; viewer short → `NotEnough`;
  - pure `matches`: `Compatibility.ofPeople viewer c`, level ≥ `minimumLevel`, by score desc then username,
    `maxMatches`;
  - SQL 2, per (match, series): watched through (seasons the user watched anything of; fully watched ≥ 80% of
    the known-length episodes of those seasons, and ≥ 1; integer arithmetic) and the viewer's started series (any
    watch event);
  - pure `recommend` (ranking of the spec) and the MyAnimeList details via `loadTheirDetails` over the matches;
  - result `NotEnough | NoMatches | NoSeries of matchCount | Series of matchCount * Recommendation list`; the
    score stays internal (finishers carry `Username`, `DisplayName`, `Level`)
  · Infrastructure/Recommendations.fs, FrunchyBackend.fsproj
- [x] T003 `GET /next` (`WebAuth.requireSignedIn`): `profileTop`-free page with `h1`, intro, the list (as compare's
  "theirs" section: `fr-card`, `sectionHeading`, MyAnimeList pills) and the three empty states; registered in
  `Program.endpoints`. The owner's dashboard gets `a[data-next-link]` (not for visitors). Texts en and es
  · Handlers/Users/Next.fs, Handlers/Users/Dashboard.fs, Views/Texts.fs, Program.fs, FrunchyBackend.fsproj
- [x] T004 Tests:
  - `RecommendationRulesTests` (pure): ranking, ties, finisher order, truncation, level threshold;
  - `NextPageTests` (Postgres): signed out redirects; not enough; no matches; a public and a members match
    recommend their watched-through series the viewer never started, ranked; a started series (even an unresolved
    episode) never shows; a private match contributes nothing until the viewer holds a grant of its current link,
    and nothing again after the link is replaced; the series link goes to `/anime/{id}`; no score or percentage in
    the HTML; the dashboard link for the owner only; Spanish
  · tests/FrunchyBackend.Tests/RecommendationRulesTests.fs, tests/FrunchyBackend.Tests/NextPageTests.fs
- [x] T005 CHANGELOG `## Unreleased` (Added), the CLAUDE.md routes line, full suite
