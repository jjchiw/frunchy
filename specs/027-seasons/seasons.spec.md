# Feature Specification: Seasons

**Feature Branch**: `027-seasons`

**Created**: 2026-10-26

**Status**: Draft

**Input**: "The stats look awesome, but One Piece and Toei are on top because they are long anime. One Piece has
1,000 episodes and The Apothecary Diaries only 12 a season. We should tell that apart, maybe with seasonals:
spring 20xx, summer 20xx, fall 20xx, winter 20xx." The owner chose all four parts: store each episode's air date,
a "Your seasons" section, taste figures that count seasons instead of minutes, and the flagship anime apart from a
"long run" card. With the designer.

## Clarifications

### Session 2026-10-26 (decided in the autonomous run; the owner can overturn any of them)

- Q: Where does the airing season come from? → A: Each episode's own air date, `episode_air_date` in the
  Crunchyroll history entry (`panel.episode_metadata`), stored on the catalog episode. MyAnimeList's season
  describes when an entry *started* (One Piece: Fall 1999), which is exactly the problem; the episode's date puts
  a One Piece episode aired this October in Fall 2026. The airing season is the UTC quarter of that date, named
  like the charts (Winter = January to March, Spring, Summer, Fall).
- Q: Existing data? → A: The date is in every stored history page already. The database fills it in for the
  catalog on its own at start, once, from the stored pages (newest capture wins), so nobody re-syncs. New imports
  store it as they parse. An episode without a date (none in the page, or only an unresolved entry seen) has no
  airing season and falls back as below.
- Q: A dub and its original? → A: They count once, as everywhere in the DNA (the original season plus the episode
  number). The group's air date is the earliest of its episodes' dates (the original airs first).
- Q: What does a taste figure count instead of minutes? → A: **Watched seasons**: a pair (MyAnimeList entry,
  airing season) for which the person fully watched at least one (deduplicated) episode. The Apothecary Diaries
  season 1 is one watched season per airing season it ran in; One Piece is one per airing season the person
  watched episodes from, so 266 episodes of One Piece aired over 12 seasons weigh 12, not 266 x 24 minutes. An
  entry whose watched episodes have no air date is one watched season. A two-cour show counts twice: it was on
  the air in two seasons.
- Q: Which figures change? → A: On the Anime DNA and the year in review: favourite studio (and runner-up), genres
  (with their top series), decades, and formats. Each share is watched seasons with that value over watched
  seasons with any value, and its coverage note counts watched seasons ("Based on 41 of 45 seasons"). A decade is
  that of the watched season's airing year, else the entry's MyAnimeList year. Totals, hours, the pace chart, the
  "Watched by" counts, the dashboard's "Top genres", charts and taste compatibility are unchanged (compatibility
  and the dashboard's genres may follow later; out of scope).
- Q: What is a long runner? → A: A Crunchyroll series is a long runner when its MyAnimeList entry (any matched
  season of it) has more than 100 episodes, or any of its known catalog episodes is numbered above 100. One Piece,
  Detective Conan, Naruto Shippuden and Bleach are; a 12- or 24-episode season, or a 4-season show of 12 each, is
  not. Deterministic and needs no new data.
- Q: The flagship ("Anime insignia")? → A: The series with the most deduplicated minutes, as today, **among the
  ones that aren't long runners**. If every watched series is a long runner, there is no flagship card.
- Q: The long-run card? → A: The long runner with the most deduplicated minutes, shown in its own card when the
  person fully watched at least 24 of its episodes: title (linked like the flagship), poster, episodes fully
  watched, hours, and the airing seasons they span ("Fall 1999 to Fall 2026") when known. No card otherwise.
- Q: "Your seasons"? → A: A section on the Anime DNA. For each airing season with at least one fully watched
  episode: the shows (Crunchyroll series) watched from it, each with its episodes fully watched, and whether any of
  them was watched **while airing** (fully watched within 7 days of its air date). It names the person's top
  season (most shows; a tie goes to the newer season) and lists the 8 newest seasons, newest first, with their
  shows (posters and titles linked like other series on the page). Seasons before the newest 8 are counted, not
  listed. The layout is the designer's (brief in `design-brief.md`); the data and `data-*` hooks are fixed here.
- Q: The year in review? → A: Its studio, genre, decade and format figures follow the new counting rule; it gets
  no seasons section (the year counts by when an episode was watched, the seasons section by when it aired; one
  page with both would be confusing).
- Q: Visibility and privacy? → A: Unchanged: the DNA page's gate and threshold (15 fully watched episodes) apply.
  Nothing new is public.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Taste figures aren't swallowed by a long show (Priority: P1)

A person who watched 266 One Piece episodes and twenty 12-episode seasons opens their Anime DNA. The favourite
studio, genres and decades reflect the twenty seasons and One Piece's seasons, not One Piece's hours; the decade
of a One Piece episode aired this year is the 2020s.

**Independent Test**: build a history with many episodes of one long series from one studio and a few short
seasons from another studio: the short seasons' studio wins on the DNA, while hours and totals are unchanged.

**Acceptance Scenarios**:

