# Feature Specification: Charts and Trends

**Feature Branch**: `012-charts-trends`

**Created**: 2026-10-16

**Status**: Draft

**Input**: "Charts and trends, the other half of 'community': what Frunchy is watching across all users. The most-watched series this week, this season and of all time; who's moving up; 'watched by N people on Frunchy' on anime and episode pages. Data Frunchy already has, a privacy rule (a minimum number of people before a series shows), no chart library (server-rendered bars and small inline SVG), the Night Broadcast look."

## Clarifications

### Session 2026-10-16 (decided in the autonomous run; the owner can overturn any of them)

- Q: How are series ranked? → A: By **people**: how many different users watched at least one episode of the series in the window. Ties go to the one with more episodes watched in the window, then the title. This is Last.fm's "listeners", which a single binge-watcher can't push to the top.
- Q: Which windows? → A: **This week** (the last 7 days, rolling, UTC), **this season** (the current anime season: Winter Jan–Mar, Spring Apr–Jun, Summer Jul–Sep, Fall Oct–Dec, UTC; MyAnimeList's convention) and **all time**.
- Q: The privacy rule? → A: A series shows in a chart, and "watched by N people" shows anywhere, only when at least **3 different people** watched it in that window. The number is a setting (`Frunchy:Charts:MinPeople`, default 3, never below 2). No chart or count ever names a person.
- Q: Where does a chart row link? → A: There is no public series page; anime pages belong to a person. A signed-in viewer who watched the series gets a link to their own anime page. Everyone gets a "Watch on Crunchyroll" link when the series' slug is known. A public series page is left for later.
- Q: Trends? → A: The week chart shows **movement** against the previous 7 days (up, down, new) and a small line of the series' people per week over the last 8 weeks. The season and all-time charts show neither.
- Q: Rewatches? → A: Frunchy keeps only the latest watch of each episode, so a series counts in the window where its episodes were last watched. The page says so in one line, like the anime page does.

### Session 2026-10-16 (after measuring)

- Q: The all-time chart takes 4–8 s at 2M watch events; what now? → A: A faster statement (aggregate per person and series first, look up posters only for the 20 charted series) brings it to about 2.5 s. It can't reach 1 s per request at that scale, so the all-time rows are kept in memory for 10 minutes (ASP.NET's memory cache: no table, no worker). Week and season stay per request. Whether the viewer watched a row's series is checked per request, so nothing personal is cached. Measured on a local copy of the 2M fixture; the cache needs justifying (plan, Complexity Tracking).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - What Frunchy is watching (Priority: P1)

