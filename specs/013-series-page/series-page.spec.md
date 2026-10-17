# Feature Specification: Public Series Page

**Feature Branch**: `013-series-page`

**Created**: 2026-10-17

**Status**: Draft

**Input**: "A public page per series, `/anime/{seriesId}`, not tied to a person: what the series is, its seasons with their MyAnimeList details, its episodes leading to their comment threads, and how many people on Frunchy watch it. Charts and episode pages link to it; it is the page a series' fediverse account will point to (feature 014). Bundled: new usernames can no longer end in a dash (Mastodon refuses them), before anyone federates."

## Clarifications

### Session 2026-10-17 (decided in the autonomous run; the owner can overturn any of them)

- Q: Which series get a page? → A: Every series in the shared catalog with at least one episode whose details are known. The catalog only grows from imports, so every page belongs to something someone watched. Nothing on the page names a person.
- Q: Which episodes are listed? → A: The episodes Frunchy knows, per season, in episode order. Each links to its episode page and shows its number of visible comments. They are "known episodes", not the season's full Crunchyroll list, and the page says so (the anime page's wording).
- Q: Activity? → A: "Watched by N people on Frunchy" (all time) and "N this week", each only at or above the charts' minimum (feature 012).
- Q: Where do links go? → A:
  - **Chart rows**: the title now links to the series page for everyone, and the viewer's own anime page becomes a second link ("Your history") when they've watched the series.
  - **Episode page**: its series title links to the series page.
  - **A person's anime page**: it gains a link to the series page.
  - **The rest**: dashboard, DNA, year and compare keep linking to the person's anime page, since they are about a person.
