# Feature Specification: MyAnimeList Enrichment

**Feature Branch**: `003-mal-enrichment`

**Created**: 2026-10-02

**Status**: Draft

**Input**: User description: "MyAnimeList enrichment via Jikan: match the Crunchyroll catalog to MyAnimeList anime and store their metadata (genres, themes, demographics, studios, format, year/season, score, original title, images) in Frunchy's database, so the dashboards and the future Anime DNA (feature 004) can use it. Respect Jikan's rate limits, keep what was fetched so Jikan isn't called on every view, and retry resiliently when Jikan or MyAnimeList fails."

## Clarifications

### Session 2026-10-02

- Q: What is matched to MyAnimeList, each series or each season? → A: Each Crunchyroll season, movies included (they appear as seasons, e.g. "JUJUTSU KAISEN 0"), gets its own MyAnimeList entry. The series' first season is found by search, and later seasons and movies are found by following MyAnimeList's sequel and related links, comparing season titles. Season numbers aren't used to match, because Crunchyroll's numbering is irregular (JUJUTSU KAISEN Season 3 is numbered 5). A dubbed season ("Season 01 (English Dub)") shares the entry of the original-language season it dubs.
- Q: How much MyAnimeList data does the dashboard show in this feature? → A: The score and up to 3 genres on each recently-watched card, plus a "Top genres" section: the 5 genres with the most watched episodes, each with its share and episode count. A line says how much of the history is covered by matched seasons. Themes, demographics, studios and everything else visual wait for feature 004.
- Q: What do "Top genres" count? → A: Fully watched episodes, the same rule as the "Episodes watched" figure. Shares are per genre over the fully watched episodes in matched seasons. Weighting by watch time is left to feature 004.
- Q: When there's a backlog, which seasons are enriched first? → A: The most recently watched first: seasons ordered by their latest watch across all users. New lookups come before refreshes, and an operator's request goes to the front.
- Q: Jikan is shutting down (and was unreachable for a whole evening); where does the data come from? → A: MyAnimeList's official API v2, authenticated with a free client id kept in Bella Baxter (`Frunchy__Mal__ClientId`), never in the repository. Jikan stays available as an alternative provider. Stored responses record which provider they came from. Without a client id the site still runs, with enrichment off.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The catalog learns what each anime is (Priority: P1)

