# Feature Specification: Review MyAnimeList Matches on the Web

**Feature Branch**: `007-mal-review`

**Created**: 2026-10-10

**Status**: Draft

**Input**: "Review MyAnimeList matches on the web: operator-only pages that do what the `mal-list`, `mal-set`, `mal-none` and `mal-retry` commands do today, with the same behaviour."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Fix the seasons that need a decision (Priority: P1)

An operator signs in and opens the review page. It lists the seasons the matcher couldn't decide (ambiguous, no match, or failed), most recently watched first.

Each row shows:
- the series and season title, linked to an anime page when someone watched it;
- why it needs a decision, and the attempts so far;
- how many users and fully watched episodes it affects;
- for an ambiguous season, the candidates MyAnimeList offered.

The operator can:
- pick a candidate;
- type a MyAnimeList id or paste a MyAnimeList link;
- mark the season "not on MyAnimeList";
- send its series back for an automatic retry.

After each action the page comes back with a short notice of what changed, and the season leaves the list when it is decided.

**Why this priority**: today these fixes need a terminal on the server. On the owner's data there are 13 ambiguous and 20 unmatched seasons waiting.

**Independent Test**: with the 2026 sample enriched by the fake MyAnimeList service, sign in as an operator, open the review page, resolve one ambiguous season by picking a candidate and one unmatched season by id. Each season then shows its match on the dashboard, exactly as the matching CLI command would leave it.

**Acceptance Scenarios**:

1. **Given** an operator, **When** they open the review page, **Then** it lists every season with status ambiguous, no match or failed, in the order the CLI's list uses, filterable by status.
2. **Given** an ambiguous season, **When** the operator picks one of its candidates, **Then** the season is matched to it by hand, exactly as `mal-set` would, and a notice names the anime.
3. **Given** a season, **When** the operator enters a MyAnimeList id or a `myanimelist.net/anime/{id}` link that isn't stored yet, **Then** the anime is fetched once and the season matched. **Given** an id MyAnimeList doesn't know, **then** nothing changes and the notice says so.
4. **Given** no MyAnimeList client id is configured, **When** the operator sets a match for an anime that isn't stored, **Then** nothing changes and the notice says the service isn't available.
5. **Given** a season, **When** the operator marks it "not on MyAnimeList", **Then** it is recorded as a manual "no match", exactly as `mal-none` would, and automatic matching never changes it.
6. **Given** a season, **When** the operator asks for a retry, **Then** its series is queued for automatic matching again, exactly as `mal-retry` would, with manual decisions kept.
7. **Given** an original season with dubs that follow it, **When** the operator changes the original, **Then** the dubs change with it, as they do from the CLI.

### User Story 2 - Undo a manual decision (Priority: P2)

A second view lists the seasons decided by hand: matched by hand, or marked "not on MyAnimeList". The operator can send any of them back to automatic matching.

**Why this priority**: a wrong manual decision is never corrected by the matcher, so it needs a way back. It matters less often than the first fix.

**Independent Test**: set a season by hand, find it in the manual view, send it back, and see the matcher decide it again on its next run.

**Acceptance Scenarios**:

1. **Given** manual decisions exist, **When** the operator opens the manual view, **Then** it lists each with its series, season, what was decided, and when.
2. **Given** a manual decision, **When** the operator sends it back to automatic, **Then** it becomes a pending automatic match that the next enrichment run decides, and it leaves the manual view.

### User Story 3 - Only operators get in (Priority: P1)

Only signed-in users named as operators in the site's configuration can see or use the review pages. Everyone else, signed in or not, gets the site's "page not found", so the pages aren't advertised. Operators see a link to the review page on their account page.

**Why this priority**: the matches are shared catalog data that every profile's figures depend on, so this is a security requirement and ships with User Story 1.

**Independent Test**: with one operator configured, request every review page and action as a signed-out visitor, as a signed-in non-operator and as the operator. Only the operator gets the pages, and the actions change data only for the operator.

**Acceptance Scenarios**:

1. **Given** a signed-out visitor or a signed-in non-operator, **When** they request any review page or action, **Then** they get "page not found" with a not-found status and nothing changes.
2. **Given** no operators configured, **When** anyone requests the review pages, **Then** they get "page not found".
3. **Given** an operator, **When** they submit an action without the form's forgery protection, or too often, **Then** it is refused as other account forms refuse it, and nothing changes.
4. **Given** an operator, **When** they take any action, **Then** it appears in their account's security activity.
5. **Given** the account page, **When** an operator views it, **Then** it links to the review page; **when** anyone else views theirs, **then** there is no such link.

