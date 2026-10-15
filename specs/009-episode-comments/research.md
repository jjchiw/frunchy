# Research: Episode Page and Comments

## R1. Route and ids

- **Page**: `GET /anime/{seriesId}/episode/{episodeId}`. Both ids are checked against Crunchyroll's
  id alphabet `^[A-Z0-9]{6,20}$` (the anime page's check) before any query. All 549 ids in the 2026
  sample match it.
- **Found**: the episode exists, `cr_episodes.series_id = {seriesId}`, and `duration_ms IS NOT NULL`
  (unresolved stubs aren't pages, as everywhere else). Anything else gets the plain 404 page.
- **Case**: ids are matched exactly; a lowercase id is a 404, like the anime page.

## R2. The episode group (dubs share a thread)

The DNA page's dedup key, `coalesce(m.shared_from, e.season_id) || '#' || e.episode_number`, with
`m` the season's `matched` row in `cr_season_matches`. An episode with no season or no number is its
own group.

- **Query**: first the page episode's key (its original season and its number), then the group's
  episode ids: episodes with that number whose season is the original season, or a season whose
  matched row has `shared_from` = the original season. One statement.
- **Comments stay put**: a comment stores the `cr_episode_id` it was written on. The group is computed
  when the page is viewed, so a dub link that appears or disappears only regroups. Nothing is moved
  or rewritten (FR-010, SC-004).
- **Dub-of-a-dub**: `shared_from` always names the original (feature 003's matcher), so one level is
  enough.
- `// simplified:` the season's *matched* row is what links a dub. A dub whose match is manual and
  carries no `shared_from` has its own thread, which is the honest answer when Frunchy doesn't know.

## R3. Comment ids and double submits

- **Id**: 16 random bytes in base64url without padding: 22 characters of `[A-Za-z0-9_-]`, which
  matches `^[A-Za-z0-9_-]{1,64}$` (FR-019) and later becomes `Idempotency-Key: comment:{id}`.
- **Minted at render**: the page renders the comment form with a fresh id in a hidden field. The POST
  checks it against the pattern and inserts `ON CONFLICT (id) DO NOTHING`. A double submit hits the
  conflict and redirects to the existing comment, so only one comment is created (FR-014).
- **Someone else's id**: if the conflicting row isn't by this user on this episode, the POST
  re-renders the form with a fresh id and stores nothing. Guessing a 128-bit id is not a concern, and
  even then nothing leaks or changes.
- **Never reused** (Megáfono's rule): deleting a comment keeps its row as a tombstone (R5), so its id
  is never inserted again. Account deletion removes rows outright, and random ids make reuse
  practically impossible.

## R4. Storage shape, ready for the fediverse

One `comments` table (data-model.md). The fediverse-facing columns exist now and stay empty or at
their defaults:
- **Author**: `author_user_id` (local) or `remote_actor` (the remote actor's id, a URI), with a CHECK
  that exactly one is set. A later `remote_actors` table can be keyed on that URI without touching
  `comments`.
- **Text**: `content` plus `format` (`text` now; `html` for sanitized remote content later).
- **Warning**: `summary` + `sensitive`, Megáfono 002's publish fields, kept apart from the text.
- **Thread**: `in_reply_to` (a comment id or a remote object id; always NULL here), `audience`,
  and `object_id` (NULL until published, UNIQUE).
- **Audience**: `public` now. A CHECK keeps local comments to `public` or `unlisted`, the only
  audiences Megáfono publishes a reply with. Remote replies may arrive as `followers` or `direct`,
  and those are stored but not shown.

FR-020 is met: the integration adds data and maybe a `remote_actors` table, but no column to
`comments`.

## R5. Edit, delete, hide

- **Edit**: the author changes `content` and the spoiler flag; `summary` is recomputed (R7) and
  `edited_at` is set when something changed. Audience and `in_reply_to` never change (Megáfono
  refuses those on Update anyway).
- **Delete**: a tombstone. `deleted_at` is set, `content` becomes `''`, `summary` NULL. The row stays
  for the id rule and for the later `DELETE /internal/objects/{objectId}`. Deleted comments are never
  shown. `// simplified:` no "deleted" placeholder, since threads are flat.
- **Hide**: `hidden_at` + `hidden_by` (`ON DELETE SET NULL`), cleared on unhide. An author can still
  edit or delete a hidden comment; it stays hidden.
- **Who**: edit and delete need the author's session. Anyone else, signed out included, gets the
  plain 404 and nothing changes (FR-011). Hide and unhide are wrapped in `Layout.requireOperator`,
  outermost, before the form is read (FR-016).

## R6. Routes and slices

| Verb | Route | Slice | Guard |
|---|---|---|---|
| GET | `/anime/{s}/episode/{e}` | `Handlers/Episodes/Episode.fs` | public |
| POST | `/anime/{s}/episode/{e}/comments` | `Handlers/Episodes/Episode.fs` | signed in, else the sign-in redirect |
| GET | `/comments/{id}/edit` | `Handlers/Episodes/EditComment.fs` | author, else 404 |
| POST | `/comments/{id}/edit` | `Handlers/Episodes/EditComment.fs` | author, else 404 |
| POST | `/comments/{id}/delete` | `Handlers/Episodes/DeleteComment.fs` | author, else 404 |
| POST | `/comments/{id}/hide`, `/comments/{id}/unhide` | `Handlers/Episodes/HideComment.fs` | `requireOperator`, else 404 |

- **Create shares the page's file** (as 008's `Operators.fs` holds its GET and POSTs). An invalid
  comment re-renders the page with status 422, the error and the typed text (FR-013), without
  calling another slice's private code.
- **Redirects**: after a write, to the comment's own episode page with `#comment-{id}`.
- **Signed out**: the create POST uses `WebAuth.requireSignedIn`, so a session that expired while
  typing goes to sign-in and back to the page. The text is lost; that's acceptable for a 30-day
  session.

## R7. Spoiler warning text

Stored at write, in the author's language (`Texts.current()`), with `sensitive = true`:
- season display number and episode number: "Spoilers: S2 E5" / "Spoilers: T2 E5";
- no season number: "Spoilers: E5";
- no episode number: "Spoilers: {special label}" (the episode's label, else the "Special" text).

The season number comes from `cr_seasons.display_number`, else `season_number`. A remote reply later
brings its own `summary`, shown as written.

## R8. Showing spoilers

A native `<details>` per spoiler comment: the `<summary>` shows the warning and "Show spoiler", and the
text sits inside. Closed on load for every viewer (SC-003), keyboard- and screen-reader-accessible,
and no script needed. "Contains spoilers" is a checkbox ticked by default (clarification Q2).

## R9. Plain text

- **Storage and length**: `content` is stored as typed after trimming, with CRLF folded to LF and
  control characters other than LF and tab removed. The length is counted in Unicode scalar values
  (`EnumerateRunes`), 1 to 2,000 (FR-007).
- **Display**: ViewEngine escapes text, so markup stays text. `white-space: pre-line` keeps line
  breaks. Nothing is turned into a link.

## R10. Rate limits

A new `comment-write` limiter, per IP, 10 POSTs a minute, shared by create, edit and delete. Hide and
unhide use the existing `review-write` limiter, which counts operators only, so an outsider never sees
a 429 there (feature 007's rule).

## R11. Security events

`comment_hidden` and `comment_unhidden`, as in feature 008:
- on the author's account with `Actor.Operator`;
- on the operator's account with `Actor.User`;
- plus an information log line naming both users and the comment id.

They are written in the hide transaction. Authors' own creates, edits and deletes aren't account
security actions and record no event.

## R12. Who sees what

| Viewer | Normal comment | Spoiler | Hidden comment |
|---|---|---|---|
| Signed out / anyone | text | warning + closed toggle | "Hidden by an operator" |
| Author | text + Edit/Delete | same + Edit/Delete | "Hidden by an operator" + Edit/Delete |
| Operator | text + Hide | same + Hide | text (marked hidden) + Unhide |

Operators see their own comments with both sets of controls. Under the thread, the page says that
hiding a comment hides it on Frunchy (FR-018), shown only to operators. `// simplified:` the
fediverse wording ("copies on other servers may stay") arrives with the integration.

## R13. Header and links

- **Header**: the series title (`cr_series.title`, else the episode's `series_title`, else the id);
  the season title, else "Season N"; the audio language (`Texts.AudioLanguage`) and the dub mark when
  the season's match has `method = 'dub'` (the anime page's rule); the number or special label; the
  title; the minutes; and the thumbnail.
- **Crunchyroll links**: the episode at `https://www.crunchyroll.com/watch/{episodeId}` (Crunchyroll
  redirects it to the slugged address). The series at
  `https://www.crunchyroll.com/series/{id}/{slug}` when the slug is known. Both open in a new tab
  with `rel="noopener"`.
- **MyAnimeList**: the season's match gives its title, score, up to 3 genres and a link (the anime
  page's query, trimmed).
- **Anime page**: each episode row's title links to the episode page (`data-episode-link`).
- **Shared UI**: the season title/dub/language line and the compact MyAnimeList block now appear on
  two pages, so they move to `Views/Components.fs` (constitution III). The anime page keeps its own
  data access.

## R14. Performance

`ix_comments_episode (cr_episode_id, created_at)`. The thread is one query: comments of the group's
episodes, joined to `users` for names, oldest first. 1,000 comments render well under a second
(SC-005). A test inserts 1,000 rows and times the page against a generous bound.

## R15. Deletion and rebuilds

- **Account deletion**: `author_user_id ... ON DELETE CASCADE` removes the author's comments.
  `hidden_by ... ON DELETE SET NULL` keeps a hide when the operator's account goes.
- **Catalog**: rows are never deleted (`user-rebuild` only clears `watch_events` and issues), so the
  `cr_episodes` foreign key is safe.
- **Rebuild and re-sent history**: they don't touch `comments` (FR-023). A test pins this.

## R16. Design

There's no mock for this screen. `design/BRIEF.md` §5.5 describes it. The page is built from the
shared components and goes through the `impeccable` skill (shape → build → audit) with the §5.5
variants. Tests read `data-*` hooks only (contract).
