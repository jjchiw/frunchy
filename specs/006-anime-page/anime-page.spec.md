# Feature Specification: Anime Page

**Feature Branch**: `006-anime-page`

**Created**: 2026-10-10

**Status**: Draft

**Input**: "Anime page: a public page per user and Crunchyroll series that shows what that person has watched of one series, from data Frunchy already holds (watch history plus the MyAnimeList data of feature 003)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - What I watched of one anime (Priority: P1)

A viewer opens a profile's page for one series and sees everything that person has watched of it.
- **Header**: the series title and poster, a link to watch it on Crunchyroll, and a way back to the dashboard.
- **Progress**: fully watched episodes, hours, and the first and latest watch Frunchy holds. A note says Frunchy keeps only the latest watch of each episode, so rewatches aren't counted.
- **Seasons**, in Crunchyroll's order, each with:
  - its title and audio language (dubs marked);
  - how many of its known episodes were fully watched, with a progress bar;
  - when matched, its MyAnimeList details: title, format, year and season, score, episode count, airing status, up to 3 genres, the first studio, and a link. An unmatched season says so plainly.
- **Episodes**: under each season, the episodes the person watched, in order. Each shows its number or special label, title, length, whether it was finished or how far it got, and when.

**Why this priority**: it is the feature: the place a viewer lands when they want the detail behind a title on the dashboard, the DNA page or the year page.

**Independent Test**: import the 2026 sample, enrich it with the feature 003 fixtures, open the page for a series with several seasons and a dub, and compare every figure, season and episode with a hand count over the stored watches, in English and Spanish.

**Acceptance Scenarios**:

1. **Given** a user who watched a series, **When** anyone opens its page, **Then** it shows the header, the progress figures, every season the user watched in Crunchyroll's season order, and under each season the user's episodes in episode order.
2. **Given** the same series, **When** its hours and fully watched episodes are compared with the dashboard's rules applied to that series alone, **Then** they are equal.
3. **Given** a season matched to MyAnimeList, **When** it is shown, **Then** it carries the MyAnimeList details and a link to its MyAnimeList page; **given** an unmatched season, **then** it shows a plain "not on MyAnimeList yet" line and no empty fields.
4. **Given** a dubbed season, **When** it is shown, **Then** its audio language is shown and it is marked as a dub, next to its original season.
5. **Given** an episode watched partly, **When** it is listed, **Then** it says how far it got (a percentage of its length) instead of "watched".
6. **Given** episodes Frunchy knows for a season but the user never watched, **When** the season is shown, **Then** they are counted in "of N known episodes" and not listed.
7. **Given** an unknown user, a deleted user, or a series the user never watched, **When** its address is opened, **Then** the site's not-found page is shown with a not-found status.

### User Story 2 - Reach it from the other pages (Priority: P2)

