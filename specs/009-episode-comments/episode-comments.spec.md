# Feature Specification: Episode Page and Comments

**Feature Branch**: `009-episode-comments`

**Created**: 2026-10-15

**Status**: Draft

**Input**: "Public episode page with local comments: step 1 of `docs/fediverse.md`, built before the Megáfono integration and making no Megáfono calls. A public page per catalog episode, a comment thread on it with spoilers as content warnings, a dub and its original sharing one thread, operator moderation, and comments stored so that fediverse replies fit later."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One page per episode (Priority: P1)

Anyone opens an episode's page and sees what the episode is, independent of who watched it.
- **Header**: the series title, the season title with its audio language (dubs marked), the episode number or special label, the episode title, its length in minutes, and its thumbnail.
- **Links**: "Watch on Crunchyroll" for the episode, and the series on Crunchyroll when its slug is known.
- **Season details**: when the season is matched, its MyAnimeList title, score, up to 3 genres and a link, the way the anime page shows them. An unmatched season shows nothing in their place.
- **From a profile**: each watched-episode row on the anime page (`/u/{username}/anime/{seriesId}`) links to the episode's page.

**Why this priority**: every page today belongs to one user. A comment thread needs a place that belongs to the episode itself, and that place is also what a fediverse thread will point to later.

**Independent Test**: import the 2026 sample, enrich it with the feature 003 fixtures, open the page of a few episodes (one matched, one unmatched, one dub, one special without a number), and compare every field with the stored catalog, in English and Spanish. Follow an episode link from an anime page.

**Acceptance Scenarios**:

1. **Given** an episode with known details, **When** anyone opens its page, signed in or not, **Then** it shows the header, the Crunchyroll links and, when matched, the season's MyAnimeList details.
2. **Given** a dubbed episode, **When** its page is shown, **Then** its audio language is shown and it is marked as a dub.
3. **Given** an anime page, **When** its watched episodes are listed, **Then** each one links to that episode's page.
4. **Given** an unknown episode, an episode of another series than the one in the address, an episode without known details yet, or an id outside Crunchyroll's id alphabet, **When** its address is opened, **Then** the site's not-found page is shown with a not-found status.

---

### User Story 2 - Read and write comments (Priority: P1)

Under the episode, a thread of comments. Each comment shows its author (display name and username, linking to their dashboard), when it was written, and its text. A signed-in user writes a comment in a box with a "Contains spoilers" checkbox. A spoiler comment carries a warning, "Spoilers: S2 E5" for example, and its text stays hidden behind a "Show spoiler" toggle until the reader opens it. Signed-out visitors see the thread and, instead of the box, a prompt to sign in that brings them back to the page.

**Why this priority**: it is the feature. Without comments the page is a catalog entry.

**Independent Test**: as two signed-in users, write a plain comment and a spoiler comment on an episode, then open the page signed out and as each user: check the order, the authors, the times, the warning and the hidden spoiler text, in English and Spanish.

**Acceptance Scenarios**:

1. **Given** a signed-in user [NEEDS CLARIFICATION: any signed-in user, or only one who has watched the episode?], **When** they submit a comment, **Then** it appears in the thread with their name and the time, and the page shows it after the redirect.
2. **Given** the "Contains spoilers" box ticked, **When** the comment is submitted, **Then** it is stored with a warning naming the season and episode and a spoiler flag, and readers see the warning with the text hidden until they open it.
3. **Given** a comment that is empty, only whitespace, or longer than the limit, **When** it is submitted, **Then** nothing is stored and the page says why, keeping what was typed.
4. **Given** a signed-out visitor, **When** they open the page, **Then** they read the thread and see a sign-in prompt that returns them to this page.
5. **Given** no comments yet, **When** the page is shown, **Then** it says so plainly.
6. **Given** a long thread, **When** the page is shown, **Then** every visible comment is listed, oldest first.

---

### User Story 3 - A dub and its original share one thread (Priority: P2)

On Crunchyroll a dub and its original are separate episodes. When Frunchy knows they're the same episode (the DNA page's rule: the dub's season is linked to its original through MyAnimeList matching, and the episode numbers are equal), both pages show the same thread, so the discussion isn't split by audio language.

**Why this priority**: important for a healthy thread, but the page and comments already work without it.

**Independent Test**: with the 2026 sample's dubbed series enriched, comment on the original and on the dub; both pages show both comments. Then remove the dub link and check each page shows only its own comments, with none lost.

**Acceptance Scenarios**:

1. **Given** a dub episode linked to its original, **When** comments are written on either, **Then** both pages show all of them in one thread.
2. **Given** a dub not linked to any original (unmatched, or matched on its own), **When** its page is shown, **Then** it shows only the comments written on it.
3. **Given** a dub link that appears or disappears later (matching or an operator review changes it), **When** the pages are shown again, **Then** the threads regroup and no comment is lost or changed.

