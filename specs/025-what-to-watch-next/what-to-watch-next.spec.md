# Feature Specification: What to watch next

**Feature Branch**: `025-what-to-watch-next`

**Created**: 2026-10-23

**Status**: Draft

**Input**: the follow-up task "What to watch next, from people with similar taste": taste compatibility (feature 011)
already computes how alike two histories are; recommendations are the next step. Among people whose profiles the
viewer can see (feature 017 and 021 rules), the series the best matches watched through and the viewer hasn't
started, ranked by match strength. Never reveal a private history.

## Clarifications

### Session 2026-10-23 (decided by the main session)

- Q: Whose recommendations, and where? → A: The signed-in viewer's own, on one private page `GET /next`. Nobody can
  open another person's recommendations, so no profile gate is involved for the page itself: `WebAuth.requireSignedIn`
  (signed out goes to `/signin?returnUrl=/next`). The owner's dashboard links to it (`data-next-link`); visitors of
  someone else's dashboard don't see the link.
- Q: Who counts as a "person with similar taste"? → A: Another user who
  1. the viewer can see, by **the same pure rule as every `/u/` page**: `Profiles.canView (Some viewer) grants access`
     (public, members, or private with a grant in this browser's `frunchy.shares` cookie naming the owner's
     **current** share link; operators and the root get no exception);
  2. has a history long enough for a level (`Compatibility.minimumEpisodes`, 15 fully watched episodes);
  3. shares at least one series with the viewer, and among those the 50 sharing the most series (then username) are
     compared (`maxCandidates`, so the page stays one bounded computation);
  4. reaches **level 3 ("Good") or more** with the viewer (`minimumLevel`), by `Compatibility.ofPeople` (the very
     score of the compare page). The 10 best by score (then username) are the matches (`maxMatches`).
- Q: "Watched through"? → A: Frunchy has no reliable episode total, so a person has watched a series through when,
  over the seasons of that series they watched anything of, they fully watched at least 80% of the episodes Frunchy
  knows (episodes with a known length), and at least one. A dub season and its original are separate seasons, so
  watching only the original isn't penalised by the dub's episodes.
- Q: "Hasn't started"? → A: The viewer has no watch event at all on any episode of the series (an unresolved episode,
  without a known length, counts as started).
- Q: The ranking? → A: Per series: the best score among the matches who watched it through (descending), then how
  many of them did (descending), then title, then series id. Up to 12 series (`maxSeries`).
- Q: What does a recommendation show? → A: The series title linking to the public series page `/anime/{seriesId}`
  (`data-series-page-link`, never into a person's history), the MyAnimeList score and up to 3 genres of the series'
  most watched matched season among those matches (the compare page's "theirs" rule), and "Watched through by" up to
  3 of those matches, best first: each one's display name linking to their dashboard, with the level in words
  (`CompatLevelName`), then "and N more".
- Q: `Frunchy:Charts:MinPeople`? → A: It doesn't apply: the page has no anonymous aggregate. Every person it names or
  counts is one the viewer can already open, and the count of finishers only counts named, visible matches. A
  person the viewer can't see contributes nothing: not a series, not a count, not the ranking.
- Q: The score? → A: As in feature 011, never printed, not even in an attribute: only levels (1..5) and words.

## Requirements *(mandatory)*

- **FR-001**: `GET /next` needs a sign-in; signed out it redirects to `/signin?returnUrl=%2Fnext` (whatever
  `requireSignedIn` does today). No POST, no state change, nothing stored.
- **FR-002**: The candidate set follows the visibility rule exactly (`Profiles.canView` with the viewer and the
  request's grants). A private profile without a current grant, a private profile whose link was replaced or turned
  off, and the viewer themself never contribute anything.
- **FR-003**: Matches are visible people at level 3 or more, the 10 best; series are those at least one match watched
  through and the viewer never started, ranked as above, up to 12.
- **FR-004**: Empty states (`data-next-empty`):
  - `not-enough`: the viewer has fewer than 15 fully watched episodes (the compare page's wording);
  - `no-matches`: nobody visible reaches level 3;
  - `no-series`: matches exist but they watched through nothing the viewer hasn't started.
- **FR-005**: Hooks: the page `data-next` (with `data-match-count`), each series `li[data-next-series="{seriesId}"]`
  (with `data-finisher-count` and, when known, `data-mal-score` as in compare), each named person
  `a[data-next-person="{username}"]` with `data-level="{1..5}"`, the "and N more" `data-next-more="{N}"`, the
  dashboard's `a[data-next-link]` (owner only). No score anywhere in the HTML.
- **FR-006**: `Compatibility.fs` keeps its single score; the recommendation logic lives in
  `Infrastructure/Recommendations.fs` (SQL plus pure functions), the page in `Handlers/Users/Next.fs`. The SQL is
  literal with parameters (the guardrail test).
- **FR-007**: All copy is in `Views/Texts.fs`, English and Spanish. Only existing components and tokens (`fr-card`,
  `sectionHeading`, `pill`, `emptyPageState`, buttons); no new design token, so the extension is untouched.
- **FR-008**: No schema change, no change to `/api/*`, the compare page or the dashboard card.

## Success Criteria *(mandatory)*

- **SC-001**: A signed-in person with a long enough history sees series they haven't started, each with who among
  their visible matches watched it through and how alike their taste is.
- **SC-002**: Making a match's profile private (or replacing the share link the viewer holds) removes that person
  and every series only they recommended on the next request.
