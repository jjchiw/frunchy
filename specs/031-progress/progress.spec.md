# Feature Specification: Progress

**Feature Branch**: `031-progress`

**Created**: 2026-11-06

**Status**: Draft

**Input**: "It would be good to list the series that were dropped: on MyAnimeList there are 12 episodes but I only
watched 4. How many completed and how many unfinished, with a list, because sometimes I forget about them." The owner
chose: a new profile tab, and the list for the owner only. With the designer.

## Clarifications

### Session 2026-11-06 (decided in the autonomous run; the owner can overturn any of them)

- Q: What is one "show" here? → A: **A MyAnimeList entry** (an anime season: The Apothecary Diaries S1 and S2 are two).
  Several Crunchyroll seasons can map to one entry (One Piece, Eyeshield 21: feature 003's "no TV sequel" rule), so
  the entry, not the Crunchyroll season, is the unit. Episodes are counted the DNA way: a dub and its original once
  (the original season plus the episode number), fully watched only.
- Q: Started? → A: An entry with **at least one fully watched episode**. A few minutes of an episode doesn't start a
  show.
- Q: The three states? → A: For each started entry:
  - **Completed**: MyAnimeList knows its episode count (more than 0) and the person fully watched at least that many
    (Crunchyroll sometimes has an extra recap episode, so "at least").
  - **Watching**: not completed, and either the newest watch of the entry was in the last **30 days**, or the entry is
    still airing (`mal_anime.status = currently_airing`) and the newest fully watched episode aired in the last
    **21 days** (keeping up week by week; air dates from feature 027).
  - **Unfinished**: every other started entry. The word is "Unfinished", not "Dropped": often nobody decided to drop
    it.
- Q: A still-airing or unknown total? → A: It can't be Completed (no final count). It shows "4 episodes" without "of".
- Q: Long runners (feature 027's rule: more than 100 episodes)? → A: Same states, but marked as long runners so the
  page never draws a 266-of-1,155 bar that reads as failure (the designer decides how they show).
- Q: Shows with no MyAnimeList match? → A: No total to compare, so they're left out of the three counts and the lists;
  a note says how many started Crunchyroll seasons have no MyAnimeList details.
- Q: Where does the person pick up? → A: For Watching and Unfinished entries: "4 of 12 episodes", the time of the
  newest watch, and **the next episode**: the lowest episode number above the highest fully watched one, within the
  entry's Crunchyroll seasons (numbers restart per Crunchyroll season, so it is per season: the season of the newest
  watch). The next episode links to its Frunchy episode page when the catalog knows it (someone watched it), else to
  the series on Crunchyroll. The title links to the public series page `/anime/{seriesId}`.
- Q: Who sees what? → A: The tab and its **three counts** follow the profile's visibility (feature 017/021's gate,
  like every `/u/{username}` page). **The Watching and Unfinished lists are the owner's only** (a reminder, not a
  public record of what they stopped watching): anyone else sees the counts and a short line, never a title. The
  Completed state is a count for everyone; the owner also gets the list of the most recently completed (12 newest).
- Q: Order? → A: Watching and Unfinished: newest watch first. Completed: newest completion first (the newest watch).
- Q: Route? → A: `GET /u/{username}/progress`, a fourth tab "Progress" ("Progreso") after Year in review.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The owner sees what they left unfinished (Priority: P1)

The owner opens Progress and sees how many shows they completed, are watching and left unfinished, and the list of
unfinished ones with where they stopped and when, newest first, with a link to pick up.

**Acceptance Scenarios**:

1. **Given** an entry of 12 episodes with episodes 1-4 fully watched 3 months ago, **When** the owner opens Progress,
   **Then** it is Unfinished: "4 of 12", the date, and next episode 5.
2. **Given** an entry of 12 with all 12 fully watched, **Then** it is Completed (also with 13 watched).
3. **Given** an entry of 12 with 6 watched, the newest 10 days ago, **Then** it is Watching.
4. **Given** an airing entry (no final count) whose newest fully watched episode aired 5 days ago, **Then** Watching;
   aired 2 months ago and watched 2 months ago, **Then** Unfinished.
5. **Given** a dub and its original of the same episodes, **Then** they count once.
6. **Given** One Piece-like entries split across Crunchyroll seasons, **Then** one entry, its episodes summed.
7. **Given** a watch with only a few minutes of episode 1, **Then** the entry isn't started and isn't listed.
8. **Given** unmatched started Crunchyroll seasons, **Then** a note gives their count and none is listed.
9. **Given** the next episode is in the catalog, **Then** it links to its episode page; otherwise to the Crunchyroll
   series.

### User Story 2 - Others see counts only (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a visitor allowed to see the profile, **When** they open Progress, **Then** they see the three counts and
   no series title, no list and no next episode.
2. **Given** a profile they can't see, **Then** the plain 404 (or the sign-in redirect for Members), exactly as the
   other tabs.
3. **Given** an operator or the root who isn't the owner, **Then** counts only, like anyone.

### User Story 3 - A fourth tab (Priority: P2)

Every profile page's tab bar has Progress after Year in review; on the Progress page it is the current one.

### Edge Cases

- No started entry: an empty state (owner: "Nothing started yet" with a pointer to sync; others: a plain line).
- Rewatches: a re-watched episode is still one episode (the stored watch is the latest).
- A movie: Completed when fully watched (MyAnimeList count 1); a partly watched movie isn't started.
- Hundreds of entries: the lists are long; the designer decides how they collapse.

## Requirements *(mandatory)*

- **FR-001**: `GET /u/{username}/progress` behind `Profiles.gate` (the same order as the DNA page: username, lookup,
  gate before any other query); its figures computed per request, nothing stored, order-independent like the rest.
- **FR-002**: The states, counts, next episode and notes exactly as clarified; all in one statement over the person's
  rows plus an F# fold (no per-row queries).
- **FR-003**: Counts for everyone the gate lets in; Watching, Unfinished and Completed lists, next episodes and
  titles only for the owner (`PageAccess` owner cases), never in another viewer's HTML.
- **FR-004**: The Progress tab on every profile page (`ProfileTab.Progress`, `data-progress-link`).
- **FR-005**: Texts in English and Spanish; tests read `data-*` hooks only; hooks in `contracts/hooks.md`.
- **FR-006**: CLAUDE.md route line, `changelog.d/031-progress.md` (Added), and the snapshot harness shoots the page.

## Success Criteria *(mandatory)*

- **SC-001**: On the owner's real history, the shows they forgot appear under Unfinished with where to resume.
- **SC-002**: No visitor ever sees a title of an unfinished show.
- **SC-003**: The page renders as fast as the DNA page; the full suite passes.

## Assumptions

- MyAnimeList's episode count is the series' length; when it's wrong, the operator fixes the match (feature 007).
