# Feature Specification: Year in Review

**Feature Branch**: `005-year-in-review`

**Created**: 2026-10-09

**Status**: Draft

**Input**: "Year in review: a public page per user and calendar year that sums up that year of the user's Crunchyroll watch history plus the MyAnimeList data of feature 003, using only data Frunchy already holds."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - My year in anime (Priority: P1)

A viewer opens a profile's year page and sees what that person watched in one calendar year: how many hours and days, how many episodes and series, the series they watched most, how the year spread across the months and which month was the busiest, their top genres and studios, how much was new to them versus continued, the formats they watched, and their longest run of consecutive days with a finished episode. Every figure comes from the stored history; MyAnimeList-based figures say how much of the year they cover. The page states that a rewatch counts in the year of its latest watch.

**Why this priority**: it is the feature: a shareable look back at a year, built from data Frunchy already holds.

**Independent Test**: import the 2026 sample for a user, enrich it with the feature 003 fixtures, open the year page for a year in the sample, and compare each figure with a hand count over that year's watches, in English and Spanish.

**Acceptance Scenarios**:

1. **Given** a user with history in a year, **When** anyone opens that year's page, **Then** it shows hours and days, fully watched episodes, series watched, the year's most-watched series with a poster, a 12-month chart of watch time with the busiest month named, the top 5 genres plus "Others", the top 3 studios, new versus continued series, the format split, and the longest daily streak.
2. **Given** the same user, **When** the hours of every year with history are added up, **Then** the total equals the dashboard's hours.
3. **Given** a MyAnimeList-based figure on the page, **When** it is shown, **Then** it counts matched seasons only and states its coverage of the year's watch time.
4. **Given** an episode watched in a year and rewatched in a later year, **When** both years are viewed, **Then** it counts only in the later year, and the page explains this.
5. **Given** a year with fewer than 15 fully watched episodes, **When** its page is opened, **Then** it shows a "not enough data" state with the count, and the year picker still works.

### User Story 2 - Move between years and pages (Priority: P2)

From the dashboard and the DNA page a viewer reaches the year page, and on it switches between every year that has history.

**Why this priority**: the page is only discoverable through links, and a single year is half the story.

**Independent Test**: with history in two years, follow the dashboard link, switch years with the picker, and return to the dashboard.

**Acceptance Scenarios**:

1. **Given** a user with history, **When** the dashboard or the DNA page is shown, **Then** each links to the year page.
2. **Given** a user with history in several years, **When** the year page is shown, **Then** a picker lists exactly those years, newest first, with the current one marked.
3. **Given** the address without a year, **When** it is opened, **Then** it goes to the most recent year with history (the current year when there is none).
4. **Given** a year with no history, **When** its address is opened, **Then** it goes to the address without a year.

### Edge Cases

- A user with no history at all: the address without a year shows the current year's "not enough data" state, with no picker entries to switch to.
- An unknown or deleted username: the site's "User not found" page with a not-found status.
- A year that is not a number, or far outside any possible history (before 2000 or after the current year): not found.
- A series watched across a year boundary counts as continued in the later year.
- A year whose matched seasons have no studio, genre or format: that section shows its own "not enough data" state.
- Ties (studios, genres, months, series) are ordered by name or by date, so the page is stable.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The site MUST serve a public page per user and calendar year at `/u/{username}/year/{year}`, in the viewer's language, with the same visibility as the dashboard.
- **FR-002**: `/u/{username}/year` MUST redirect to the most recent year with history, or to the current year when the user has none.
- **FR-003**: A year with no history, other than the current year, MUST redirect to `/u/{username}/year`; a non-numeric year, a year before 2000 or after the current year, or an unknown user MUST get the not-found page.
- **FR-004**: A year belongs to an episode by the date of its latest watch, as a calendar date in UTC; the page MUST say that rewatches count in the year of their latest watch.
- **FR-005**: The year's hours, days, fully watched episodes and series MUST follow the dashboard's rules, restricted to watches in that year, so the hours of all years add up to the dashboard's hours.
- **FR-006**: The most-watched series of the year MUST be the Crunchyroll series with the most minutes watched that year, with its title, a poster (its most-watched matched season's MyAnimeList image, else a Crunchyroll thumbnail) and its fully watched episodes that year.
- **FR-007**: The page MUST show watch time for each of the 12 months as a chart and name the busiest month (ties to the earlier month).
- **FR-008**: Top genres (top 5 plus "Others", shares adding up to 100%), top 3 studios and the format split MUST follow the DNA page's rules (minutes watched, a dub and its original counted once, matched seasons only, explicit genres excluded), restricted to the year, each with its coverage of the year's watch time.
- **FR-009**: A series MUST count as new in a year when the earliest watch Frunchy holds for any of its episodes falls in that year, and as continued otherwise; the page shows both counts. Because only the latest watch of each episode is held, a series rewatched entirely in a later year counts as new there; the rewatch note of FR-004 covers this.
- **FR-010**: The longest streak MUST be the most consecutive calendar days (UTC) in the year with at least one fully watched episode, with its first and last day.
- **FR-011**: A year with fewer than 15 fully watched episodes MUST show a "not enough data" state with the count instead of the figures.
- **FR-012**: The page MUST list every year with history as a picker, newest first, marking the shown year.
- **FR-013**: The dashboard and the DNA page MUST link to `/u/{username}/year`, and the year page MUST link back to both.
- **FR-014**: Every figure MUST be computed from stored data when the page is viewed, with no call to MyAnimeList and no new stored data, and MUST be identical after `user-rebuild` and after re-sending the same history.
- **FR-015**: The page MUST NOT show rankings, comparisons with other users, image export or anything without a real data source; every visible text comes from the site's English and Spanish texts.
- **FR-016**: The page MUST use the shared design system and components, work from 360px wide in both languages, and meet WCAG 2.1 AA contrast.

### Key Entities

- **Year review** (computed, not stored): one user's figures for one calendar year, the years with history, and each MyAnimeList figure's coverage.
- **Watch year**: the UTC calendar year of an episode's latest watch.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the 2026 sample, every figure on a year page equals an independent count over the same data, in English and Spanish.
- **SC-002**: The hours of all of a user's year pages add up to the dashboard's hours, within rounding (0.1 hour per year).
- **SC-003**: On the owner's history (six years, about 6,000 episodes) a year page is ready in under 1 second.
- **SC-004**: A viewer reaches any year of a profile from its dashboard in 2 clicks.
- **SC-005**: The page passes the design-system checks of feature 004 (no default-theme leftovers, every test hook present) and an impeccable audit with no blocking finding.

## Assumptions

- Years are UTC calendar years, the clock the server stores; a watch near midnight may land in the neighbouring day or year for a viewer elsewhere.
- "Series watched" in a year means series with a fully watched episode that year, as on the dashboard.
- Top studios show 3, as the year has less data than the DNA page's all-time view.
- No mockup exists for this page; it is built from the shared components, and `design/BRIEF.md` gains an entry so the designer can draw it later.
- The extension does not link to the page in this feature; a later contract addition can add it.

## Verbatim Constraints

- Route: `/u/{username}/year/{year}`
- Route without a year: `/u/{username}/year`
- Minimum for figures: 15 fully watched episodes