1. **Given** 266 fully watched episodes of a long runner aired over 12 seasons (studio A) and 20 short seasons
   (studio B), **When** the DNA renders, **Then** studio B is the favourite with 20 of 32 watched seasons.
2. **Given** a long-runner episode aired in 2026 on an entry that started in 1999, **When** decades are computed,
   **Then** it counts in the 2020s.
3. **Given** a watched season whose episodes have no air date, **When** figures are computed, **Then** it counts
   once, its decade from the MyAnimeList year.
4. **Given** a dub and its original both watched, **When** figures are computed, **Then** they are one watched season.
5. **Given** the same history, **When** the year in review renders, **Then** its studio, genre, decade and format
   figures follow the same rule for that year's watching.

---

### User Story 2 - The flagship and the long run (Priority: P1)

The flagship card shows the person's top regular-length show; One Piece gets its own "long run" card with its
episodes, hours and the seasons it spans.

**Acceptance Scenarios**:

1. **Given** One Piece (most minutes) and The Apothecary Diaries (most among the rest), **When** the DNA renders,
   **Then** the flagship is The Apothecary Diaries and the long-run card is One Piece.
2. **Given** a long runner with fewer than 24 fully watched episodes, **When** the DNA renders, **Then** there is no
   long-run card.
3. **Given** only long runners watched, **When** the DNA renders, **Then** there is no flagship card, and the
   long-run card shows.
4. **Given** no long runner, **When** the DNA renders, **Then** the flagship is as today and there is no long-run card.

---

### User Story 3 - Your seasons (Priority: P2)

The person sees which airing seasons they watched, which shows from each, which they watched while airing, and
their top season.

**Acceptance Scenarios**:

1. **Given** episodes aired in Fall 2025, Winter 2026 and Fall 2026, **When** the DNA renders, **Then** the section
   lists Fall 2026, Winter 2026, Fall 2025 in that order with their shows.
2. **Given** an episode fully watched 3 days after it aired, **When** its season renders, **Then** its show is marked
   as watched while airing; watched 30 days later, it is not.
3. **Given** two seasons with the same number of shows, **When** the top season is chosen, **Then** it is the newer.
4. **Given** 11 airing seasons, **When** the section renders, **Then** it lists the 8 newest and says 3 earlier
   seasons are not shown.
5. **Given** no episode with an air date, **When** the DNA renders, **Then** the section is not shown.

---

### User Story 4 - Existing histories get air dates without a re-sync (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a database from before this feature with stored pages, **When** the new version starts, **Then** every
   catalog episode that appears in a stored page with an `episode_air_date` gets it, once; a second start changes
   nothing.
2. **Given** an episode seen in two pages with different air dates, **When** the backfill runs, **Then** the newest
   capture wins, as the catalog's freshness rule.
3. **Given** a new import, **When** it is processed, **Then** the air date is stored with the episode (freshness
   rule unchanged), and `user-rebuild` gives identical figures.

### Edge Cases

- An air date in the far future or before 1960 in a page: stored as given; a season label is still computed.
- An entry the person only partly watched: no watched season (it needs a fully watched episode), as the DNA's
  "fully watched" rules; its minutes still count in totals.
- Unmatched series (no MyAnimeList entry): no studio, genre, decade or format, as today; they still appear in "Your
  seasons" (it needs only the air date) and can be a long runner by episode number.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The catalog episode MUST store the air date from `episode_air_date`; new imports set it under the
  existing freshness rule; existing databases are filled in from the stored pages once, at start, as a guarded,
  idempotent upgrade (stored pages are only read).
- **FR-002**: The airing season MUST be the UTC quarter of the air date, named as the charts name seasons, in English
  and Spanish.
- **FR-003**: On the DNA and the year in review, studios, genres (and each genre's top series), decades and formats
  MUST be weighted by watched seasons as defined above; coverage notes MUST count watched seasons.
- **FR-004**: Long runners MUST be identified by the >100-episode rule; the flagship MUST exclude them; the long-run
  card MUST follow the rules above.
- **FR-005**: The DNA MUST show "Your seasons" per the rules above.
- **FR-006**: Order-independence, idempotency and `user-rebuild` equality MUST hold.
- **FR-007**: Every new string MUST be in `Views/Texts.fs` in English and Spanish; tests read `data-*` hooks only.
- **FR-008**: The DNA's totals MUST still equal the dashboard's (the existing pinning test stays green).

### Key Entities

- **Catalog episode**: gains `aired_at` (nullable).
- **Watched season**: (MyAnimeList entry or none, airing season or none) per person, derived at request time.

## Success Criteria *(mandatory)*

- **SC-001**: In the owner's own history, the favourite studio and the decades no longer follow One Piece's hours.
- **SC-002**: The owner's flagship is a regular-length show and One Piece has the long-run card.
- **SC-003**: Existing data shows airing seasons after one restart, with no re-sync.
- **SC-004**: The full suite passes; the DNA page renders in under the time it takes today (one statement per figure
  group, no per-row queries).

## Assumptions

- `episode_air_date` is present on most Crunchyroll entries with a panel (it is in the 2026 sample).
- The designer session reviews the new sections (brief in `design-brief.md`) before the UI is final.
