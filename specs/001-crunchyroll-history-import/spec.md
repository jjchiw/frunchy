# Feature Specification: Crunchyroll Watch-History Import with Real Dashboard

**Feature Branch**: `001-crunchyroll-history-import`

**Created**: 2026-10-02

**Status**: Draft

**Input**: User description: "Crunchyroll watch-history import with a real dashboard. The user sends their Crunchyroll watch history to Frunchy (via the browser extension, which intercepts Crunchyroll's own watch-history responses page by page) and then sees real numbers on the dashboard: total series, total episodes, total hours and the most recently watched episodes. Everything is derived from Crunchyroll's data; external anime databases are a later feature."

## Clarifications

### Session 2026-10-02

- Q: How is the user identified without login? → A: Each user has a personal sync key, created by the operator (no sign-up page). The extension sends it with every submission. Real login is a later feature.
- Q: When a later sync brings a newer watch date for an already-imported episode, is it a rewatch or an update? → A: Update. One watch per episode and the newest date wins. Rewatch counting can be enabled later by reprocessing the stored originals.
- Q: Are the dashboard period filters (Hoy / Esta semana / Este mes / Todo el tiempo) in scope? → A: No. This feature shows all-time figures only; the filters are deferred.
- Q: Who can see a dashboard, and how is the user chosen? → A: Each user has a public, read-only dashboard at an address containing their username (e.g. `/u/animenerd`). No login; users are told their history is public.
- Q: What does the extension need from the backend beyond submitting and checking status? → A: A "who am I" lookup by sync key returning display name, username, dashboard address and last sync time. No monthly statistics.
- Q: Which time zone do "Ayer", "Hace 3 días" and clock times on the dashboard use? → A: The viewer's own browser time zone. The page carries exact times, and the viewer's browser shows them relative and local, in the page's language.
- Q: Can a user and their history be deleted? → A: Yes. The operator can delete a user, which removes all their data (original submissions, history, dashboard). This is an explicit exception to constitution Principle I (amended in v1.1.0).
- Q: How much i18n goes into this feature? → A: English (default) and Spanish. The language follows the viewer's browser preference and falls back to English; there's no language switcher yet. All UI text comes from per-language text, never hardcoded in views.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Send my watch history and have it processed (Priority: P1)

As a Frunchy user with a personal sync key, I send one or more pages of my Crunchyroll watch history, either my whole history on the first sync or only the newest entries on later syncs. Frunchy confirms receipt immediately and processes the history in the background. I can ask how a submission is going: whether it is waiting, in progress, finished or failed, how many entries were processed, and which entries couldn't be used and why. Sending the same entries again, fully or partially overlapping, never counts anything twice.

**Why this priority**: Nothing else in Frunchy works without the history. It is the product's core asset, and the first sync of a long-time user is the most demanding case.

**Independent Test**: Submit the real sample history (`JsonSamples/PostHistory.json`, 101 entries) and query the submission's status until it finishes. All 101 entries must be accounted for: 100 processed and 1 unresolved (the entry without episode details). Then submit the same page again and confirm that the user's watched-episode count doesn't change.

**Acceptance Scenarios**:

1. **Given** a user with no history in Frunchy, **When** they submit one page of Crunchyroll history, **Then** they immediately receive a confirmation with a submission reference, and the submission's status is "pending" or "processing".
2. **Given** a submitted page, **When** background processing finishes, **Then** the status is "completed" and reports the number of entries received, processed, unresolved and failed, and these add up to the number of entries received.
3. **Given** a first sync made of 15 pages (about 1,500 entries) sent one after another, **When** all are processed, **Then** every entry is reflected exactly once in the user's history.
4. **Given** a page that was already processed, **When** the same page is submitted again, **Then** the user's totals are unchanged and no episode appears twice.
5. **Given** a page where one entry has no episode details (no title, series or duration), **When** it is processed, **Then** that entry is kept, reported as "unresolved" in the status, excluded from the totals, and the rest of the page is processed normally.
6. **Given** an entry that was unresolved in an earlier submission, **When** a later submission contains the same episode with full details, **Then** the episode becomes resolved and starts counting toward the totals.
7. **Given** a submission that is not a Crunchyroll history page at all (malformed or missing its list of entries), **When** it is submitted, **Then** it is rejected immediately with an explanatory error and nothing is stored.
8. **Given** a submission with a missing, unknown or revoked sync key, **When** it is submitted, **Then** it is rejected immediately as unauthorized and nothing is stored.
9. **Given** an episode already in the history watched on 2023-01-09, **When** a later submission contains the same episode with a watch date of 2023-03-01, **Then** the episode still counts once and its watch date becomes 2023-03-01.
10. **Given** a user's submission status reference, **When** a different user's sync key is used to query it, **Then** the status is not disclosed.
11. **Given** a valid sync key, **When** the extension asks who it belongs to, **Then** it receives the user's display name, username, dashboard address and last sync time (empty if never synced).
12. **Given** an unknown or revoked sync key, **When** the extension asks who it belongs to, **Then** the request is rejected as unauthorized, so the extension can tell the user their key is wrong.