- Q: Usernames ending in a dash? → A: New usernames (sign-up, `user-create`, and the operator commands' input) must end in a letter, digit or underscore. Existing usernames keep working: lookups still accept the old rule. The local database has none (checked 2026-10-10), and there's no rename feature. A `user-list` marker for any that exist is left for 014 if needed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A page for the series (Priority: P1)

Anyone opens `/anime/{seriesId}` and sees:
- **Header**: the series title, its poster, "Watch on Crunchyroll", and the activity line.
- **Seasons**, in Crunchyroll's order, each with:
  - its title, audio language and dub mark;
  - its MyAnimeList details, shown once per entry when seasons share it (the anime page's rule);
  - its known episodes, each linking to its episode page with its comment count.

**Why this priority**: it is the feature. Charts and episode pages need somewhere to point, and the fediverse needs a profile page for each series.

**Independent Test**: import the 2026 sample for several users, enrich it with the 003 fixtures, open a series with several seasons and a dub, and compare every field, season, episode and count with independent queries, in English and Spanish, signed in and out.

**Acceptance Scenarios**:

1. **Given** a series in the catalog, **When** anyone opens its page, **Then** it shows the header, every season with known episodes in Crunchyroll's season order, and under each the known episodes in episode order with links to their episode pages.
2. **Given** a matched season, **When** it is shown, **Then** it carries its MyAnimeList details. Seasons sharing a MyAnimeList entry show the details once, and an unmatched season says so plainly.
3. **Given** an episode with visible comments, **When** it is listed, **Then** it shows the count of public, unlisted, non-deleted comments (hidden ones count, as in the thread).
4. **Given** people counts below the charts' minimum, **When** the page is shown, **Then** neither activity line appears.
5. **Given** an id outside Crunchyroll's alphabet, an unknown series, or a series with no known episode, **When** its address is opened, **Then** the not-found page is shown.

---

### User Story 2 - Everything leads to it (Priority: P2)

- **Chart rows**: each title links to its series page.
- **Episode page**: its series title links to the series page.
- **A person's anime page**: it has a link to the series page.

**Why this priority**: without the links, the page exists but nobody reaches it, and the charts keep their dead ends.

**Independent Test**: from a chart row, an episode page and a person's anime page, follow the series link and land on the right page.

**Acceptance Scenarios**:

1. **Given** any chart row, **When** it is shown, **Then** its title links to `/anime/{seriesId}`. A signed-in viewer who watched the series also gets a "Your history" link to their anime page.
2. **Given** an episode page, **When** it is shown, **Then** its series title links to the series page.
3. **Given** a person's anime page, **When** it is shown, **Then** it links to the series page.

---

### User Story 3 - Usernames that can federate (Priority: P2)

A new username can't end in a dash; sign-up and `user-create` explain the rule. Existing usernames keep working everywhere.

**Why this priority**: Mastodon refuses remote usernames ending in `-`, so such a user could never be found from the fediverse (feature 014). The rule must be in place before anyone federates.

**Independent Test**: sign up with `abc-` and get the error; sign up with `abc_` and `a-b` and succeed. A user stored with `abc-` before the change still has a working dashboard.

**Acceptance Scenarios**:

1. **Given** sign-up with a username ending in `-`, **When** it is submitted, **Then** it is refused with a message saying the name must end in a letter, digit or underscore, and nothing is created.
2. **Given** `user-create abc-`, **When** it runs, **Then** it exits with the bad-username code and creates nothing.
3. **Given** an existing user whose username ends in `-`, **When** their pages are opened, **Then** they work as before.

### Edge Cases

- **Series without a stored series row** (a franchise movie on its own): the title comes from its episodes, and there is no Crunchyroll link without a slug (the anime page's rule).
- **Episodes without a season**: grouped under "Other episodes", after the seasons.
- **Episodes without a number**: they show their special label and come after the numbered ones.
- **Very long series** (One Piece, over 1,000 known episodes): seasons list their episodes compactly. The page stays under 1 s, and long seasons are collapsed by default with a native `<details>`.
- **Dubs**: a dub season is shown as its own season with its audio language and dub mark. Its episodes link to their own episode pages, whose threads are shared with the original (feature 009).

## Requirements *(mandatory)*

### Functional Requirements

**Series page**

- **FR-001**: The site MUST serve a public page at `/anime/{seriesId}`, in the viewer's language. An id outside `^[A-Z0-9]{6,20}$` gets the not-found page without a query. So does an unknown series or one with no episode of known length.
- **FR-002**: The header MUST show:
  - the series title (the stored title, else the episodes' series title, else the id);
  - a poster: the MyAnimeList image of the matched season most people watched, else the newest Crunchyroll thumbnail, else none;
  - "Watch on Crunchyroll" when the slug is known;
  - "Watched by N people on Frunchy" and "N watched it this week", each only at or above the charts' minimum.
- **FR-003**: Seasons MUST be listed in Crunchyroll's order (season number, then id), only those with known episodes. Each shows:
  - its title (else "Season N");
  - its audio language and a dub mark;
  - its known-episode count;
  - its MyAnimeList details when matched (shown once per shared entry), else a plain "not on MyAnimeList yet" line.
- **FR-004**: Under each season, its known episodes MUST be listed in episode order (numberless ones last), each with its number or label, title, length, and comment count. Each links to `/anime/{seriesId}/episode/{episodeId}`.
- **FR-005**: A season with more than 30 known episodes MUST start collapsed, with a native `<details>` whose summary gives the episode count. No script.

**Links**

- **FR-006**: Chart rows MUST link their title to the series page, and keep the viewer's own anime-page link as a separate "Your history" link.
- **FR-007**: The episode page's series title and a person's anime page MUST link to the series page.

**Usernames**

- **FR-008**: New usernames (sign-up, `user-create`, and the usernames typed into operator commands and the operators page) MUST end in a letter, digit or underscore, in addition to today's rule.
- **FR-009**: Looking up an existing user MUST keep accepting today's rule, so stored usernames ending in `-` still resolve.

**General**

- **FR-010**: Every figure MUST be computed from stored data when the page is viewed, and nothing new is stored. The page uses the Night Broadcast design system, works at 360px, meets WCAG 2.1 AA, and takes every text from the English and Spanish texts.

### Key Entities

- **Series page** (computed): series, header, activity, and seasons with their known episodes and comment counts.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For series in the 2026 sample (several seasons, a dub, unmatched, a movie without a series row), every field, season, episode and count equals an independent query, in English and Spanish.
- **SC-002**: The One Piece series page from the owner's history (or a synthetic one with 1,100 episodes) is ready in under 1 second.
- **SC-003**: From any chart row, episode page or anime page, the series page is 1 click away.
- **SC-004**: No username ending in `-` can be created, and every existing one still resolves.
- **SC-005**: The page passes the design-system checks and an impeccable audit with no blocking finding, at 1280 and 360px, in both languages.

## Assumptions

- Comment counts follow the thread's visibility rules (feature 009).
- The page has no comments of its own. Discussion stays on the episode pages.
- Feature 014 (the fediverse integration) uses `/anime/{seriesId}` as each series account's profile URL.

## Verbatim Constraints

- Route: `/anime/{seriesId}`
- Username rule for new names: `^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$`, lengths 3–30 as today