Frunchy's catalog today only knows what Crunchyroll's history pages say: titles, durations and thumbnails. In the background, without anyone doing anything, Frunchy looks up each Crunchyroll season it knows, movies included, on MyAnimeList (through MyAnimeList's official API) and records the match. It then keeps that anime's MyAnimeList details: genres, themes, demographic, studios, format (TV, movie, OVA…), year and season, score, original title and image.

Each anime is looked up once. The details are kept in Frunchy, so viewing a dashboard never calls the external service. Imports and dashboards keep working exactly as today, whether enrichment has run, is running, or the external service is down.

**Why this priority**: Every later statistic (genre breakdown, favourite studio, dominant decade, format split, and the Anime DNA of feature 004) needs this data. Without it, the mockups in `design/` can't be built.

**Independent Test**: With a stand-in for the external service, import the 2026 sample for a user and let enrichment run. Every season in the sample ends up either matched, with its details stored, or recorded as unmatched or ambiguous with a reason. The user's dashboard figures are identical before and after enrichment. Running enrichment again makes no new lookups for the anime already done.

**Acceptance Scenarios**:

1. **Given** a Crunchyroll season that Frunchy hasn't looked up yet, **When** enrichment runs, **Then** the season is matched to exactly one MyAnimeList anime, or recorded as "no match" or "ambiguous" with the candidates considered. It is never matched silently on a guess.
2. **Given** a series whose first season is matched, **When** its later seasons and movies are matched, **Then** each gets its own MyAnimeList entry (for example JUJUTSU KAISEN, JUJUTSU KAISEN Season 2 and the movie JUJUTSU KAISEN 0 each get theirs), found through that entry's sequel and related links before any new search.
3. **Given** a dubbed season ("Season 01 (English Dub)"), **When** it's matched, **Then** it shares the MyAnimeList entry of the original-language season it dubs, without a lookup of its own.
4. **Given** a matched season, **When** its details are stored, **Then** Frunchy keeps the genres, themes, demographic, studios, format, year and season, episode count, score, original and English titles, image and a link to the MyAnimeList page, plus the external service's full response, so these fields can be re-derived later without asking again.
5. **Given** an import arrives with series Frunchy has never seen, **When** the import is processed, **Then** it completes as before, without waiting for enrichment. The new seasons are queued for enrichment and picked up afterwards, the most recently watched first, so a new user's recent cards fill in before the rest of the backlog.
6. **Given** the external service answers "too many requests" or a temporary error (server error, gateway timeout, maintenance, no answer), **When** that happens for a lookup, **Then** the lookup is retried a few times with increasing waits, honouring any "retry after" the service gives. If it still fails, the work is kept and tried again later with growing delays.
7. **Given** the external service is down for hours, **When** Frunchy keeps running or is restarted during the outage, **Then** no pending lookup is lost. Attempts slow down to a few per hour, and enrichment resumes on its own once the service is back.
8. **Given** a lookup the external service definitively answers with "not found", or a search with no acceptable candidate, **When** it is recorded, **Then** it stays "no match" and isn't retried automatically. It's retried only when the operator asks (User Story 3) or the catalog's title for that season changes.
9. **Given** enrichment is running through a large backlog (the real catalog: 336 series, 501 seasons), **When** it makes requests, **Then** it never sends more than 1 request per second on average.
10. **Given** a user deletes their account, **When** the deletion completes, **Then** the MyAnimeList data stays: it is shared catalog data, not personal data.

---

### User Story 2 - My dashboard shows what kind of anime I watch (Priority: P2)

Visiting a public dashboard (`/u/{username}`), a viewer now sees MyAnimeList information where it exists. Each recently-watched card shows the season's MyAnimeList score and up to 3 genres, linking to its MyAnimeList page. A new "Top genres" section shows the 5 genres with the most watched episodes, each with its share and episode count, and says how much of the history it covers ("based on 92% of watched episodes"). Seasons that aren't matched yet show what they show today.

**Why this priority**: It makes the enrichment visible and gives the first taste of the "what kind of watcher am I" story, but the data (User Story 1) has value on its own for feature 004.

**Independent Test**: With a stand-in for the external service, import the 2026 sample and enrich it, with one season deliberately left unmatched. The dashboard shows score and genres for matched seasons and today's view for the unmatched one. The top genres and their counts equal a hand count over the matched seasons' watched episodes, in English and in Spanish.

**Acceptance Scenarios**:

1. **Given** a recently-watched episode whose season is matched, **When** the dashboard is shown, **Then** its card shows the MyAnimeList score (when MyAnimeList has one) and up to 3 genres, alongside today's information, with a link to the MyAnimeList page.
2. **Given** a recently-watched episode whose season isn't matched (yet, ambiguous, or no match), **When** the dashboard is shown, **Then** it looks exactly as it does today for that episode, with no error and no empty placeholder.
3. **Given** the external service is down, **When** anyone views a dashboard, **Then** it loads as fast as today, from the data Frunchy already holds.
4. **Given** a viewer whose browser prefers Spanish, **When** the dashboard is shown, **Then** every label Frunchy adds is in Spanish. Genre and studio names are shown as MyAnimeList provides them.
5. **Given** the figures shown today (series, episodes, hours, days), **When** enrichment adds data, **Then** those figures don't change.
6. **Given** a user with watched episodes in matched seasons, **When** the dashboard is shown, **Then** the "Top genres" section lists up to 5 genres ordered by fully watched episodes (ties by name), counted with the same rule as the "Episodes watched" figure. Each shows its episode count and its share of the fully watched episodes in matched seasons, plus the share of all fully watched episodes those seasons cover. An episode counts once for each genre of its season, so the shares can add up to more than 100%, and the section says so.
7. **Given** a user with no watched episodes in a matched season, **When** the dashboard is shown, **Then** the "Top genres" section isn't shown at all.

---

### User Story 3 - The operator reviews and fixes matches (Priority: P2)

Some matches can't be decided automatically: titles differ between Crunchyroll and MyAnimeList, several seasons look alike, or a Crunchyroll season isn't on MyAnimeList at all. The operator lists the seasons that are unmatched, ambiguous or failed, with their candidates and reasons. They set the right MyAnimeList anime by hand for a season, mark a season as "not on MyAnimeList", or ask Frunchy to look a season (or a whole series) up again. Manual decisions are never overwritten by automatic matching.

**Why this priority**: Automatic matching won't be perfect, and wrong genres would mislead every later statistic. But the feature still delivers value before any manual fix.

**Independent Test**: With a stand-in service that returns two equally good candidates for one season, enrichment records it as ambiguous. The operator lists it, sets one of the candidates by hand, and the season becomes matched with that anime's details. A later automatic re-run leaves it as set.

**Acceptance Scenarios**:

1. **Given** seasons in any state other than matched, **When** the operator lists them, **Then** each shows its Crunchyroll series and season title, state, reason, number of attempts, next attempt time and, if ambiguous, the candidates considered (MyAnimeList id, title, format, year).
2. **Given** a season and a MyAnimeList anime id, **When** the operator sets the match by hand, **Then** the season (and the dubs that share it) is matched to that anime, its details are fetched (or reused if already held), and the match is marked manual.
3. **Given** a manually matched season, or one marked "not on MyAnimeList" by hand, **When** automatic matching runs again (a re-run, a title change, or improved matching rules), **Then** the manual decision stays.
4. **Given** a season or a series in any state, **When** the operator asks to look it up again, **Then** it's queued right away and processed with the next batch.
5. **Given** a MyAnimeList id that doesn't exist, **When** the operator tries to set it by hand, **Then** it's refused with an explanation and the season keeps its previous state.
6. **Given** the matching rules improve, **When** the operator asks to re-match all automatic matches, **Then** Frunchy re-decides them, reusing the stored responses wherever possible instead of asking the service again.

---

### User Story 4 - Details stay current without waste (Priority: P3)

Anime details change slowly: a score moves, an airing show gains episodes or ends. Frunchy refreshes the details it holds every 30 days, and every 7 days for shows still airing. It asks the service whether anything changed, so unchanged data costs almost nothing. A refresh never removes data Frunchy already shows.

**Why this priority**: The first fetch is what matters; stale data for a few weeks harms little. But without a refresh, airing shows would stay frozen forever.

**Independent Test**: With a stand-in service and a controllable clock, enrich a season. Advance the clock past the refresh age; its anime is refreshed once. When the stand-in says "not modified", the stored details keep their values and get a new "checked" time.

**Acceptance Scenarios**:

1. **Given** details older than the refresh age (30 days; 7 days while airing), **When** enrichment runs, **Then** they're refreshed. Details younger than that aren't requested again.
2. **Given** the service reports the details unchanged, **When** a refresh happens, **Then** the stored details are kept and only the "last checked" time moves.
3. **Given** a refresh fails (any temporary error or outage), **When** it's recorded, **Then** the existing details keep being used and the refresh is retried later, with the same growing delays as first lookups.
4. **Given** a refresh finds the anime removed from MyAnimeList ("not found"), **When** it's recorded, **Then** the last known details are kept and flagged, and the operator sees it in the list.

---

### Edge Cases

- **Seasons on MyAnimeList**: MyAnimeList usually has one entry per season and per movie, and so does Frunchy (Clarifications). The real catalog has 336 series and 501 seasons, 43 of them dubs in other languages.
- **Movies under a series**: franchise movies counted with their series (feature 001) appear as seasons ("CHAINSAW MAN – THE MOVIE: REZE ARC", "BLUE LOCK THE MOVIE -EPISODE NAGI-"). Each gets its own movie entry, so its format is "movie", not the series' TV format.
- **A split MyAnimeList entry**: some Crunchyroll seasons cover two MyAnimeList entries (a season aired in two parts, or "cours"). The season is matched to the entry its first episodes belong to, normally the first part. Exact splitting is later work.
- **A season whose series' first season can't be matched**: it's looked up on its own by its season title, so one unmatched season doesn't block the others.
- **Episodes outside any season**: unresolved stubs (feature 001) have no season. They stay out of enrichment until a later page brings their season.
- **Two Crunchyroll seasons, one anime**: several Crunchyroll seasons may match the same MyAnimeList anime (dubs, or the same show listed under two series). That's allowed, and the anime's details are held once.
- **Not anime, or not on MyAnimeList**: Crunchyroll listings that aren't on MyAnimeList (concerts, live action, some specials) end as "no match" after a search, not as errors.
- **Adult entries**: MyAnimeList entries rated for adults only (hentai) are never candidates. Crunchyroll doesn't carry them, so a candidate like that is always a wrong match.
- **Title changes**: when a later import changes a series' or season's Crunchyroll title, an automatic "no match" or ambiguous season is looked up again. Matched and manual seasons stay as they are.
- **Refused by MyAnimeList**: MyAnimeList answers "forbidden" both to an unknown client id and to its overload protection. It's handled like a temporary failure (backed off, shown to the operator), never as a final answer.
- **Restart mid-lookup**: a lookup interrupted by a restart is picked up again later, as with imports in feature 001. Running two Frunchy processes never looks up the same season twice at once.
- **Unexpected response shape**: a response that can't be read is recorded as a failure with the reason, kept for the operator, and retried later. It never stops the rest of the backlog.
- **Missing fields**: MyAnimeList fields that are unknown (no score yet, no year, no studio) are stored as unknown. A score of 0 from the service means "no score", not zero.

## Requirements *(mandatory)*

### Functional Requirements

**Matching**

- **FR-001**: Every Crunchyroll season in the catalog, movies included, MUST get an enrichment state: pending, matched, ambiguous, no match, or failed (with the reason and number of attempts). Seasons MUST NOT be skipped silently.
- **FR-002**: A new season arriving through an import MUST be queued for enrichment automatically. Import processing MUST NOT wait for or depend on enrichment.
- **FR-002a**: Pending work MUST be taken in this order: operator requests first, then new lookups by the season's latest watch across all users (most recent first), then refreshes (oldest details first). Work waiting for its next attempt time after a failure is skipped until then.
- **FR-003**: A series' first season (the lowest-numbered original-language season that isn't a movie) MUST be matched by searching MyAnimeList with the series title, and with the season title when it differs. The search terms MUST be compared against each candidate's titles (main, English, Japanese romanization and synonyms) after normalizing case, punctuation and season or dub markers.
- **FR-003a**: The series' other original-language seasons and movies MUST be matched first among the entries reachable from the matched first season through MyAnimeList's sequel, prequel, side-story and related links, by comparing season titles and format. A new search, by season title, is made only when no linked entry qualifies.
- **FR-003b**: A dubbed season MUST share the match of the original-language season of the same series that it dubs (same season title once dub markers are removed, or the same season number when titles don't help). It MUST NOT be looked up separately. When no original-language season exists, it's matched like an original.
- **FR-004**: A season MUST be matched automatically only when exactly one candidate matches its title after normalization and has a plausible format (a movie season only to a movie entry; other seasons to TV, ONA, OVA or special). Otherwise the season is recorded as "ambiguous", with up to 5 candidates kept for the operator, or "no match" when none qualify.
- **FR-005**: Adult-only MyAnimeList entries MUST never be candidates.
- **FR-006**: Matches MUST record how they were decided (automatic or manual) and when. Manual decisions (a set match, or "not on MyAnimeList") MUST NOT be changed by automatic matching.

**Stored details**

- **FR-007**: For each matched anime, Frunchy MUST store: MyAnimeList id and page link, main, English and Japanese titles, format, episode count, airing status, start date, year and season, score, genres, themes, demographics, studios and the main image link. It MUST also store the full response received, so that fields can be re-derived without a new request.
- **FR-008**: Search responses used to decide a match MUST be stored as well, so the operator can see the candidates and matching can be re-decided without new requests (FR-021).
- **FR-009**: MyAnimeList details MUST be shared catalog data, held once per anime no matter how many seasons or users point at it, and MUST NOT be deleted when a user is deleted.

**Respecting the service**

- **FR-010**: Frunchy MUST stay well under the service's limits: at most 1 request per second on average, across everything it does (lookups, refreshes and manual requests). Jikan publishes 3 per second and 60 per minute; MyAnimeList's official API publishes none, so the same margin applies.
- **FR-011**: Page views and imports MUST NEVER call the service. Only background enrichment and operator commands do.
- **FR-012**: An anime whose details are held and younger than the refresh age MUST NOT be requested again. Refreshes MUST ask whether the details changed, so an unchanged anime returns without a new copy.

**Resilience**

- **FR-013**: A temporary failure ("too many requests", server error, gateway timeout, maintenance, timeout, connection failure) MUST be retried up to 3 times with increasing waits and jitter, honouring any "retry after" the service gives.
- **FR-014**: When short retries are used up, the work MUST stay pending with a next attempt time that grows with each failure: starting around a minute, doubling, capped at 6 hours. It MUST survive restarts.
- **FR-015**: While the service keeps failing (5 failed lookups in a row), Frunchy MUST pause all requests for 5 minutes, rather than trying every pending season in turn, and then send a single probe. When the probe fails, the pause starts again; when it succeeds, work resumes on its own.
- **FR-016**: A definite "not found", or a search with no acceptable candidate, MUST be recorded as final ("no match") and not retried automatically, except when the season's Crunchyroll title changes or the operator asks.
- **FR-017**: Every failure MUST be recorded per season with its reason and time. A failure for one season MUST NOT stop the others.

**Dashboard**

- **FR-018**: For matched seasons, each recently-watched card on the public dashboard MUST show the MyAnimeList score (only when MyAnimeList has one) and up to 3 genres in MyAnimeList's order, with a link to the anime's MyAnimeList page. All of it comes from data Frunchy already holds. Unmatched seasons MUST look exactly as today. The existing figures MUST NOT change.
- **FR-018a**: The dashboard MUST show a "Top genres" section: the 5 MyAnimeList genres with the most fully watched episodes among the user's episodes in matched seasons (ties by name). It counts with the same rule as the "Episodes watched" figure, so a movie counts as one. Each genre shows its episode count and its share of those episodes. The section MUST also show the share of all the user's fully watched episodes that matched seasons cover. It MUST NOT be shown when no fully watched episode is in a matched season. Only genres count here, not themes or demographics.
- **FR-019**: Every label added to pages MUST come from the English and Spanish texts. Genre, theme and studio names are shown as MyAnimeList provides them.

**Operator**

- **FR-020**: Operator commands MUST let the operator:
  - list seasons that aren't matched, with state, reason, attempts, next attempt and candidates;
  - show a series' seasons with their matches and details;
  - set a season's match by hand by MyAnimeList id, refused if the id doesn't exist;
  - mark a season "not on MyAnimeList";
  - queue one season, or every season of a series, for a new lookup;
  - re-match all automatic matches (FR-021).
- **FR-021**: Matches and stored fields MUST be rebuildable: re-deciding automatic matches and re-deriving the stored fields MUST work from the stored responses, without new requests, and MUST give identical results when run twice.

**Refresh**

- **FR-022**: Held details MUST be refreshed after 30 days, or 7 days while the anime is airing, through the same paced, resilient path. A failed or "not found" refresh MUST keep the last known details.

### Key Entities

- **MyAnimeList anime**: one anime as MyAnimeList knows it. It holds:
  - id, page link, titles (main, English, Japanese, synonyms);
  - format, episode count, airing status, start date, year and season;
  - score, image;
  - its genres, themes, demographics and studios;
  - the full response, when it was fetched and last checked, and a change marker for refreshes.

  Shared by all users.
- **Genre / theme / demographic / studio**: named MyAnimeList classifications, each with its MyAnimeList id. An anime has several of each.
- **Season match**: the link from one Crunchyroll season (or movie) to a MyAnimeList anime, or to nothing. It holds its state, how it was decided (search, linked entry, shared from the original-language season, or manual), the candidates considered, and when.
- **Enrichment work item**: the pending work for a season or an anime refresh: kind (lookup or refresh), attempts, last error, next attempt time, and who is working on it. Restart-safe.
- **Stored search response**: what a search returned for a title and when. It's used to show candidates and to re-decide matches without asking again.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On the real catalog (501 seasons in 336 series), at least 85% of seasons are matched automatically. A hand check of 30 random automatic matches finds no more than 1 wrong.
- **SC-002**: With the service healthy, the first enrichment of the real catalog finishes within 30 minutes, without a single "too many requests" answer caused by Frunchy's own pace. Even with a backlog, the seasons on a user's recently-watched cards are enriched within 5 minutes of their import being processed.
- **SC-003**: During a service outage of any length, including restarts, no pending lookup is lost. After the outage, enrichment completes with no operator action, and during it Frunchy sends at most a few requests per hour.
- **SC-004**: With the service unreachable, dashboards load in under 1 second, as today, and imports are processed as today.
- **SC-005**: Running enrichment again over an already-enriched catalog sends no requests for details younger than the refresh age.
- **SC-006**: Re-deciding all automatic matches from stored responses sends zero requests and gives the same matches when run twice.
- **SC-007**: The "Top genres" counts equal a hand count over the user's fully watched episodes in matched seasons, and adding a match changes them on the next page view.
- **SC-008**: A manual fix by the operator shows on the dashboard on the next page view after its details are held, and survives any number of automatic re-runs.
- **SC-009**: Every test from features 001 and 002 still passes, and the dashboard figures (series, episodes, hours, days) are identical before and after enrichment.

## Assumptions

- **The service**: MyAnimeList's official API v2 (free client id, read-only public data, no published rate limits). Jikan v4 (unofficial, 3 per second / 60 per minute) was the first choice and stays as an alternative provider; it was unreachable for a whole evening while this feature was built, and announced shutting down. Search results from the official API include English titles and synonyms, so a candidate is judged without an extra request.
- **Crunchyroll ids don't appear in MyAnimeList data** (MyAnimeList links an old Crunchyroll address format), so matching is by title.
- **Refresh ages**: 30 days, and 7 days while airing, are defaults chosen here. Faster refresh isn't needed for statistics.
- **Score and genres as MyAnimeList states them**: the score is MyAnimeList's community score, not a personal rating. Genre and studio names aren't translated.
- **Attribution**: wherever MyAnimeList data is shown, it links to the anime's MyAnimeList page.
- **Tests never call the real service**: a stand-in answers with recorded real responses, so tests are deterministic and offline.
- **The browser extension is unaffected**: the import contract (v1.2) doesn't change.
- **Out of scope**: the Anime DNA page and its algorithm (feature 004), AniList, MyAnimeList login or list sync, personal ratings and favourites, the mockups' invented metrics (aesthetic 0–100 scores, ranks, trends, followers), and streaming platform detection.