---

### User Story 2 - See my real numbers on the dashboard (Priority: P2)

As a Frunchy user, my dashboard lives at a public address with my username (e.g. `/u/animenerd`), so I can share it like a Last.fm profile. When anyone opens it, they see my real anime numbers instead of placeholder values: how many series I've watched, how many episodes, how many hours (with the equivalent in days of my life), when my history was last synchronized, and a list of my most recently watched episodes. Each recent item shows the series title, episode number, duration, thumbnail and how long ago I watched it (e.g. "2 hours ago" / "Hace 2 horas"). If I haven't synced anything yet, the dashboard tells me how to sync from the extension instead of showing zeros or fake data. The dashboard speaks my browser's language: Spanish or English.

**Why this priority**: This is the first screen where users see their own data and see that Frunchy is worth using. It depends on User Story 1 for data, but it can be tested on its own with a prepared history.

**Independent Test**: With the sample history processed for a user, open that user's dashboard address. It shows 29 series, 96 episodes and about 39.3 hours (about 1.6 days). The most recent item is "The Vampire Dies in No Time", episode 2, watched on 2023-01-09. A user with no history sees the empty state.

**Acceptance Scenarios**:

1. **Given** a user whose sample history has been processed, **When** anyone opens that user's dashboard, **Then** they see 29 series, 96 episodes and 39.3 hours, plus the equivalent in days (1.6).
2. **Given** a user with processed history, **When** anyone opens that user's dashboard, **Then** the 6 most recently watched episodes are listed newest first, each with series title, episode number (or "Especial" when Crunchyroll gives none), duration in minutes, thumbnail, and a relative time plus clock time in the page's language and the viewer's local time zone.
3. **Given** a user with processed history, **When** anyone opens that user's dashboard, **Then** they see when their history was last synchronized and which episode was the newest one received.
4. **Given** a user with no processed history, **When** anyone opens that user's dashboard, **Then** they see an empty state, in the page's language, explaining how to sync their history from the extension, and no placeholder numbers.
5. **Given** a submission still being processed, **When** anyone opens that user's dashboard, **Then** it shows the totals processed so far and indicates that a sync is in progress.
6. **Given** two users with different histories, **When** each dashboard address is opened, **Then** each shows only its own user's figures.
7. **Given** an address with a username that doesn't exist, **When** it is opened, **Then** a "user not found" page is shown in the page's language.
8. **Given** a viewer whose browser prefers Spanish, **When** they open any dashboard, **Then** all interface text, including relative times ("Hace 2 horas") and the "Especial" label, is in Spanish. A viewer whose browser prefers English, or any language Frunchy doesn't support, sees English ("2 hours ago", "Special").

---

### User Story 3 - Rebuild or erase a user's history (Priority: P3)

As the operator of Frunchy, I can throw away everything derived from a user's history (their normalized watch history and their dashboard numbers) and rebuild it from the original submissions, which Frunchy keeps exactly as they were received. The rebuilt result is identical to the original.

The operator can also delete a user entirely when that user asks, removing everything Frunchy holds about them.

**Why this priority**: This protects the core asset and covers erasure requests now that profiles are public. If processing rules change or a bug corrupts derived data, nothing is lost. Users don't see it directly, so it comes after the user-facing stories.

**Independent Test**: Process the sample history and note the dashboard numbers. Trigger a rebuild for that user, wait for it to finish, and confirm the numbers and the recent-episodes list are identical.

