# Feature Specification: Better MyAnimeList matching

**Feature Branch**: `032-mal-matching`

**Created**: 2026-11-07

**Status**: Draft

**Input**: "How are we matching with MAL? The Casebook of Arne isn't matched, and on MyAnimeList it is Arne no Jikenbo,
English title The Case Book of Arne." The owner chose all three fixes, in the spec flow: compare titles ignoring
spaces, a second search when the first finds nothing usable, and re-searching new unmatched shows sooner.

## What happened (the case)

- Crunchyroll season `GS00365734JAJP`, series "The Casebook of Arne", status `no_match`, **no candidates stored**.
  A no-match stores the first 5 search results as candidates when there are any (`MalMatching`, anchor search:
  `NoMatch("no-title-match", shown)`), so MyAnimeList's search for "The Casebook of Arne" **returned nothing**.
- Even if it had returned entry 60255, the title rules would refuse it: normalized, Crunchyroll's "casebook of arne"
  is not MyAnimeList's English "case book of arne" (tier 1: equal; tier 2: equal after cutting a subtitle).
- An automatic no-match is never searched again unless the Crunchyroll titles change (`Enrichment.sweep`) or an
  operator asks; a stored search is reused for 30 days (`MalClient.searchReuse`). A show searched before MyAnimeList
  listed it, or under another spelling, stays unmatched.

## Clarifications

### Session 2026-11-07 (decided in the autonomous run; the owner can overturn any of them)

- Q: The space rule? → A: Two new title tiers, after the existing two, so an exact match always wins over a looser
  one: **tier 3** = the normalized titles are equal once every space is removed (`casebookofarne`), with the same
  season number as today's rule; **tier 4** = the same after cutting a subtitle (either side, as tier 2 does). It is
  still whole-title equality, never a similarity score. The season marker is read from the spaced form before spaces
  are removed, so "Show 2" still means season 2.
- Q: Which second searches? → A: When the series' first search gives **no title hit at any tier** (no results, or
  results none of which match), the matcher tries **fallback queries**, in order, stopping at the first that yields a
  title hit: each **content word** of the Crunchyroll series title, longest first, at most **3** words. A content word
  has at least 4 letters after normalization and isn't a stop word (`the`, `of`, `and`, `a`, `an`, `to`, `in`, `no`,
  `wa`, `ga`, `wo`, `ni`, `de`, `with`, `from`, `for`, `season`, `part`, `movie`, `film`). For "The Casebook of Arne":
  "Casebook", then "Arne". Every result still has to pass the title tiers (and the existing type, part and year
  rules), so a fallback can only find what the rules already accept; it can't create a guess. The same fallback
  applies to the season-title search (`searchSeason`).
- Q: Requests? → A: Fallback searches go through the same lookup (stored, reused, paced at 1 request a second) and are
  stored like any search, so `mal-rebuild` replays them with no new request. At most 3 extra searches per series.
- Q: Re-searching new unmatched shows? → A: Two rules:
  - **An empty search answer is reused for 3 days**, not 30 (a non-empty answer keeps 30). A show MyAnimeList hasn't
    listed yet is asked again soon.
  - **The sweep re-queues an automatic `no_match`** (never `manual`, never `ambiguous`: ambiguity needs an operator)
    when it was decided more than **3 days** ago and the season is **new**: its earliest known episode air date
    (`cr_episodes.aired_at`, feature 027) is within the last **120 days**, or, with no air date, the match row was
    created within the last 120 days. Older no-matches stay as they are.
- Q: Existing unmatched seasons in production? → A: A new operator command, `mal-retry-unmatched`, re-queues every
  automatic `no_match` once (never manual, never ambiguous), prints how many, and exits 0. The operator runs it after
  deploying this. The web "Try again" and `mal-retry <id>` stay.
- Q: Does a match ever change without an operator? → A: Only `no_match` rows are re-decided; `matched` rows aren't
  touched, and `manual` rows never (`MalCatalog`'s guard).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A spelling difference doesn't block a match (Priority: P1)

**Acceptance Scenarios**:

1. **Given** a Crunchyroll series "The Casebook of Arne" and a MyAnimeList TV entry whose English title is "The Case
   Book of Arne" among the search results, **When** it is matched, **Then** it matches (tier 3).
2. **Given** both an exact-title entry and a spaceless-equal entry, **Then** the exact one wins (tier 1 before 3).
3. **Given** "Show 2" and a candidate "Show2" (no space), **Then** the season numbers still decide (no cross-season
   match).
4. **Given** a candidate equal only after removing spaces **and** cutting a subtitle, **Then** tier 4 applies.

### User Story 2 - A second search finds what the first missed (Priority: P1)

**Acceptance Scenarios**:

1. **Given** the search for "The Casebook of Arne" returns nothing and the search for "Casebook" returns nothing, and
   the search for "Arne" returns the entry, **When** matched, **Then** it matches, after exactly 3 searches.
2. **Given** the first search already yields a title hit, **Then** no fallback search is made.
3. **Given** every fallback query yields results but none passes the title rules, **Then** `no_match`, and the
   candidates stored are the first search's results (or the first non-empty fallback's when the first was empty).
4. **Given** a rebuild (`mal-rebuild`, stored only), **Then** the same decision, no request.

### User Story 3 - New shows are asked again (Priority: P2)

**Acceptance Scenarios**:

1. **Given** an empty stored search 4 days old, **When** the matcher searches the same query, **Then** it asks
   MyAnimeList again; 2 days old, **Then** it reuses it. A non-empty 20-day-old search is reused.
2. **Given** an automatic `no_match` decided 4 days ago on a season whose newest episode aired 10 days ago, **When**
   the sweep runs, **Then** it is pending again; decided 2 days ago, **Then** not; a season whose episodes aired a year
   ago, **Then** not; `manual` or `ambiguous`, **Then** never.
3. **Given** `mal-retry-unmatched`, **Then** every automatic `no_match` is pending, the count is printed, `manual`
   and `ambiguous` rows are untouched.

## Requirements *(mandatory)*

- **FR-001**: Tiers 3 and 4 in `MalMatching` as clarified; `bestHits` keeps ranking by tier, then main title before
  synonym.
- **FR-002**: Fallback queries as clarified, in the anchor search and in `searchSeason`, through the existing lookup.
- **FR-003**: `searchReuse` split: 3 days for an empty result list, 30 days otherwise.
- **FR-004**: The sweep's new re-queue rule; one literal statement, parameters only.
- **FR-005**: `mal-retry-unmatched` (`Operations/MalRetryUnmatched.fs`, registered in Program's CLI), documented in the
  operator CLI contract and the README's command list.
- **FR-006**: Every existing matching test stays green unless the new tiers legitimately change its outcome (say which
  and why); `mal-rebuild` reproduces decisions from stored responses.
- **FR-007**: `changelog.d/032-mal-matching.md` (Changed: better matching; Upgrade: run `mal-retry-unmatched` once).

## Success Criteria *(mandatory)*

- **SC-001**: After deploying and running `mal-retry-unmatched`, The Casebook of Arne matches 60255 with no operator
  action (if MyAnimeList's search for "Arne" returns it, as it does on its site).
- **SC-002**: No existing correct match changes; the suite passes.

## Assumptions

- MyAnimeList's search returns the entry for one of its distinctive words (it searches by words).