Anyone, signed in or not, opens **Charts** from the navigation and sees the top 20 series this week. Each row shows:
- its rank;
- the series title and its poster or thumbnail;
- how many people watched it and how many episodes they watched in the window;
- a bar for the people count relative to the top row;
- links (to the viewer's own anime page, and to Crunchyroll).

Tabs switch to this season and all time.

**Why this priority**: it is the feature: the first page that is about everyone, not one person.

**Independent Test**: import histories for several users with overlapping series and controlled watch dates. Open each tab and compare every rank, count and link with an independent query, in English and Spanish, signed in and out.

**Acceptance Scenarios**:

1. **Given** watches in the last 7 days by several users, **When** anyone opens the charts, **Then** the week chart lists series by people (ties by episodes, then title), up to 20, each with its people and episode counts.
2. **Given** a series watched by fewer people than the minimum in the window, **When** the chart is built, **Then** it doesn't appear.
3. **Given** the season tab, **When** it is opened, **Then** it uses the current anime season's dates and names the season and year ("Fall 2026").
4. **Given** too little activity for any row, **When** a chart is opened, **Then** it says plainly that not enough people have watched anything yet in that window.
5. **Given** a signed-in viewer who watched a charted series, **When** the row is shown, **Then** its title links to their own anime page. Otherwise the title isn't a link.

---

### User Story 2 - Who's moving (Priority: P2)

On the week chart, each row shows its movement against the previous 7 days: up N, down N, the same, or new. It also shows a small line of its people per week over the last 8 weeks.

**Why this priority**: it turns a list into a chart worth coming back to, but the list already works without it.

**Independent Test**: build two consecutive weeks of watches with known ranks. Check each movement and each line's 8 values against a query.

**Acceptance Scenarios**:

1. **Given** a series ranked 5 last week and 2 this week, **When** the week chart is shown, **Then** it shows "up 3".
2. **Given** a series not in last week's chart (not ranked, or below the minimum), **When** it is shown, **Then** it is marked new.
3. **Given** the trend line, **When** it is drawn, **Then** it plots 8 weekly values, has an accessible text equivalent (for example "Last 8 weeks: 1, 0, 2, …"), and drops nobody below the minimum. Weekly values are counts of people, shown only for charted series.

---

### User Story 3 - "Watched by N people on Frunchy" (Priority: P2)

On a person's anime page and on an episode page, a line says how many people on Frunchy have watched that series or episode, when the number reaches the minimum.

**Why this priority**: it connects the personal pages to the community, cheaply.

**Independent Test**: with known histories, the counts on both pages equal a query, and they're absent below the minimum.

**Acceptance Scenarios**:

1. **Given** a series watched by at least the minimum number of people, **When** anyone opens someone's anime page for it, **Then** it says "Watched by N people on Frunchy".
2. **Given** an episode (with its dub group, as its comment thread) watched by at least the minimum, **When** its page is shown, **Then** it says the same for the episode.
3. **Given** fewer people than the minimum, **When** either page is shown, **Then** the line is absent.

### Edge Cases

- **Deleted users** stop counting immediately; their watches are deleted with them.
- **Unresolved episodes** don't count, as everywhere.
- **Dubs**: series are keyed on the Crunchyroll series, so a dub counts with its series. On episode pages the count covers the dub group.
- **Season boundary**: on 1 January the season chart restarts (Winter); with no rows yet it shows the not-enough state.
- **Minimum misconfigured** (0, 1, negative): treated as 2.
- **No poster**: the newest Crunchyroll thumbnail of the series, else no image, as on the anime page.
- **Week movement when last week's chart was empty**: every row is new.

## Requirements *(mandatory)*

### Functional Requirements

**Charts page**

- **FR-001**: The site MUST serve a public page `/charts` (the week chart), plus `/charts/season` and `/charts/all`. It MUST link to the page from the main navigation for everyone.
- **FR-002**: A chart MUST rank series by the number of different users with at least one watched episode (known length) of the series in the window. Ties go to more episodes watched in the window, then title, then series id. A chart shows up to 20 rows.
- **FR-003**: A series MUST only appear when its people count in the window is at least the minimum (`Frunchy:Charts:MinPeople`, default 3, values below 2 treated as 2).
- **FR-004**: Windows:
  - **Week**: the 7 days before now.
  - **Season**: from the first day of the current UTC quarter to now, named Winter, Spring, Summer or Fall plus the year.
  - **All time**: everything.
- **FR-005**: Each row MUST show:
  - its rank, the title, and the poster (the MyAnimeList image of the series' most-watched matched season, else the newest Crunchyroll thumbnail, else none);
  - the people and episode counts;
  - a solid bar of people relative to the first row;
  - the Crunchyroll series link when the slug is known;
  - a link to the viewer's own anime page when the viewer is signed in and has watched the series.
- **FR-006**: The page MUST state, once:
  - that a series counts where its episodes were last watched (only the latest watch is kept);
  - the minimum number of people.
- **FR-007**: The week chart MUST show each row's movement against the chart of the 7 days before, built with the same rules: up N, down N, same, or new.
- **FR-008**: The week chart MUST show, per row, a small line of the series' people for each of the last 8 weeks (7-day blocks ending now). It is drawn on the server, with an accessible text equivalent.

**Counts elsewhere**

- **FR-009**: The anime page (`/u/{username}/anime/{seriesId}`) MUST show "Watched by N people on Frunchy" (all time, all users) when N reaches the minimum.
- **FR-010**: The episode page MUST show the same for the episode's group (the dub rule of feature 009) when N reaches the minimum.

**General**

- **FR-011**: Every figure MUST be computed from stored data when the page is viewed. Nothing new is stored except an index. No person is ever named or linked in a chart or count.
- **FR-012**: The pages MUST use the Night Broadcast design system: amber for the top rows' bars, solid fills, no chart library, no new script. They MUST work at 360px, meet WCAG 2.1 AA, and take every text from the English and Spanish texts.

### Key Entities

- **Chart** (computed): a window, the minimum, and ranked rows.
- **Chart row** (computed): series, title, poster, people, episodes, and for the week chart its movement and its 8 weekly people counts.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For a fixture of several users with controlled dates, every rank, count, movement and trend value on the three charts equals an independent query, in English and Spanish.
- **SC-002**: No chart, count or line ever reflects a series watched by fewer people than the minimum, at any minimum from 2 to 5.
- **SC-003**: With 10,000 users × 200 watched episodes each (synthetic), the week and season charts render in under 1 second, and the all-time chart in under 1 second when its 10-minute copy is warm (under 4 seconds cold, measured and logged).
- **SC-004**: The charts are reachable in 1 click from any page, and a charted series' anime page (for a viewer who watched it) is 1 click from its row.
- **SC-005**: The pages pass the design-system checks and an impeccable audit with no blocking finding, at 1280 and 360px, in both languages.

## Assumptions

- Dashboards are already public, and charts only show counts at or above the minimum, so they reveal less than dashboards do.
- The extension doesn't change.
- A public series page, genre charts and charts per country or language are later features.
- Scale reference: the constitution's 10k users; SC-003 is set well above today's handful of users.

## Verbatim Constraints

- Routes: `/charts`, `/charts/season`, `/charts/all`
- Setting: `Frunchy:Charts:MinPeople` (default 3, minimum 2)
- No chart library: server-rendered bars and inline SVG