**Acceptance Scenarios**:

1. **Given** a user with processed history, **When** the operator triggers a rebuild for that user, **Then** after it finishes the totals and recent episodes are identical to those before the rebuild.
2. **Given** any number of submissions, **When** they are processed in any order, **Then** the stored originals are never modified or deleted.
3. **Given** a user with submissions, history and a dashboard, **When** the operator deletes that user, **Then** their original submissions, watch history and dashboard figures are all removed, their dashboard address shows "user not found", and their sync key is rejected.
4. **Given** a submission of that user still waiting to be processed, **When** the user is deleted, **Then** it is never processed and no data for that user reappears.

---

### Edge Cases

- **Entries without episode details** (no title, series or duration): kept, reported as unresolved, excluded from totals. They get resolved if a later submission brings the details (US1 scenario 6).
- **Episodes without an episode number** (specials, OVAs; 2 in the sample): counted normally and displayed as "Special" / "Especial".
- **Partially watched entries**: they don't count as watched episodes, but the watched portion counts toward hours (see Assumptions).
- **Out-of-order submissions** (an older page arrives after a newer one): the result is the same as if they had arrived in chronological order. For each episode, the most recent watch date wins.
- **Same series split across seasons, or dubs listed as separate seasons** (e.g. "Ranking of Kings" season 2 with the same title as season 1): counts as one series.
- **Non-episode content** (e.g. movies, if Crunchyroll's history includes them): kept and reported as unsupported in the status; excluded from totals in this feature.
- **Page size not fixed**: Crunchyroll decides how many entries a page has (the sample page reports `total: 100` but contains 101 entries). Submissions MUST NOT be rejected for having more than 100 entries; the accepted size needs comfortable headroom above Crunchyroll's page size.
- **Very large first sync** (e.g. 5,000 entries): accepted and processed without the user having to keep the browser open after submitting.
- **Concurrent submissions from the same user**: each one is processed fully and the end result is the same as processing them one after another.
- **Unknown or revoked sync key**: the submission is rejected as unauthorized and nothing is stored (FR-001).
- **Watch date moves backwards** (an older page re-sent after a newer one): the newer date is kept (FR-011).
- **Resumed partial watch**: a later submission marks a previously partial episode as fully watched; it starts counting as a watched episode, with no duplicate.
- **Viewer in another time zone**: an episode watched at 2023-01-09 22:14 UTC shows as "9 ene 23:14" to a viewer in Madrid and "9 ene 19:14" to a viewer in Buenos Aires. Both see the same relative time.
- **Username not found** on a dashboard address: a "user not found" page in the page's language, never another user's data.
- **Unsupported browser language** (e.g. French): the page is shown in English.
- **Anime and episode titles** are shown as Crunchyroll provided them (the language of the user's Crunchyroll session) and are not translated by Frunchy.
- **Durations longer than a regular episode** (e.g. a 90-minute special): counted with their real duration.

## Requirements *(mandatory)*

### Functional Requirements

**Submitting history**

- **FR-001**: The system MUST associate each submission with a Frunchy user through that user's personal sync key, sent with every submission. Submissions with a missing, unknown or revoked key MUST be rejected as unauthorized without storing anything.
- **FR-001a**: The operator MUST be able to create a user with a unique, URL-safe username and a display name, which issues that user's sync key, and to regenerate a user's key. Regenerating revokes the previous key immediately. Issued keys MUST NOT be retrievable afterwards; a lost key can only be regenerated.
- **FR-001b**: Submission status MUST only be disclosed to requests authenticated with the owning user's sync key.
- **FR-001c**: The system MUST provide a "who am I" lookup authenticated by sync key, returning the user's display name, username, dashboard address and last sync time. An unknown or revoked key MUST be rejected as unauthorized.
- **FR-002**: The system MUST accept a submission containing one or more Crunchyroll watch-history pages in the exact form Crunchyroll returns them (page total, list of entries, paging information).
- **FR-003**: The system MUST confirm receipt of a valid submission immediately, with a submission reference, without waiting for processing to finish.
- **FR-004**: The system MUST reject a submission immediately, with an explanatory error and without storing anything, when it is not a Crunchyroll history page (not parseable or missing its list of entries).
- **FR-005**: The system MUST store every accepted submission exactly as received and MUST never modify or delete it afterwards.

**Processing**

- **FR-006**: The system MUST process accepted submissions in the background and turn each entry into the user's watch history: series, season, episode, watch date, watched portion, duration, title and thumbnail.
- **FR-007**: The system MUST identify each watched episode by the user plus Crunchyroll's episode identifier. Submitting the same episode again MUST NOT create a second watched episode. When a later submission brings a different watch date, watched portion or fully-watched flag for an episode already in the history, the system MUST update that single watch to the most recent state (newest watch date wins). Rewatches MUST NOT add to totals in this feature.
- **FR-008**: The system MUST keep entries that lack episode details, mark them as unresolved, and resolve them automatically when a later submission provides the details for the same episode.
- **FR-009**: The system MUST record, per entry, any processing failure with a human-readable reason. A failing entry MUST NOT stop the rest of the submission from being processed.
- **FR-010**: The system MUST report each submission's status (pending, processing, completed, failed) and its counts of entries received, processed, unresolved, unsupported and failed, plus the list of failed and unresolved entries with their reasons.
- **FR-011**: Processing results MUST NOT depend on the order in which submissions arrive. For each episode, the latest watch date wins.

**Dashboard**

- **FR-011a**: Each user MUST have a public, read-only dashboard at an address containing their username. No login is required to view it.
- **FR-011b**: A dashboard MUST show only the data of the user named in its address, and MUST NOT expose sync keys, submission status details or failed/unresolved entries.
- **FR-011c**: The dashboard MUST state that the profile is public.
- **FR-012**: The dashboard MUST show, for the user whose dashboard is viewed: number of distinct series with at least one fully watched episode (one Crunchyroll series counts once regardless of seasons or dubs), number of fully watched episodes, total hours watched (one decimal) and that total expressed in days (one decimal).
- **FR-013**: The dashboard MUST list the 6 most recently watched episodes, newest first, each with series title, episode number (or "Special" / "Especial"), duration in minutes, thumbnail, and its watch time shown relative (e.g. "2 hours ago", "Yesterday") with the clock time.
- **FR-013a**: Watch times and the last-sync time MUST be shown in the viewer's local time zone. The page MUST carry the exact time, so the display is correct for viewers in any time zone. Before or without local formatting, the page MUST still show an unambiguous date.
- **FR-014**: The dashboard MUST show when the user's history was last synchronized and the newest episode received, and indicate when a sync is still being processed.
- **FR-015**: The dashboard MUST show an empty state with instructions to sync from the extension when the user has no processed history, and MUST NOT show placeholder numbers.
- **FR-016**: The dashboard MUST show all-time figures only. The design's period filters ("Hoy", "Esta semana", "Este mes", "Todo el tiempo") are not shown in this feature.
- **FR-017**: All user-facing text MUST come from per-language text and MUST NOT be hardcoded in views. This feature ships English (default) and Spanish.
- **FR-017a**: The page language MUST follow the viewer's browser language preference: Spanish if preferred, otherwise English. Relative and clock times MUST be formatted in the page's language.

**Rebuild and deletion**

- **FR-018**: The system MUST let the operator rebuild a user's derived history and dashboard figures from that user's stored submissions. The result MUST be identical to the original processing.
- **FR-019**: The operator MUST be able to delete a user. Deletion MUST remove all of that user's data: original submissions, entry outcomes, watch history, dashboard figures and sync key. It MUST NOT affect other users' data. This is the only case in which original submissions are deleted.

### Key Entities

- **User**: a Frunchy user that owns submissions and a watch history. Created by the operator; in this feature it has a unique URL-safe username (used in the dashboard address), a display name and one active sync key.
- **Sync Key**: a secret credential that identifies a user's submissions. One active key per user; it can be regenerated, which revokes the previous one.
- **History Submission**: one delivery of one or more Crunchyroll history pages, stored exactly as received. It has a reference, an owner, a received time, a processing status and per-status entry counts.
- **Submission Entry Outcome**: the result of processing one entry of a submission: processed, unresolved, unsupported or failed, with a reason when it isn't processed.
- **Series**: a Crunchyroll series (e.g. "Bungo Stray Dogs") identified by Crunchyroll's series identifier, with its title. Seasons and dubs belong to it.
- **Season**: a Crunchyroll season of a series, with its number and title (may be a dub variant).
- **Episode**: a Crunchyroll episode, identified by Crunchyroll's episode identifier, with episode number (optional), title, duration and thumbnail.
- **Watch Event**: the user watched an episode, with the most recent watch time, the watched portion and whether it was watched fully. There is exactly one per user and episode in this feature. It is derived from submission entries and can always be rebuilt from them.
- **Dashboard Figures**: the per-user totals and recent list shown on the dashboard, derived from watch events.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user submitting a 100-entry history page gets a confirmation in under 2 seconds.
- **SC-002**: A first sync of 1,500 entries is fully reflected on the dashboard within 5 minutes of the last page being submitted.
- **SC-003**: Every submitted entry is accounted for: received = processed + unresolved + unsupported + failed, for 100% of submissions.
- **SC-004**: With the sample history (`JsonSamples/PostHistory.json`) processed, the dashboard shows exactly 29 series, 96 episodes and 39.3 hours, and the status reports 100 processed and 1 unresolved.
- **SC-005**: Submitting the same history 3 times leaves the totals identical to submitting it once (0 duplicate episodes).
- **SC-006**: A rebuild from stored submissions produces totals and a recent-episodes list identical to the original processing, for 100% of tested users.
- **SC-007**: The dashboard opens in under 2 seconds for a user with 5,000 watched episodes.
- **SC-008**: A user with no history never sees placeholder or invented numbers on the dashboard.
- **SC-009**: 100% of submissions with a missing, unknown or revoked sync key are rejected, and none of them leave stored data.
- **SC-010**: After a user is deleted, none of their submissions, history or figures remain, and other users' figures are unchanged.
- **SC-011**: Every piece of interface text on the dashboard, empty state and "user not found" page exists in both English and Spanish, and switching the browser's preferred language changes all of it (0 untranslated strings).

## Assumptions

- **Source of submissions**: the browser extension will send the pages it intercepts from Crunchyroll's history. Changing the extension is out of scope; this feature only defines and serves what it must send.
- **Partial watches**: an entry not marked as fully watched doesn't count as a watched episode. Its watched portion (capped at the episode's duration) counts toward total hours. This is why the sample yields 96 episodes and 39.3 hours rather than 39.1.
- **Series counting** uses Crunchyroll's series identifier, so seasons and dub variants of the same show count once.
- **Thumbnails** come from the images Crunchyroll includes in each entry, so no external catalog is needed.
- **Relative times** are computed against the viewer's current time and local time zone, in the page's language ("2 hours ago" / "Hace 2 horas", "Yesterday" / "Ayer"), with the clock time shown alongside as in the design.
- **Language selection** is automatic from the browser preference; a manual language switcher and more languages are later work. The extension's own language is decided in the extension.
- **Deletion** is an operator action on request; there's no self-service delete button in this feature. A deleted username may be reused later.
- **Rebuild** is an operator action (no end-user button) and may run in the background.
- **Operator actions** (create user, regenerate sync key, rebuild) don't need a polished UI in this feature. A simple operator-only mechanism is enough.
- **Public profiles**: dashboards are public, as on Last.fm. Telling a user that their history will be public happens when the operator creates their account. Private profiles are a later feature.
- **Rewatch history**: Crunchyroll only keeps the latest watch date per episode, so past rewatches can't be recovered anyway. Keeping the original submissions means rewatch counting can be added later by reprocessing.
- **Out of scope for this feature**: matching with MyAnimeList (genres, scores, studios, MAL cover art), Anime DNA, ratings, XP or collection ranks, Frunchy trends, community and followers, the AniList/MAL sync button, monthly sync statistics for the extension popup ("Este mes … vs mes anterior"), the weekly activity heatmap, streaks and episodes-per-day, series completion percentages (Crunchyroll's history doesn't say how many episodes a series has), a full-history page ("Ver historial completo" / "Cargar más"), favorites, dashboard period filters, real login/sign-up, and counting rewatches.
- **Scale**: a long-time user may have several thousand entries. Sync frequency is low (a few times per day per user at most).
