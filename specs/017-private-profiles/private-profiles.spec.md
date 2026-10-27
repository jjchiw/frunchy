# Feature Specification: Private Profiles

**Feature Branch**: `017-private-profiles`

**Created**: 2026-10-19

**Status**: Draft

**Input**: "Dashboards must be private. An environment variable decides whether profiles are public or not, and a user can share a link with a token or make their profile public." (the owner, 2026-10-19; backlog notes in the README)

## Clarifications

### Session 2026-10-19 (decided in the autonomous run; the owner can overturn any of them)

- Q: What does "the profile" cover? → A: Every page under `/u/{username}`: dashboard, Anime DNA, year in review, anime pages and compare. They share one rule.
- Q: Who sees a private profile? → A: Its owner, and anyone holding a valid share link. Nobody else, operators and the root included: operators review MyAnimeList matches and comments, and don't need anyone's history. Everyone else gets **the same 404 as a username that doesn't exist**, so a private profile doesn't reveal what it holds.
- Q: The instance setting? → A: `Frunchy:Profiles:DefaultVisibility`, `private` (the default) or `public`. Anything else stops startup naming the key. A user who has never chosen follows it, so changing the setting moves everyone who hasn't chosen. A user who chose keeps their choice.
- Q: Share links: how many, how long, what they open? → A:
  - **One per user**: creating a new one replaces the old, and turning it off ends it. No expiry.
  - **What it opens**: the whole profile, every page under `/u/{username}`. Signed out or signed in doesn't matter.
  - **The token**: 256 random bits (`frs_` + base64url), stored only as a SHA-256 hash like the sync key. It's shown once, when created; a lost link is replaced, not shown again.
- Q: How does a link holder browse several pages? → A: The link is `/s/{token}`. Opening it checks the token, gives the browser a signed cookie remembering "may see this profile" (up to 20 profiles, 30 days), and redirects to `/u/{username}`.
  - Every page under `/u/{username}` then works without the token in the address, so the token never travels in a `Referer` or a copied page address.
  - The cookie names the link, not the token. Replacing or turning off the link ends every browser's access at once.
- Q: Comments? → A: A comment stays public content: its author's display name shows on the episode page as now. The name links to the profile only when the viewer can see it (public, their own, or shared with them). Otherwise it's plain text.
- Q: Taste compatibility? → A: It follows the profile: the compare page and the dashboard card exist only for a viewer who can see that profile. A private user can still compare themselves with public profiles: the comparison is shown to them alone.
- Q: The fediverse (015)? → A: The rule covers watch history, not the account's existence. With federation on, a user's actor still exists (their comments need an author) and its `profileUrl` stays `/u/{username}`, which answers 404 to strangers when private. The handle line and the ActivityPub advertisement show only on pages the viewer can see, so nothing changes for public profiles.
- Q: Aggregates ("Watched by N people", charts)? → A: Unchanged. They never name anyone and already need `Frunchy:Charts:MinPeople` people, so private histories still count in them.
- Q: The operators' MyAnimeList review linked a season to its latest watcher's anime page. → A: It links to the public series page (`/anime/{seriesId}`) instead, so no review page points into anyone's history.
- Q: Does the extension change? → A: No. `/api/me`'s `dashboardUrl` is the owner's own address, which still works for them. No contract change.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Private by default (Priority: P1)

On an instance with the default setting, a new user's profile pages are visible only to them. Someone who types `/u/{username}`, or any page under it, gets the same "not found" page as for a username that doesn't exist. The owner sees their pages with a line saying only they can see them, and a link to the visibility settings.

**Why this priority**: today every history is readable by anyone who guesses a username; friends are about to try the site.

**Independent Test**: create a user with history, then request each `/u/{username}/…` page signed out, as another user and as an operator: 404 identical to an unknown username's. Request them as the owner: 200 with the private line.

**Acceptance Scenarios**:

