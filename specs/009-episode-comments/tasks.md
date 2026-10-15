# Tasks: Episode Page and Comments

**Input**: [episode-comments.spec.md](episode-comments.spec.md), [plan.md](plan.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/episode-page.md](contracts/episode-page.md), [quickstart.md](quickstart.md)

**Tests**: required by constitution V. Integration tests run against real Postgres (Testcontainers) with the
2026 sample and the feature 003 MyAnimeList fixtures. `AnimeTests.withSample` shows how to import and enrich
it; season `GRK5CNJ14` is a dub with its original in the same series.

**Delegation**: Sonnet subagents write the code. The main session reviews each diff (plan "Security review
points" first) and runs the tests. Load the `oxpecker` skill for routes and views, and the `impeccable`
skill for UI. Tests read `data-*` hooks only (contract), never classes.

## Phase 1: Setup

- [x] T001 Register empty modules in compile order:
  - `Handlers/Episodes/Episode.fs`, `EditComment.fs`, `DeleteComment.fs` and `HideComment.fs` after `Handlers/Review/Operators.fs`;
  - the test files `EpisodeHelpers.fs`, `EpisodePageTests.fs`, `CommentsTests.fs`, `CommentThreadGroupTests.fs`, `CommentEditTests.fs` and `CommentModerationTests.fs` after `OperatorsPageTests.fs`.

  `dotnet build` stays at 0 warnings · FrunchyBackend.fsproj, tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj

## Phase 2: Foundational (blocks every story)

**Wave 1, independent (different files):**

- [x] T002 [P] `comments` table and `ix_comments_episode`, exactly as in data-model.md (both CHECKs on author and the local-audience CHECK), in `initializeSchema`; document the table and the `comment_hidden`/`comment_unhidden` events in SCHEMA.md · Infrastructure/Database.fs, SCHEMA.md
- [x] T003 [P] `SecurityEventType.CommentHidden` / `CommentUnhidden` (`comment_hidden`, `comment_unhidden`) in the union, `toDb` and `all` · Domains/Identity.fs
- [x] T004 [P] Every text of the feature, en and es, in `Views/Texts.fs` (no literal in views):
  - **Page and header**: title, "Watch on Crunchyroll", the series link, the season-number fallback.
  - **Thread**: heading, empty state, the edited mark, "Hidden by an operator", the operator-only moderation note (hiding hides it on Frunchy), "Show spoiler".
  - **Form**: label, "Contains spoilers", the submit button, the sign-in prompt.
  - **Errors**: `CommentError: string -> string` over `empty`, `too-long` and `bad-id`.
  - **Edit page**: title, save and cancel, the delete button and its confirm text.
  - **Moderation**: hide and unhide buttons.
  - **Spoiler warning** (research R7): `SpoilerSummary: string option -> int option -> string option -> string`, taking the season display number, the episode number and the special label; it gives "Spoilers: S2 E5" / "Spoilers: E5" / "Spoilers: {label}", and in Spanish "Spoilers: T2 E5".
  - **Event labels**: the two in both `SecurityEventLabel` blocks.

  · Views/Texts.fs

**⟶ Wait for Wave 1, then:**

- [x] T005 Move the season line (title, audio language via `Texts.AudioLanguage`, dub mark) and `malCompact` from `Handlers/Users/Anime.fs` to `Views/Components.fs` (research R13), keeping every `data-*` hook. `Anime.fs` calls them, and `AnimeTests`/`AnimeLinkTests` stay green unchanged · Views/Components.fs, Handlers/Users/Anime.fs
- [x] T006 [P] Test helpers in `EpisodeHelpers.fs`:
  - `episodeUrl series ep`;
  - `episodePage app lang series ep` (GET, decoded with `readHtml`);
  - `comments html`, which returns each `data-comment` with its attributes (author, episode, spoiler, edited, hidden) and text;
  - `postComment (browser: Browser) series ep text spoiler`, which reads the form's `id` from the page and posts with the antiforgery token;
  - `rawCommentRows db`, a dump of `comments`.

  · tests/FrunchyBackend.Tests/EpisodeHelpers.fs

**Checkpoint**: schema, texts and shared components in place; the suite is green.

## Phase 3: User Story 1 - One page per episode (Priority: P1) 🎯 MVP

Goal: a public episode page with header, Crunchyroll links and MyAnimeList details, linked from the anime
page. Independent test: open matched, unmatched, dub and numberless episodes from the sample; follow a link
from the anime page.

- [x] T007 [US1] `EpisodePageTests`:
  - **Header (SC-001)**: for a matched episode, an unmatched one, a `GRK5CNJ14` dub episode and a numberless special, every header hook (`data-episode-number`, `-title`, `data-minutes`, `data-audio`, `data-dub`), both Crunchyroll links (`https://www.crunchyroll.com/watch/{e}`, and `/series/{id}/{slug}` only with a slug) and the MyAnimeList block (only when matched) equal a query over the catalog, in en and es.
  - **404s**: lowercase or bad-alphabet ids (no query), an unknown episode, an unresolved stub, an episode of another series.
  - **Anyone**: signed out and signed in see the same header.
  - **Anime page**: every `data-episode` row has a `data-episode-link` whose href opens a 200 episode page.

  · tests/FrunchyBackend.Tests/EpisodePageTests.fs
- [x] T008 [US1] `Handlers/Episodes/Episode.fs` GET: view → private SQL (one query for the episode, season, `method = 'dub'`, series title/slug, the matched MyAnimeList entry with up to 3 genres) → handler (id pattern check before any query, 404 via `Layout.messagePage`) → `GET [ routef "/anime/{%s}/episode/{%s}" … ]`. Leave an empty thread section with `data-thread="0"` and `data-thread-empty`. Add the endpoint to `Program.endpoints`. Go through impeccable shape → build for the header · Handlers/Episodes/Episode.fs, Program.fs
- [x] T009 [US1] Anime page: each episode row's title links to `/anime/{seriesId}/episode/{id}` with `data-episode-link` · Handlers/Users/Anime.fs

**Checkpoint**: the episode page exists and is reachable.

## Phase 4: User Story 2 - Read and write comments (Priority: P1)

Goal: signed-in users write plain-text comments, spoilers closed behind `<details>`, signed-out visitors read
and get a sign-in prompt. Independent test: two users comment (one spoiler), then read as each and signed out.

- [x] T010 [US2] `CommentsTests`:
  - **Create (SC-002)**: two users comment; the thread shows them oldest first with `data-author`, `data-created` and text. The spoiler box is checked by default (`data-spoiler-toggle`), a ticked comment stores the `SpoilerSummary` warning in the author's language with `sensitive = true`, and an unticked one stores NULL/false.
  - **Spoilers closed (SC-003)**: every `data-spoiler` `<details>` has no `open` attribute, for every viewer.
  - **Validation**: empty, whitespace-only, 2,001 runes and a malformed id each give 422 with `data-comment-error`, the typed text kept, and no row. Exactly 2,000 runes (emoji included) is accepted.
  - **Double submit**: the same form posted twice gives one row and a redirect to `#comment-{id}`. Reusing another user's id stores nothing.
  - **Plain text**: `<script>` and `<a>` stay text, and line breaks are kept.
  - **Signed out**: `data-signin-prompt` with `returnUrl` set to the page, no form; a POST redirects to sign-in and stores nothing.
  - **Rate limit**: the 11th write in a minute gets 429.
  - **Rebuild (FR-023)**: `user-rebuild` and re-sending the sample leave `comments` byte-identical.
  - **Account deletion**: the author's comments go, and other users' comments stay.
  - **1,000 comments (SC-005)**: seeded directly, the page renders them all in under 1 s, measured after a warm-up request.

  · tests/FrunchyBackend.Tests/CommentsTests.fs
- [x] T011 [US2] In `Episode.fs`:
  - **Thread query**: the comments of this episode only for now (US3 widens it). Shown when `deleted_at IS NULL AND audience IN ('public','unlisted')`, ordered by `created_at, id`, joined to `users` for display name and username (linking to `/u/{username}`).
  - **Rendering**: as the contract says. Spoilers go in `<details data-spoiler>` with `<summary data-spoiler-summary>`. Text is `white-space: pre-line`.
  - **Form**: for signed-in users, with a freshly minted id (research R3: 16 random bytes, base64url without padding) and the checkbox ticked. Signed-out visitors get the sign-in prompt instead.
  - **POST create**: `/anime/{s}/episode/{e}/comments`, wrapped in `WebAuth.requireSignedIn`, then `readValidatedForm`, then the id pattern, then normalize and validate (research R9). `INSERT … ON CONFLICT (id) DO NOTHING`, then a 302 to `#comment-{id}`, or a 422 re-render. The rules for a conflicting id are in research R3.
  - **Limiter**: add `comment-write` (`perIp`, 10 a minute) in Program.fs and `RequireRateLimiting` on the POST.

  · Handlers/Episodes/Episode.fs, Program.fs

**Checkpoint**: MVP: people can read and write comments on an episode.

## Phase 5: User Story 3 - A dub and its original share one thread (Priority: P2)

Goal: comments on an original and its linked dub show on both pages. Independent test: comment on both,
unlink, relink.

- [ ] T012 [US3] `CommentThreadGroupTests`, with the enriched sample:
  - **Shared thread**: a comment on a `GRK5CNJ14` episode and one on the original season's episode with the same number both show on both pages, each with its own `data-episode`.
  - **Unlinking**: setting the dub's match to `no_match` (and `shared_from` to NULL) makes each page show only its own comments. The `comments` dump is unchanged.
  - **Relinking**: the thread is shared again.
  - **Own threads**: a numberless episode and a seasonless one keep their own thread.

  · tests/FrunchyBackend.Tests/CommentThreadGroupTests.fs
- [ ] T013 [US3] Widen the thread query to the episode's group (research R2): the group's episode ids are those with the page episode's number whose season is the original (`coalesce(shared_from, season_id)` over the `matched` row) or a season whose matched row has that `shared_from`. An episode without a season or number is its own group. One statement · Handlers/Episodes/Episode.fs

## Phase 6: User Story 4 - Authors manage their comments (Priority: P2)

Goal: authors edit and delete; nobody else can. Independent test: edit and delete as the author; force both
as another user and signed out.

- [ ] T014 [US4] `CommentEditTests`:
  - **Edit**: the author changes the text and the spoiler flag. `data-edited` appears, `summary` is recomputed or cleared, and `audience`/`in_reply_to` are unchanged. A no-op edit doesn't set `edited_at`. Invalid input gives 422 with the edit page and the text kept.
  - **Delete**: the comment leaves every page. The row stays as a tombstone (`deleted_at` set, content `''`, summary NULL), and a create reusing its id stores nothing.
  - **Refusals (SC-006)**: another user, a signed-out visitor and an operator get 404 on GET/POST edit and POST delete, and nothing changes. A deleted comment's edit is 404.
  - **Controls**: only the author sees `data-comment-edit`/`data-comment-delete`.

  · tests/FrunchyBackend.Tests/CommentEditTests.fs
- [ ] T015 [US4] Implement editing and deleting:
  - **`EditComment.fs`**: `GET`/`POST /comments/{id}/edit` (`data-comment-edit-page`). The author check runs before reading the form, and anyone else gets 404.
  - **`DeleteComment.fs`**: `POST /comments/{id}/delete` writes the tombstone.
  - **Redirects**: both go to the comment's own episode page.
  - **Episode page**: shows the author's controls.
  - **Program.fs**: the endpoints, with `comment-write` on both POSTs.

  · Handlers/Episodes/EditComment.fs, Handlers/Episodes/DeleteComment.fs, Handlers/Episodes/Episode.fs, Program.fs

## Phase 7: User Story 5 - Operators hide comments (Priority: P3)

Goal: operators hide and unhide; outsiders can't tell the routes exist. Independent test: the access matrix
and what each viewer sees.

- [ ] T016 [US5] `CommentModerationTests`:
  - **Access**: a visitor and a regular user get 404 on POST hide/unhide (with and without a token) and never 429 after 35 POSTs, and nothing changes.
  - **Hiding (as an operator, via `OperatorActions.add … Server` and the root setting)**:
    - visitors and the author see `data-hidden-placeholder`;
    - operators see the text, `data-hidden` and `data-comment-unhide`;
    - only operators see `data-moderation-note`.
  - **Events**: `comment_hidden`/`comment_unhidden` on the author's account (`operator`) and the operator's (`user`).
  - **Author on a hidden comment**: can edit it (it stays hidden) and delete it.
  - **Operator account deleted**: the hide stays with `hidden_by` NULL.
  - **Unhide**: restores the comment as before.

  · tests/FrunchyBackend.Tests/CommentModerationTests.fs
- [ ] T017 [US5] `HideComment.fs`: `POST /comments/{id}/hide` and `/unhide`, wrapped in `Layout.requireOperator` outermost, then `readValidatedForm`, then one transaction that updates `hidden_at`/`hidden_by` and records both events (`SecurityEvents.record`), then an information log line naming the operator, the author and the comment id, then a 302 to `#comment-{id}`. Unknown or deleted comments get 404. The episode page renders the hidden states and operator controls (research R12). Add the endpoints in Program.fs with `review-write` · Handlers/Episodes/HideComment.fs, Handlers/Episodes/Episode.fs, Program.fs

## Phase 8: Polish

**Wave 1, independent (different files):**

- [ ] T018 [P] Add the episode page (signed out, with comments, as an operator with a hidden comment) and the edit page to the every-page design-system check, asserting the contract's hooks · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [ ] T019 [P] Docs:
  - **CLAUDE.md**: the routes (`/anime/{s}/episode/{e}`, `/comments/{id}/…`, the `comment-write` limiter, `Handlers/Episodes/`).
  - **FRONTEND.md**: the page.
  - **design/BRIEF.md** §5.5: marked built.
  - **docs/fediverse.md**: step 1 done, with a pointer to `data-model.md`.
  - **README.md**: the roadmap entry 009.

  · CLAUDE.md, FRONTEND.md, design/BRIEF.md, docs/fediverse.md, README.md

**⟶ Wait for Wave 1, then:**

- [ ] T020 Impeccable audit of the §5.5 variants (no comments, long thread, hidden comment, signed out, Spanish, 360px); fix the blocking findings · Handlers/Episodes/Episode.fs, Handlers/Episodes/EditComment.fs
- [ ] T021 Run the full `dotnet test` with 0 warnings, validate SC-001 to SC-007 against quickstart.md, and append the validation log · specs/009-episode-comments/quickstart.md

## Dependencies & Execution Order

- **Order**: Setup → Foundational → US1 → US2 → US3 → US4 → US5 → Polish. US3, US4 and US5 each need US2's thread, and are otherwise independent of each other.
- **Foundational**: T002, T003 and T004 in parallel, then T005 with T006 in parallel.
- **Tests first in each story**: T007, T010, T012, T014 and T016 are written before their implementation and fail until it lands.
- **Shared files**: tasks on these files run one after the other.
  - `Episode.fs`: T008, T011, T013, T015 and T017.
  - `Program.fs`: T008, T011, T015 and T017.
  - `Anime.fs`: T005, then T009.

## Parallel Opportunities

- **Wave 1**: T002, T003 and T004.
- **Test files**: T007, T010, T012, T014 and T016 are separate files, so one agent can write them in parallel with implementation once T006 is done.
- **Polish**: T018 and T019.

## Implementation Strategy

- **MVP**: Phases 1–4 (US1 and US2), the page plus local comments. Stop and validate there.
- **Then**: US3 (shared dub threads), US4 (edit and delete), US5 (moderation). Each one is a reviewable diff with its own tests.
- **Before anyone outside the first users comments**: US5 must land, as moderation (spec, US5 "why").
