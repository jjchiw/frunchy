# Feature Specification: Taste Compatibility

**Feature Branch**: `011-taste-compatibility`

**Created**: 2026-10-15

**Status**: Draft

**Input**: "Taste compatibility, Last.fm's 'taste-o-meter' for anime: on someone's profile, how much your anime overlaps with theirs, the series you share, the genres in common and what's in their history that isn't in yours. Built from data Frunchy already has (two watch histories, MyAnimeList genres and studios), in the Night Broadcast look. Pitches: https://claude.ai/artifact/D8QVV7NJaTvGogvBJBB2yV (directions A and B, with C's headline sentence)."

## Clarifications

### Session 2026-10-15 (decided in the autonomous run; the owner can overturn any of them)

- Q: A number or a level? → A: A level in words on a 5-step meter ("High compatibility", 4 of 5), never a percentage. The method blends two signals and a percentage would suggest more precision than it has. The level's meaning is explained on the page in one plain sentence.
- Q: Who can compare? → A: A signed-in user compares themselves with another profile. A signed-out visitor gets no card, and the compare page sends them to sign in and back. Comparing two other people (`/u/a/vs/b`) is left for later.
- Q: Which directions? → A: The card on the other person's dashboard (pitch A) leads to a compare page (pitch B), whose heading is pitch C's sentence ("You and Mika share 14 series…").
- Q: How much history is enough? → A: The DNA page's rule: both people need at least 15 fully watched episodes. Below that, the card and page say there isn't enough yet, for whichever side is missing.
- Q: Is "In Mika's history, not yours" a recommendation? → A: No. It's a plain list of series in the other person's history that you have never watched, ordered by how much they watched. It isn't "recommended for you" and has no scores of its own.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See how compatible you are (Priority: P1)

A signed-in user opens someone else's dashboard and sees a card, "You and Mika". It shows a compatibility level in words with a 5-step meter, how many series both have watched, up to three genres both spend the most time on, the shared studio they both watch most, up to four series both have watched, and a link to see the full comparison.

**Why this priority**: it is the feature's whole point, and it appears where people already look.

**Independent Test**: two users import histories with overlap. Each opens the other's dashboard and sees the same level, the same shared count and the same shared series, computed independently by a test query, in English and Spanish.

**Acceptance Scenarios**:

1. **Given** two users with enough history, **When** one opens the other's dashboard signed in, **Then** the card shows the level, the shared series count, the shared genres, the shared studio, up to four shared series linking to the owner's anime pages, and a link to the compare page.
2. **Given** the same two users, **When** each opens the other's dashboard, **Then** both see the same level and the same shared series count.
3. **Given** a signed-out visitor, or the owner on their own dashboard, **When** the dashboard is shown, **Then** there is no card.
4. **Given** either side with fewer than 15 fully watched episodes, **When** the card would be shown, **Then** it says there isn't enough history yet, naming whose, with no level.
5. **Given** two users with no series in common, **When** the card is shown, **Then** it shows the lowest level and says they have no series in common yet, with no empty lists.

---

### User Story 2 - Compare side by side (Priority: P1)

The compare page (`/u/{username}/compare`) shows:
- **Heading**: a one-sentence summary ("You and Mika share 14 series and a soft spot for Romance") and the level, with each person's name and series count.
- **Where your time goes**: each person's share of minutes for the top genres either of them watches, side by side (you in amber, them in rose), with a coverage note like the DNA page's.
- **Series you've both watched**: each person's fully watched episodes of each.
- **In Mika's history, not yours**: series they watched that you never have, most watched first, each with its MyAnimeList score and genres when matched.
- **Studios you share**: studios both watch, with how many shared series each covers.

**Why this priority**: the card is a summary, and this page is where the comparison becomes useful.

**Independent Test**: for the same two sample users, every figure on the page equals an independent query, in both languages and at 360px.

**Acceptance Scenarios**:

1. **Given** two users with enough history, **When** one opens the compare page, **Then** every section shows, and every figure equals the rules below computed independently.
2. **Given** the genre section, **When** it is drawn, **Then** it lists up to 6 genres (the union of each person's top genres by share), each with both shares, and a coverage note for each person.
3. **Given** "Series you've both watched", **When** it lists more than 10, **Then** it shows the 10 both have watched most and says how many more there are.
4. **Given** a signed-out visitor, **When** they open the compare page, **Then** they go to sign-in and come back to it.
5. **Given** a signed-in user opening their own compare page, an unknown user, or a deleted one, **When** it is opened, **Then** the user's own page redirects to their dashboard, and unknown or deleted users get the not-found page.
6. **Given** either side without enough history, **When** the page opens, **Then** it shows the not-enough state instead of the sections.

### Edge Cases

- **No MyAnimeList data on one side**: the genre and studio sections say so; the level then rests on shared series alone, and the page says that.
- **Dubs**: a dub and its original count as one series, as everywhere: series are keyed on the Crunchyroll series.
- **Unresolved episodes**: they don't count, as on the dashboard.
- **Very different history sizes** (50 series against 900): the shared-series signal is measured against the smaller history, so a newcomer whose few series are all in a big history still scores high.
- **Rename**: compare pages use the current username; the old URL is not found, as for dashboards.
- **Symmetry**: the level is the same from both sides.

## Requirements *(mandatory)*

### Functional Requirements

**Who and where**

- **FR-001**: The dashboard `/u/{username}` MUST show the compatibility card to a signed-in viewer who isn't its owner, and to nobody else.
- **FR-002**: The site MUST serve `/u/{username}/compare` to a signed-in viewer comparing themselves with `{username}`. Signed out: to sign-in and back. Self: redirect to the viewer's dashboard. Unknown or deleted user: the not-found page.

**The level**

- **FR-003**: Compatibility MUST combine two signals, each from 0 to 1:
  - **Shared series**: the number of Crunchyroll series both have watched (any episode with a known length), divided by the smaller of the two people's series counts.
  - **Genre similarity**: how alike the two people's shares of minutes per MyAnimeList genre are (cosine similarity), over deduplicated minutes of matched seasons, the DNA page's rule.

  The score is their average. When either side has no matched genres, the score is the shared-series signal alone.
- **FR-004**: The score MUST map to 5 levels: below 0.2 "Low", from 0.2 "Some", from 0.4 "Good", from 0.6 "High", from 0.8 "Very high". Each level is shown in words, with a 5-step meter that has an accessible name ("4 of 5"). No percentage or raw score is shown.
- **FR-005**: The level MUST be the same from both sides, and computed when the page is viewed, from stored data only. Nothing new is stored.
- **FR-006**: Both people MUST have at least 15 fully watched episodes for a level to show. Otherwise the card and the page say whose history isn't enough yet.

**What's shown**

- **FR-007**: The card MUST show:
  - the level and its meter;
  - the shared series count;
  - up to 3 shared genres (genres in both people's top 5 by share, ordered by the sum of shares);
  - the shared studio both watch most (by summed minutes), when there is one;
  - up to 4 shared series, most watched by both first;
  - a link to the compare page.
- **FR-008**: The compare page MUST show:
  - the summary sentence, the level, and both people's names, usernames and series counts;
  - the genre comparison (up to 6 genres, both shares, a coverage note per person);
  - the shared series with each person's fully watched episodes of each (top 10, then a count of the rest);
  - up to 8 series in the other person's history that the viewer has never watched, most watched by them first, with their MyAnimeList score and up to 3 genres when matched;
  - up to 5 studios both watch, each with the number of shared series it covers.
- **FR-009**: Series titles MUST link to the profile owner's anime pages (`Components.seriesLink`), as elsewhere.
- **FR-010**: The summary sentence MUST read "You and {name} share {n} series", followed by "and a soft spot for {genre}" when there is a shared genre. It is localized, with correct singular and plural.

**Look**

- **FR-011**: The card and page MUST use the Night Broadcast design system and its colour roles: amber for the viewer, rose for the other person, lilac for tags. Charts are solid. The page works at 360px, meets WCAG 2.1 AA, and every text comes from the English and Spanish texts.

### Key Entities

- **Comparison** (computed, not stored): two users, their shared series, genre shares, shared studios, the score and its level.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For two users built from the sample histories, every figure on the card and the page equals an independent query, in English and Spanish.
- **SC-002**: The level is identical from both sides for every pair tested.
- **SC-003**: A compare page between two users with 2,000 watched episodes each is ready in under 1 second.
- **SC-004**: From someone's dashboard, a signed-in user reaches the full comparison in 1 click.
- **SC-005**: The card and page pass the design-system checks (tokens, hooks, contrast) and an impeccable audit with no blocking finding, at 1280px and 360px, in both languages.

## Assumptions

- Dashboards are public, so comparing yourself with a public profile reveals nothing new about them. The viewer's own data shows only to the viewer.
- The extension doesn't change.
- No new stored data; everything is computed per request, like the DNA page.
- The pitch canvas used the old palette; the layout carries over, rebuilt in Night Broadcast through the `impeccable` skill.

## Verbatim Constraints

- Route: `/u/{username}/compare`
- Levels: Low, Some, Good, High, Very high (5 steps), no percentage
- Minimum history: 15 fully watched episodes each (the DNA page's rule)
