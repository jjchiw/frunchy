# Feature Specification: Anime DNA Profile and Shared Design System

**Feature Branch**: `004-anime-dna-design-system`

**Created**: 2026-10-08

**Status**: Draft

**Input**: User description: "Anime DNA profile, and the whole site moved to the shared design system. A public page per user, /u/{username}/dna, linked from the dashboard, that sums up their taste from their Crunchyroll watch history plus the MyAnimeList data of feature 003, showing only figures backed by real data: favourite studio, dominant decade, viewing pace, insignia anime, genre distribution, format split, totals, and a copy-link share button. Every MyAnimeList-based figure covers matched seasons only and shows its coverage. The whole web UI moves from the default theme to the 'Neo-Scrobble Profile' design system, with token names matching the browser extension's, following the designer's mockups from design/BRIEF.md; behaviour, routes, forms, security rules and the dashboard's four figures stay unchanged. Every string in English and Spanish; figures computed when the page is viewed; no change to the extension's API contract."

## Clarifications

### Session 2026-10-08 (decisions the owner made before specifying)

- Q: Is moving the whole site to the design system part of this feature, or a feature of its own? → A: Part of this feature. The DNA page can't look right on a different base, and the site should look like one product with the redesigned extension popup.
- Q: Is there a minimum before the DNA page shows anything? → A: Yes: 15 fully watched episodes (with a known duration). Below that, the page shows the "not enough data yet" state with how many are synced so far (design review of the designer's DNA mockup).
- Q: Which design decisions came out of the mockup review? → A: radius `lg` stays 1rem (as the extension ships); the logo is the clapperboard-and-bars mark, redrawn in the tokens (`design/brand/frunchy-mark.svg`); pace consistency stays "9 of 13 weeks"; the copy stays plain (the site's existing texts), not the mockups' "telemetry" wording.
- Q: Does the DNA page keep a "viewing pace" figure, given that Frunchy keeps only the latest watch of each episode? → A: Yes, as the design shows: fully watched episodes per day over the last 90 days, labelled "steady" or "in bursts", with a "not enough recent data" state (FR-006).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - My Anime DNA (Priority: P1)

A viewer opens `/u/{username}/dna` (linked from the dashboard) and sees, on one page, what kind of anime that person watches: their favourite studio and the runner-up, the decade most of their anime comes from, how much they watch lately, their "insignia" anime, how their watching splits across genres and formats, and the same totals the dashboard shows. Every figure comes from the person's real watch history and the MyAnimeList data Frunchy already holds; nothing is invented, estimated or decorative. Where a figure depends on MyAnimeList, a short note says how much of the history it covers. A "copy link" button copies the page's address to share it.

**Why this priority**: The Anime DNA is Frunchy's identity feature ("your anime identity, in numbers") and the reason feature 003 collected MyAnimeList data. It's the most visible new value.

**Independent Test**: Import the 2026 sample for a user and enrich it with the stand-in for MyAnimeList (feature 003's fixtures). The DNA page shows each figure, and each one equals a hand count over the same data, in English and in Spanish. A user with no history sees the "not enough data yet" state.

**Acceptance Scenarios**:

1. **Given** a user with matched watch history, **When** anyone opens their DNA page, **Then** it shows: favourite studio and runner-up; dominant decade with its share; viewing pace (or its "not enough data" state); the insignia anime with its poster and episode count; the top 5 genres plus "Others", each with its share and its most-watched anime; the format split (TV, Movie, OVA, ONA, Special, and any other format MyAnimeList reports); and total hours and days.
2. **Given** the same user, **When** the dashboard and the DNA page are compared, **Then** the DNA page's hours and days equal the dashboard's figures exactly.
3. **Given** a figure based on MyAnimeList data, **When** it's shown, **Then** it counts matched seasons only and shows its coverage ("based on 93% of your watch time").
4. **Given** a 2-hour movie and a 24-minute episode, **When** studio, decade, genre and format figures are computed, **Then** each counts by the minutes watched, so the movie weighs five times the episode.
5. **Given** a user who watched both the original and a dub of the same episode, **When** the figures are computed, **Then** that episode counts once.
6. **Given** a user with fewer than 15 fully watched episodes, **When** their DNA page is opened, **Then** it shows a "not enough data yet" state with how many they have out of 15 and how to get there (sync with the extension; MyAnimeList data arrives shortly after), with no broken or empty figures. A user with 15 or more but no matched seasons sees totals, insignia and pace, and each MyAnimeList figure shows its own "not enough data" state.
7. **Given** an unknown username, **When** `/u/{username}/dna` is opened, **Then** the site's "User not found" page is shown with a not-found status, as for the dashboard.
8. **Given** a viewer on the DNA page, **When** they press "copy link", **Then** the page's full address is copied and a short confirmation appears; nothing else is shared or generated.
9. **Given** a viewer on the dashboard, **When** they look for the DNA, **Then** a visible link leads to `/u/{username}/dna`, and the DNA page links back to the dashboard.

---

### User Story 2 - The site looks like one product (Priority: P2)

Every page of the website (home, sign-in, sign-up, claim an account, confirm it's you, account, connect the extension, delete account, the message pages and the dashboard) uses the "Neo-Scrobble Profile" design system: its typeface, colours, surfaces, spacing, radii and components (metric tiles, genre bars, status pills, buttons, inputs, alerts, empty and loading states), with the same token names as the browser extension, so the website and the popup feel like one product. Each page follows the designer's mockup for it (`design/BRIEF.md`) when one exists. Nothing a user can do changes: same addresses, same forms and fields, same messages, same security behaviour, same figures.

**Why this priority**: The DNA page needs this base to look right, and the extension popup has already moved to it; without it the site looks like a different product. It delivers no new capability by itself, so it comes after User Story 1.

**Independent Test**: Run every existing automated test unchanged in behaviour (the same routes, forms, redirects, status codes and figures), then review every page in English and Spanish, on a desktop width and a 360px phone width, against the design system and the available mockups, with the design audit (impeccable) finding nothing blocking.

**Acceptance Scenarios**:

1. **Given** any page of the site, **When** it renders, **Then** it uses only the design system's typeface, colours and components, and no leftover default-theme styling is visible.
2. **Given** the design system tokens, **When** they're compared with the extension's (`browser-extension/src/styles/tokens.css`), **Then** every token both sides use has the same name and value.
3. **Given** any form (sign-up, claim, display name, connect extension, delete account), **When** it's used, **Then** its fields, validation messages, protections against forged submissions, rate limits and results are exactly as before.
4. **Given** the dashboard, **When** it renders with the new design, **Then** its four figures, recent cards and "Top genres" show the same values as before.
5. **Given** a page in Spanish (whose texts run about 30% longer), **When** it's shown at 360px width, **Then** no text is cut off or overlaps, and nothing scrolls sideways.
6. **Given** any page, **When** checked for accessibility, **Then** text and controls meet WCAG 2.1 AA contrast, every control is reachable and visible by keyboard, and motion respects the viewer's "reduce motion" setting.

---

### Edge Cases

- **Low coverage**: a user whose seasons are mostly unmatched still sees the figures that have data, with their (low) coverage stated; a MyAnimeList-based figure with no matched minutes at all shows its own "not enough data" state instead of zeroes.
- **Ties**: equal values (two studios, two decades) are ordered by name, so the page is stable between views.
- **Several studios, genres or formats on one anime**: an anime with several studios credits each of them; with several genres it counts in each (see FR-007 for how genre shares still add up to 100%).
- **Unknown year or format**: an anime with no year doesn't count toward the decade; one with no format doesn't count toward the format split; both lower that figure's stated coverage.
- **A season matched to an anime later marked missing on MyAnimeList**: its stored details still count, as on the dashboard.
- **Partly watched episodes**: count toward minutes up to how far they were watched, as the dashboard's hours do.
- **Unresolved episodes** (no duration yet): excluded everywhere, as on the dashboard.
- **Recent activity but a long history**: viewing pace looks only at the last 90 days, so a first sync that brings years of history doesn't inflate it.
- **Deleted user**: the DNA page shows "User not found", like the dashboard.
- **MyAnimeList unreachable**: the page loads from data Frunchy already holds, as fast as usual.
- **Mockup missing for a page when it's built**: the page uses the design system's components and is revisited when the mockup arrives.

## Requirements *(mandatory)*

### Functional Requirements

**DNA page (User Story 1)**

- **FR-001**: The site MUST serve a public DNA page per user at `/u/{username}/dna`, visible to anyone with the link (the same visibility as the dashboard), in the viewer's language.
- **FR-002**: The dashboard MUST link to the user's DNA page, and the DNA page MUST link back to the dashboard.
- **FR-003**: Weighting: every MyAnimeList-based figure (studio, decade, genres, format, insignia) MUST weigh each watched episode by its minutes watched (the whole duration when fully watched; up to how far it was watched otherwise), counting only episodes with a known duration. The dashboard's figures and its "Top genres" stay as they are.
- **FR-004**: Every MyAnimeList-based figure MUST count matched seasons only, and MUST state its coverage: the share of the user's minutes watched that the figure is based on.
- **FR-005**: An episode watched both in its original language and in a dub (seasons that share one MyAnimeList entry, same episode number) MUST count once, with the larger of the two minutes watched.
- **FR-006**: Viewing pace MUST show the fully watched episodes per day over the last 90 days (episodes ÷ 90), with a consistency label: "steady" when at least 9 of the 13 weeks in the window have a watched episode, "in bursts" otherwise. With fewer than 10 fully watched episodes in the window it MUST show "not enough recent data" instead of a number.
- **FR-007**: Genre distribution MUST show the 5 genres with the most minutes, plus "Others" for the rest, each with its share and its most-watched anime. Because an anime counts in each of its genres, shares MUST be over the sum of all genre minutes, so the 6 rows add up to 100%. Explicit genres (MyAnimeList's adult categories) MUST NOT be shown.
- **FR-008**: Favourite studio MUST be the studio with the most minutes, with the runner-up; every studio credited on an anime counts. Its share of matched minutes MUST be shown.
- **FR-009**: Dominant decade MUST be the decade (by the anime's start year) with the most minutes, with its share of the minutes whose year is known.
- **FR-010**: Insignia anime MUST be the Crunchyroll series with the most minutes watched by the user, shown with its title, a poster (the MyAnimeList image of its most-watched matched season, or else a Crunchyroll episode thumbnail) and its count of fully watched episodes. It doesn't need MyAnimeList data to appear.
- **FR-011**: Format split MUST show each format's share of matched minutes (TV, Movie, OVA, ONA, Special, and any other format MyAnimeList reports, by name), largest first.
- **FR-012**: Totals MUST show hours and days watched, computed exactly as the dashboard computes them.
- **FR-013**: A "copy link" control MUST copy the DNA page's absolute address and confirm it. No image, file or external share is generated.
- **FR-014**: A user with fewer than 15 fully watched episodes (with a known duration) MUST see a page-level "not enough data yet" state showing their count out of 15; each MyAnimeList-based figure with no matched minutes MUST show its own "not enough data" state.
- **FR-015**: An unknown or deleted username MUST get the site's "User not found" page with a not-found status.
- **FR-016**: Every figure MUST be computed from Frunchy's stored data when the page is viewed, with no call to MyAnimeList, and MUST be identical after a full rebuild of the user's history (`user-rebuild`) and after re-sending the same submissions.
- **FR-017**: The page MUST NOT show elements without a real data source: no radar or "pillar" scores, "algorithmic diagnosis", affinity percentage, personal ratings or Hall of Fame scores, rewatch tags, followers, badges, levels or PRO marks, linked MyAnimeList account, avatars or image export.

**Design system (User Story 2)**

- **FR-018**: Every page MUST use the "Neo-Scrobble Profile" design system (`design/neo_scrobble_profile/DESIGN.md`). Where that document contradicts itself, its token list wins: primary `#4cd7f6`, secondary `#c0c1ff`, radius `lg` = 1rem. `DESIGN.md` MUST be corrected to say so.
- **FR-019**: Design tokens MUST use the same names as the browser extension's CSS custom properties (`--color-*`, `--text-<style>-*`, `--radius-*`, `--space-*`), with the same values for every token both use.
- **FR-020**: The site MUST provide one shared set of components used by every page: metric tile, genre bar, status pill, primary/secondary/destructive buttons, provider sign-in buttons, text input with hint and error, success and error alerts, empty state, loading/processing state, and a confirmation layout for destructive actions.
- **FR-021**: Each page MUST follow its mockup from `design/` when one exists. A page without a mockup when it's built MUST use the shared components and be listed for revisit.
- **FR-022**: Routes, forms and their fields, validation, forged-submission protection, rate limits, redirects, status codes, security events and every figure MUST behave exactly as before the redesign.
- **FR-023**: Every page MUST work from a 360px-wide phone to a wide desktop, in English and Spanish, with no clipped or overlapping text and no sideways scrolling.
- **FR-024**: Every page MUST meet WCAG 2.1 AA contrast, have visible keyboard focus on every control, and respect "reduce motion".
- **FR-025**: The site's mark is the clapperboard-and-bars mark (`design/brand/frunchy-mark.svg`), next to the "Frunchy" wordmark, replacing the emoji logo; it's also the favicon.

**Constraints**

- **FR-026**: Every user-facing text MUST come from the site's text sources in English and Spanish; views contain no literal text.
- **FR-027**: The extension's API contract MUST NOT change. The DNA isn't exposed to the extension; the extension may link to the page.
- **FR-028**: No new stored data unless a figure can't be computed from what Frunchy already holds; any new stored data MUST be rebuildable from the raw imports and the stored MyAnimeList responses.

### Key Entities

- **DNA profile** (computed, not stored): a user's figures at view time: favourite studio and runner-up, dominant decade, viewing pace, insignia anime, genre distribution, format split, totals, and each MyAnimeList-based figure's coverage.
- **Watched minutes**: per user and episode, the minutes that count (full duration if fully watched, otherwise up to how far it was watched), deduplicated across a season and its dubs.
- **Design token**: a named value of the design system (colour, type style, radius, spacing) shared by the website and the extension.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the 2026 sample, every DNA figure equals a hand count over the same data, in English and Spanish.
- **SC-002**: For the owner's real history (about 6,000 watched episodes), the DNA page is ready in under 1 second on the development machine.
- **SC-003**: The dashboard's four figures for `/u/jjchiw` (327 / 5,921 / 2,367.6 h / 98.6 days) and every existing automated behaviour test are unchanged after the redesign.
- **SC-004**: Every page passes the design audit with no blocking finding, meets WCAG 2.1 AA contrast, and shows no clipping at 360px in both languages.
- **SC-005**: 100% of the tokens the website and the extension both use have identical names and values.
- **SC-006**: 0 elements on the DNA page lack a real data source, and 0 user-facing literals exist outside the text sources.
- **SC-007**: A viewer can go from a dashboard to its DNA page and copy its link in 2 clicks.

## Assumptions

- The DNA page is public like the dashboard (dashboards are already public by link); no new privacy setting is introduced.
- The designer's mockups (`design/BRIEF.md`) arrive while this feature is built; pages are not blocked on them (FR-021). The DNA mockup is redone per brief section 5.2.
- Viewing pace uses calendar days on the server's clock (UTC) over the last 90 days; the small error at day boundaries doesn't change episodes per day meaningfully.
- "Explicit genres" means MyAnimeList's explicit genres (Ecchi, Erotica, Hentai). Feature 003 already never matches adult entries; none of these genres appears in the owner's data today. "Award Winning", which MyAnimeList lists as a genre, is shown like any other genre, as the dashboard does.
- The project constitution names "Tailwind/DaisyUI from CDNs, no frontend build step"; dropping the default theme keeps the no-build-step rule, and the plan updates the constitution's wording if needed.
- MyAnimeList fields not requested today (rank, popularity, members, source material) stay out of scope.
- Dependencies: feature 003's stored MyAnimeList data (93% of the owner's seasons matched), the extension's tokens file, and the designer's mockups.