From any page that names a series (the dashboard's recently watched cards, the DNA page's insignia anime and each genre's top anime, the year page's most watched series and each genre's top anime), the series title leads to its anime page.

**Why this priority**: the page is only found through these links; without them it exists but nobody reaches it.

**Independent Test**: with the 2026 sample imported, follow the series links on the dashboard, the DNA page and the year page, and land on the right anime page each time.

**Acceptance Scenarios**:

1. **Given** the dashboard, **When** a recently watched card is shown, **Then** its series title links to that series' anime page.
2. **Given** the DNA page, **When** its insignia anime and each genre's top anime are shown, **Then** each title links to its anime page.
3. **Given** the year page, **When** its most watched series and each genre's top anime are shown, **Then** each title links to its anime page.

### Edge Cases

- **Watched only partly**: a series with no fully watched episode still has a page, with 0 fully watched and the partial episodes listed.
- **Unresolved episodes**: episodes with no details yet are excluded everywhere, as on the dashboard. A series whose watches are all unresolved has no page.
- **No series row**: a series with no stored series row (a franchise movie watched on its own) uses its episodes' series title, and it has no Crunchyroll link when there is no slug.
- **No season**: episodes without a season are grouped under a plain "Other episodes" group, after the seasons.
- **Missing episode numbers**: episodes without a number are listed with their special label, after the numbered ones.
- **Poster fallback**: with no matched season, the poster is a Crunchyroll thumbnail; with none of either, the header has no image.
- **Series id format**: an id with characters outside Crunchyroll's id alphabet is not found, without touching the database.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The site MUST serve a public page at `/u/{username}/anime/{seriesId}`, in the viewer's language, with the same visibility as the dashboard.
- **FR-002**: An unknown or deleted user, a series id outside Crunchyroll's id alphabet, or a series with no watched episode with a known length for that user MUST get the not-found page with a not-found status. The series id is matched exactly, case included.
- **FR-003**: The header MUST show the series title (the stored series title, else the episodes' series title), a poster (the MyAnimeList image of the user's most-watched matched season, else the newest Crunchyroll thumbnail of the series), and a link back to the dashboard.
- **FR-004**: When the series has a stored slug, the header MUST link to `https://www.crunchyroll.com/series/{id}/{slug}`, opening in a new tab.
- **FR-005**: The page MUST show the user's fully watched episodes and hours for the series, following the dashboard's rules restricted to that series.
- **FR-006**: The page MUST show the first and the latest watch Frunchy holds for the series, and say that only the latest watch of each episode is kept.
- **FR-007**: Seasons MUST be listed in Crunchyroll's season order (season number, then id), and only those with at least one watched episode.
- **FR-008**: Each season MUST show its title, its audio language, a dub mark when it is a dub of another season, and its fully watched episodes out of the episodes Frunchy knows for that season, with a progress bar.
- **FR-009**: A matched season MUST show its MyAnimeList title, format, year and season, score (when it has one), episode count (when known), airing status, up to 3 genres, the first studio, and a link to its MyAnimeList page. An unmatched season MUST show a plain line saying it isn't on MyAnimeList yet.
- **FR-010**: Each season MUST list the user's watched episodes in episode-number order (numberless ones last, by title), each with its number or special label, title, length in minutes, "watched" or its progress percentage, and when, using the site's existing time element.
- **FR-011**: The dashboard's recent cards, the DNA page's insignia anime and genre top anime, and the year page's most watched series and genre top anime MUST link their series title to the anime page.
- **FR-012**: Every figure MUST be computed from stored data when the page is viewed, with no call to MyAnimeList and no new stored data, and MUST be identical after `user-rebuild` and after re-sending the same history.
- **FR-013**: The page MUST NOT show ratings of the user's own, rankings, recommendations or anything without a real data source. Every visible text comes from the site's English and Spanish texts.
- **FR-014**: The page MUST use the shared design system and components, work from 360px wide in both languages, and meet WCAG 2.1 AA contrast.

### Key Entities

- **Anime page** (computed, not stored): one user's view of one Crunchyroll series: the header, progress figures, and the watched seasons with their episodes.
- **Season row**: a Crunchyroll season the user watched, its known-episode count, its dub relation, and its MyAnimeList match when there is one.
- **Watched episode**: an episode of that season with its latest held watch state for the user.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For series in the 2026 sample (one with several seasons, one with a dub, one unmatched), every figure, season and episode on the page equals an independent count over the same data, in English and Spanish.
- **SC-002**: The series' hours and fully watched episodes equal the dashboard's rules applied to that series alone.
- **SC-003**: On the owner's history, the page for the longest series (One Piece) is ready in under 1 second.
- **SC-004**: From the dashboard, the DNA page or the year page, a viewer reaches an anime page in 1 click.
- **SC-005**: The page passes the design-system checks of feature 004 (no default-theme leftovers, every test hook present) and an impeccable audit with no blocking finding.

## Assumptions

- The page is public like the dashboard; it shows nothing the dashboard's recent cards don't already reveal about watches.
- "Episodes Frunchy knows" for a season are the episodes any user's history has brought into the shared catalog, so the count can grow as more history arrives; it is not the season's total on Crunchyroll.
- Audio languages are shown with the language name for the stored locale code (for example "Japanese", "English", "Spanish (Latin America)") in the viewer's language.
- No mockup exists; the page is built from the shared components, and `design/BRIEF.md` gains an entry for it.
- The extension does not link to the page in this feature.

## Verbatim Constraints

- Route: `/u/{username}/anime/{seriesId}`
- Crunchyroll link: `https://www.crunchyroll.com/series/{id}/{slug}`