---

### User Story 4 - Authors manage their comments (Priority: P2)

The author of a comment can edit it (text and spoiler flag) and delete it. An edited comment shows that it was edited.

**Why this priority**: people make typos and regret posts. The fediverse side already supports edits and deletes, so the model should too.

**Independent Test**: as the author, edit a comment and then delete another; as a different user and signed out, check that edit and delete aren't offered and are refused when forced.

**Acceptance Scenarios**:

1. **Given** the author, **When** they edit their comment, **Then** the new text and spoiler flag are shown with an "edited" mark.
2. **Given** the author, **When** they delete their comment, **Then** it no longer appears anywhere.
3. **Given** anyone other than the author, **When** they try to edit or delete it, **Then** nothing changes and they get the site's not-found page.

---

### User Story 5 - Operators hide comments (Priority: P3)

An operator (features 007 and 008) can hide any comment on the episode page, and unhide it. Everyone else sees "Hidden by an operator" in its place; operators still see the text with an unhide control. The page says that hiding a comment hides it on Frunchy.

**Why this priority**: moderation must exist before comments are promoted anywhere, but the first users are few and known.

**Independent Test**: as an operator, hide a comment and check what an outsider, the author and another operator see; unhide it. As a non-operator, force the hide request and get the not-found page.

**Acceptance Scenarios**:

1. **Given** an operator, **When** they hide a comment, **Then** everyone who isn't an operator sees "Hidden by an operator" in its place, and the action is recorded on the author's account.
2. **Given** a hidden comment, **When** an operator unhides it, **Then** it is shown again as before.
3. **Given** anyone who isn't an operator, **When** they send a hide or unhide request, **Then** they get the plain not-found page and nothing changes.

### Edge Cases

- **Deleted user**: their comments are removed with the rest of their data.
- **Renamed user**: their comments show the current username and display name.
- **Hidden then edited**: an author can still edit or delete a hidden comment; it stays hidden.
- **Episode never watched by anyone else**: the page exists for any episode with known details in the shared catalog, whoever brought it in.
- **No season**: an episode without a season shows no season line and no MyAnimeList details; its thread is its own.
- **No number**: an episode without a number uses its special label, and the spoiler warning names the label (for example "Spoilers: OVA").
- **Markup in a comment**: shown as plain text, line breaks kept; nothing in it becomes a link, a tag or a script.
- **Double submit**: submitting the same form twice doesn't create two comments.

## Requirements *(mandatory)*

### Functional Requirements

**Episode page**