### Edge Cases

- **Stale form**: a season decided by someone else, or by the matcher, between loading the page and submitting. The action applies only if the season still needs it; otherwise the notice says it was already decided and nothing changes.
- **Bad input**: a typed id that isn't a positive whole number, or a link that isn't a MyAnimeList anime link, is refused with a notice and no request is made.
- **Unknown season**: a season id that doesn't exist is refused with a notice.
- **Nobody watched it**: the title shows without a link, since no anime page exists for it.
- **Empty list**: nothing waiting shows a plain "nothing to review" state.
- **Long list**: the list shows at most the same number of rows as the CLI's default and says when there are more.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The site MUST serve the review list at `/review/mal`, filterable by status (ambiguous, no match, failed, or all three, the default), in the CLI list's order.
- **FR-002**: Each row MUST show:
  - the series title and the season title, the series linked to the anime page of a user who watched it when one exists;
  - the reason and the attempts;
  - the number of users and of fully watched episodes in that season;
  - for an ambiguous season, its stored candidates with title, format, year, MyAnimeList id and a link.
- **FR-003**: Picking a candidate or entering a MyAnimeList id or `myanimelist.net/anime/{id}` link MUST set the season's match exactly as `mal-set` does: fetch the anime once when it isn't stored, refuse an id MyAnimeList doesn't know, and change nothing when the service or its client id is unavailable.
- **FR-004**: Marking a season "not on MyAnimeList" MUST behave exactly as `mal-none`.
- **FR-005**: Asking for a retry MUST behave exactly as `mal-retry` for the season's series.
- **FR-006**: The CLI commands and the web actions MUST call the same shared functions, so their behaviour cannot drift.
- **FR-007**: The manual view MUST list the manual decisions, and sending one back MUST make it a pending automatic match again.
- **FR-008**: Only a signed-in user whose username is listed in the `Frunchy:Operators` setting MAY see the pages or take an action. Everyone else MUST get the site's not-found page with a not-found status, for pages and actions alike. The setting is empty by default.
- **FR-009**: Every action MUST be a form post that passes the site's existing forgery protection and a rate limit, and MUST answer with a redirect back to the page with a notice (post/redirect/get).
- **FR-010**: Every action MUST be recorded in the operator's security activity.
- **FR-011**: An action on a season that no longer needs it, a malformed id or link, or an unknown season MUST change nothing and show a notice saying why.
- **FR-012**: The account page MUST show a link to the review page to operators only.
- **FR-013**: The pages MUST use the shared design system and components. Every visible text MUST come from the site's English and Spanish texts. The pages MUST work from 360px wide.
- **FR-014**: No new stored tables; no change to the extension's API; tests MUST NOT call the real MyAnimeList service.

### Key Entities

- **Season needing a decision**: a season match with status ambiguous, no match or failed, with its reason, attempts, candidates and the watch counts it affects.
- **Manual decision**: a season match whose method is manual: matched by hand, or marked not on MyAnimeList.
- **Operator**: a signed-in user named in the operators setting.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the same season and input, a web action and its CLI command leave identical stored matches (tested pairwise for set, none and retry).
- **SC-002**: 100% of review pages and actions answer "not found" to signed-out visitors and non-operators, and change nothing for them.
- **SC-003**: An operator resolves an ambiguous season in at most 2 clicks from the review page.
- **SC-004**: The owner's 13 ambiguous and 20 unmatched seasons can all be decided from the web without a terminal.
- **SC-005**: The pages pass the design-system checks of feature 004 and an impeccable audit with no blocking finding.

## Assumptions

- Operators are named by username in configuration, for now only the owner. A role column or an operator management page is out of scope.
- Hiding the pages behind "not found" rather than "forbidden" is deliberate: it doesn't reveal that operator pages exist.
- The review list is not paged; it shows the CLI's default number of rows and a count of the rest.
- Security activity records which kind of action was taken; the season and anime involved go to the application log.

## Verbatim Constraints

- Route: `/review/mal`
- Setting: `Frunchy:Operators`
- Link format accepted: `myanimelist.net/anime/{id}`