1. **Given** the default setting and a user who never chose, **When** anyone but the owner requests the dashboard, DNA, a year, the year redirect, an anime page or compare, **Then** they get 404 with the "user not found" page, byte-for-byte the same body as for an unknown username (apart from the layout's per-request tokens).
2. **Given** the owner signed in, **When** they open their pages, **Then** they see them, with `data-profile-private` explaining that only they can see them and linking to `/account#visibility`. The "copy link" for the page address is replaced by a pointer to share links.
3. **Given** an operator or the root, **When** they request a private profile, **Then** 404 too.
4. **Given** `Frunchy:Profiles:DefaultVisibility=public` and a user who never chose, **When** anyone requests their pages, **Then** 200, as before this feature.
5. **Given** an invalid setting value, **When** Frunchy starts, **Then** it refuses, naming `Frunchy:Profiles:DefaultVisibility`.
6. **Given** the home page, **When** it renders, **Then** its note about profiles matches the instance default (private: profiles are visible only to their owner and people they share a link with; public: profiles are public unless their owner makes them private).

---

### User Story 2 - Choosing public or private (Priority: P1)

On `/account`, a "Profile visibility" section shows the current state and the instance default. The owner picks Public or Private and saves; the choice sticks even if the instance default changes later.

**Why this priority**: some users want a public profile to share widely; the default must be reversible per person.

**Independent Test**: as the owner, choose Public: a signed-out visitor sees the dashboard. Choose Private: 404 again. Change the instance default: the explicit choice holds.

**Acceptance Scenarios**:

1. **Given** the owner on `/account`, **When** the page renders, **Then** `data-visibility` shows the effective state (`public` or `private`) and whether it comes from their choice or the instance default.
2. **Given** a valid form post choosing `public` or `private`, **When** it's submitted, **Then** the choice is stored, a `profile_visibility_changed` security event is recorded in the same transaction, and the page shows `?msg=visibility-saved#visibility`.
3. **Given** a value other than `public` or `private`, a missing antiforgery token, or a signed-out request, **When** posted, **Then** nothing changes (`?msg=visibility-invalid`, the antiforgery refusal, or the sign-in redirect).
4. **Given** a user who chose `private` on an instance whose default becomes `public`, **When** anyone else requests their pages, **Then** 404.

---

### User Story 3 - Sharing a link (Priority: P1)

The owner creates a share link on `/account`. It's shown once, with a copy button. Whoever opens it can see the whole profile (every page under `/u/{username}`) in that browser, signed in or not, until the owner replaces the link or turns it off.

**Why this priority**: the owner's friends test is exactly this: share a private profile with a few people, not the world.

**Independent Test**: create a link, open it in a fresh browser: redirected to the dashboard, and DNA, a year and an anime page open too. Replace the link: the same browser gets 404, and the new link works. Turn it off: 404.

**Acceptance Scenarios**:

1. **Given** the owner, **When** they post "create share link", **Then**:
   - a new token is generated and only its hash is stored;
   - the link `https://{host}/s/{token}` is shown once (`data-share-link`);
   - a `share_link_created` event is recorded.

   Creating again replaces the old link (`share_link_replaced`).
2. **Given** a valid link, **When** a browser opens `/s/{token}`, **Then** it's redirected (303) to `/u/{username}` and gets a cookie granting that profile. Every page under `/u/{username}` answers 200 in that browser.
3. **Given** an unknown, malformed, replaced or turned-off token, **When** `/s/{token}` is opened, **Then** the plain 404 page, and no cookie.
4. **Given** a browser holding a grant, **When** the owner replaces the link or turns it off, **Then** the browser's next request gets 404 (the grant names a link that no longer exists).
5. **Given** a grant for user A, **When** that browser requests user B's private profile, **Then** 404.
6. **Given** the owner, **When** they post "turn off share link", **Then** the link ends and `share_link_revoked` is recorded. The section offers to create a new one.
7. **Given** a public profile, **When** the owner has a share link too, **Then** both work. The link matters again if they go private.
8. **Given** `/s/{token}` requests from one IP, **When** more than 30 arrive in a minute, **Then** 429 (`share` limiter).

---

### User Story 4 - Everything that points at a profile follows the rule (Priority: P2)

Links into a profile appear only for viewers who can follow them.

**Why this priority**: a dead link everywhere a private user commented would be confusing, and the operators' review must not point into histories.

**Independent Test**: a private user comments on an episode. A stranger sees the name as plain text, the owner and a link holder see it linked. The compatibility card and compare page appear only for viewers who can see the profile. The MyAnimeList review links to series pages.

**Acceptance Scenarios**:

1. **Given** a comment by a private user, **When** a viewer who can't see that profile reads the episode page, **Then** the author's display name shows without a link (`data-comment-author`, no `href`). A viewer who can see it gets the link.
2. **Given** a signed-in viewer and a private profile they can't see, **When** they request `/u/{username}/compare`, **Then** 404. **Given** they can see it (public or shared), **Then** the compare page and the dashboard card work as before.
3. **Given** a private user viewing a public profile, **When** the card renders, **Then** it shows (their own history is shown to them alone).
4. **Given** the MyAnimeList review page, **When** a season row renders, **Then** its link goes to `/anime/{seriesId}`, never to `/u/…`.
5. **Given** federation on and a private profile, **When** a page the viewer may see renders (owner or link holder), **Then** the handle line and ActivityPub link show as before. Strangers get the 404 and see neither. Megáfono's actor lookup is unchanged.

### Edge Cases

- A username that changes case in the address (`/u/AnimeGolden` vs `animegolden`) resolves as today; the rule applies to the user found.
- A deleted user: their grants point at nothing; 404 as today.
- A grant cookie that's tampered with or from another instance: rejected (data protection), treated as no grant.
- More than 20 grants in one browser: the oldest is dropped.
- The owner opens their own share link: redirected to their dashboard; no grant needed.
- Year redirect `/u/{username}/year` for a private profile: 404 for strangers, not a redirect that confirms a history exists.
- An operator CLI command (`user-list`) shows each user's effective visibility; operators still can't see private pages on the web.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every route under `/u/{username}` MUST answer the unknown-user 404 to a viewer who isn't the owner and holds no valid grant, unless the profile is public.
- **FR-002**: A profile is public when the owner chose `public`, or never chose and `Frunchy:Profiles:DefaultVisibility` is `public`. The setting defaults to `private`, and any other value stops startup naming the key.
- **FR-003**: `/account` MUST show a "Profile visibility" section (`#visibility`) to choose `public` or `private`, posting with `readValidatedForm`, the `account-write` limiter and a `profile_visibility_changed` security event in the same transaction.
- **FR-004**: The owner MUST be able to create (or replace) and turn off one share link. Its token is 256 random bits, shown once, and stored only as a SHA-256 hash with a link id. Events: `share_link_created`, `share_link_replaced`, `share_link_revoked`.
- **FR-005**: `GET /s/{token}` MUST check the token by hash. If valid, it adds a grant (user id + link id) to a data-protected, HttpOnly, SameSite=Lax cookie (Secure over https; at most 20 grants, 30 days) and redirects 303 to `/u/{username}`. Otherwise, the plain 404 and no cookie. Rate-limited per IP (`share`, 30 a minute).
- **FR-006**: A grant MUST count only while its link id is the user's current one.
- **FR-007**: Episode-page comment authors MUST link to their profile only when the viewer can see it.
- **FR-008**: The compare page and the dashboard compatibility card MUST follow FR-001 for the profile compared with.
- **FR-009**: The MyAnimeList review MUST link seasons to the series page, not to a user's anime page.
- **FR-010**: The owner's pages MUST show `data-profile-private` when the profile is private. The home page's note MUST match the instance default. All copy in English and Spanish.
- **FR-011**: `user-list` MUST show each user's effective visibility.
- **FR-012**: No change to `/api/*`, the extension contract, charts or "Watched by N" counts.

### Key Entities

- **User** (extended):
  - visibility choice: `public`, `private`, or none (follow the instance default);
  - share link: the token's hash, a link id, and when it was created (all empty when there is no link).
- **Grant**: kept in the browser only, in the signed cookie: a user id and the link id it was opened with.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a default instance, no page under `/u/` of any user is readable without being the owner or holding their link (tested on every route).
- **SC-002**: A shared link opens every page of the profile in one click, and stops working on the next request after the owner replaces or turns it off.
- **SC-003**: A private profile's 404 can't be told apart from an unknown username's.
- **SC-004**: The full suite passes, with the earlier profile tests running against public profiles.

## Assumptions

- The extension's views link to the owner's own dashboard only, so nothing there changes.
- Existing tests that read other users' profiles set `Frunchy:Profiles:DefaultVisibility=public` (or the user's choice) explicitly.
- Search engines: private pages are 404s; public pages are unchanged (no new `noindex` rule in this feature).

## Verbatim Constraints

- "dashboards must be private :/ … we can use an environment variable to be public or not.... and also the user could share a link with a token or make it public"