- **FR-001**: The site MUST serve a public page at `/anime/{seriesId}/episode/{episodeId}`, in the viewer's language, signed in or not.
- **FR-002**: An id outside Crunchyroll's id alphabet MUST get the not-found page without touching the database. An unknown episode, an episode whose series isn't `{seriesId}`, or an episode without known details (no length yet) MUST get the not-found page with a not-found status.
- **FR-003**: The header MUST show the series title (the stored series title, else the episode's series title), the season title with its audio language and a dub mark when it is a dub of another season, the episode number or special label, the episode title, its length in minutes and its thumbnail when there is one.
- **FR-004**: The page MUST link to the episode on Crunchyroll, and to the series on Crunchyroll when the series' slug is known, both opening in a new tab.
- **FR-005**: When the episode's season is matched, the page MUST show its MyAnimeList title, score (when it has one), up to 3 genres and a link to its MyAnimeList page.
- **FR-006**: Each watched episode on the anime page MUST link to its episode page.

**Comments**

- **FR-007**: A signed-in user [NEEDS CLARIFICATION: any signed-in user, or only one who has watched the episode?] MUST be able to write a comment on an episode: plain text, 1 to 2,000 characters after trimming, with a "Contains spoilers" option [NEEDS CLARIFICATION: is "Contains spoilers" ticked by default, and is the whole thread collapsed until the reader opens it?].
- **FR-008**: A spoiler comment MUST store a warning text naming the season and episode ("Spoilers: S2 E5", or the special label) in the author's language, and a spoiler flag. Readers MUST see the warning and a "Show spoiler" toggle, with the text hidden until they open it.
- **FR-009**: The thread MUST list every comment on the episode's group, oldest first, each with its author's display name and username (linking to their dashboard), its time in the site's time element, an "edited" mark when edited, and its text as plain text with line breaks kept.
- **FR-010**: An episode's group MUST be computed when the page is viewed: the episode alone, or, when its season is a dub linked to an original season through MyAnimeList matching, every episode with the same episode number in the original season and in the seasons linked to it. A comment MUST stay attached to the episode it was written on, so regrouping never moves, changes or loses a comment.
- **FR-011**: The author of a comment MUST be able to edit its text and spoiler flag, and delete it. Anyone else's edit or delete request MUST get the not-found page and change nothing.
- **FR-012**: Signed-out visitors MUST see the thread and a sign-in prompt in place of the comment box, which returns them to the episode page after signing in.
- **FR-013**: An invalid comment (empty, too long) MUST NOT be stored; the page MUST say why and keep the typed text.
- **FR-014**: Every comment write (create, edit, delete) MUST use the site's antiforgery-checked forms, MUST be rate limited per IP, and submitting the same form twice MUST NOT create two comments.

**Moderation**

- **FR-015**: An operator MUST be able to hide and unhide any comment from the episode page. A hidden comment MUST show "Hidden by an operator" to everyone except operators, who see its text and an unhide control.
- **FR-016**: Hide and unhide requests from anyone who isn't an operator MUST get the plain not-found page, before any form is read, and change nothing, as on the review pages.
- **FR-017**: Each hide and unhide MUST record a security event on the author's account, naming the operator.
- **FR-018**: The page MUST say that hiding a comment hides it on Frunchy.

**Ready for the fediverse (no fediverse calls in this feature)**

- **FR-019**: Each comment MUST have an id that never changes, is never reused, and matches `^[A-Za-z0-9_-]{1,64}$`.
- **FR-020**: The stored comment MUST be able to hold, without a schema change later: an author who is either a local user or a remote fediverse account; a warning text and a spoiler flag kept separately from the text; the comment it answers, if any; its audience; and an empty-for-now fediverse object id.
- **FR-021**: This feature MUST NOT call Megáfono or any other server, and MUST NOT create remote authors.

**General**

- **FR-022**: Every visible text MUST come from the site's English and Spanish texts. The page MUST use the shared design system and components, work from 360px wide in both languages, and meet WCAG 2.1 AA contrast.
- **FR-023**: Comments are content written by users, not derived data: they MUST be kept as written, removed only by their author, by the author's account deletion, or not at all. `user-rebuild` and re-sending history MUST NOT change them.

### Key Entities

- **Episode page** (computed, not stored): one catalog episode with its series, season and MyAnimeList details, and its group's thread.
- **Episode group** (computed, not stored): the episode alone, or a dub and its original episodes with the same number, by the DNA page's rule.
- **Comment**: the episode it was written on, its author (a local user now; a remote fediverse account later), its text, its warning text and spoiler flag, the comment it answers (none in this feature), its audience, when it was written and last edited, whether an operator hid it, and its fediverse object id (empty in this feature).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For the episodes picked from the 2026 sample (matched, unmatched, dub, special), every field on the page equals the stored catalog, in English and Spanish.
- **SC-002**: A signed-in user writes a comment in under 30 seconds from opening the page, and it is shown right after submitting.
- **SC-003**: No spoiler comment's text is visible on first load, for any viewer.
- **SC-004**: When a dub link appears or disappears, the number of stored comments and their content are unchanged, and each comment is shown on its own episode's page.
- **SC-005**: An episode page with 1,000 comments is ready in under 1 second.
- **SC-006**: Every write path refuses a request from the wrong user, a signed-out visitor or a non-operator, and none of them changes stored data.
- **SC-007**: The page passes the design-system checks of feature 004 (every test hook present) and an impeccable audit with no blocking finding, covering the variants of `design/BRIEF.md` §5.5: no comments yet, long thread, hidden comment, signed out, Spanish, 360px.

## Assumptions

- The episode page shows the catalog, not anyone's watches; it reveals nothing about who watched what.
- Comments are flat in this feature: no replying to a comment. The stored comment keeps a slot for the comment it answers, because fediverse replies to replies arrive that way later.
- Comment text is plain text. Remote fediverse replies will arrive as sanitized HTML later; how they are shown is decided in the integration feature.
- The Crunchyroll episode link is `https://www.crunchyroll.com/watch/{episodeId}`; Crunchyroll redirects it to the full address. The plan verifies this.
- Hiding a comment only hides it on Frunchy. Once comments are federated, copies already on other servers may stay; the integration feature's help text says so.
- Operators hide comments from the episode page itself; a review list of comments is out of scope.
- Out of scope: Megáfono calls, series and user fediverse accounts, thread posts, the username trailing-dash rule, following and feeds, notifications, likes.
- The extension doesn't change; the import contract isn't touched.

## Verbatim Constraints

- Route: `/anime/{seriesId}/episode/{episodeId}`
- Comment ids: `^[A-Za-z0-9_-]{1,64}$` (later used as Megáfono's `Idempotency-Key: comment:{id}`)
- Spoiler warning: `summary` text plus a `sensitive` flag, as in Megáfono 002's publish shape
- Moderation copy: "Hidden by an operator"
