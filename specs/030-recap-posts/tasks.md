# Tasks: Weekly recap posts on the fediverse

**Input**: [recap-posts.spec.md](recap-posts.spec.md)

**Tests**: `data-*` hooks only, `FakeMegafono` for every Megáfono call. The suite must stay green.

- [x] T001 Schema: `recap_post_settings` and `recap_posts`; `fediverse_outbox.kind` with `post_recap` and
  `delete_recaps` in the `CREATE TABLE` and as a guarded `upgrades` block after 028's (find the kind check lacking
  `post_recap`, drop it by its real name, add the ten-kind `fediverse_outbox_kind_check`). SCHEMA.md.
  `SchemaUpgradeTests`: an eight-kind outbox and a six-kind outbox both reach ten kinds with their rows kept; a second
  `initializeSchema` is a no-op. `SchemaBootstrapTests` table list and counts
  · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaUpgradeTests.fs,
  tests/FrunchyBackend.Tests/SchemaBootstrapTests.fs
- [x] T002 `Domains/Identity.fs`: `SecurityEventType.RecapPostsEnabled|RecapPostsDisabled`
  (`recap_posts_enabled|disabled`, in the list of all types); labels in `Texts` en and es
  · Domains/Identity.fs, Views/Texts.fs
- [x] T003 `Infrastructure/Recap.fs`: `loadBetween` (window form; `loadWeek` unchanged in behaviour), the week's days
  watched and most watched (up to 3) for the post
  · Infrastructure/Recap.fs
- [x] T004 `Infrastructure/RecapPosts.fs`: `isOn`, `setEnabled`, `queueRemoval`, `queueAfterImport` (FR-004);
  `Workers/ImportProcessor.fs` calls it in the existing savepoint after `WatchUpdates.queueAfterImport`
  · Infrastructure/RecapPosts.fs, FrunchyBackend.fsproj, Workers/ImportProcessor.fs
- [x] T005 `Workers/FediverseOutbox.fs`: pure `recapPostHtml`; `post_recap` (re-check, build at first attempt and
  freeze `content`/`url`, unlisted, key `recap:{userId}:{weekStart}`, url `…/u/{username}#recap-{weekStart}`, object
  stored whatever the state, `published` only from `pending`, empty week → `skipped`, permanent refusal → row
  `skipped`) and `delete_recaps`
  · Workers/FediverseOutbox.fs
- [x] T006 `POST /account/recap-posts` (`Handlers/Account/RecapPosts.fs`, `Program.federationEndpoints`), the
  `/account` section and its messages, `/account/visibility` removal. Texts en and es
  · Handlers/Account/RecapPosts.fs, Handlers/Account/AccountPage.fs, Handlers/Account/Visibility.fs, Views/Texts.fs,
  Program.fs, FrunchyBackend.fsproj
- [x] T007 Tests:
  - `RecapPostHtmlTests` (pure), as FR-006 lists;
  - `RecapPostsTests` (Postgres, federated app, `processAllFor`, `drainOutbox`): opted in 14 days ago + public + last
    week watched → one `POST /internal/actors/{userId}/objects` with key `recap:{userId}:{monday}`, `audience`
    unlisted, no `inReplyTo`, figures equal to `Recap.loadBetween` over the week, row `published`; a second import →
    no second post; opted in during last week → nothing; only this week's watching → nothing; only partial watches
    last week → nothing; not on → nothing; members (`""`) / private → nothing; queued then made members before the
    worker → row `skipped`, nothing published; a timed-out first attempt then a retry sends the same stored body;
    turning off after a post → `DELETE /internal/objects/{id}`, rows `deleted`, events once; Public→Members after a
    post → delete; turning watch updates off deletes no recap and turning recaps off deletes no watch update;
    `user-rebuild` → no new post; federation off → `POST /account/recap-posts` is the plain 404, no section, nothing
    queued; account page hooks, invalid value, Spanish
  · tests/FrunchyBackend.Tests/RecapPostHtmlTests.fs, tests/FrunchyBackend.Tests/RecapPostsTests.fs,
  tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T008 Docs: `docs/fediverse.md` "Weekly recap posts", CLAUDE.md line after "Watch updates",
  `changelog.d/030-recap-posts.md` (Added), full suite and `.github/scripts/changelog.sh check`

## Notes from implementation

- T004: `Infrastructure/RecapPosts.fs` sits next to `WatchUpdates.fs` in `FrunchyBackend.fsproj` (before `Recap.fs`), not
  after `Recap.fs`: it needs `Profiles`, `Fediverse` and `WatchUpdates.Policy` only, the import processor (later) is its
  caller, and the worker (which uses `Recap`) compiles after both.
- T003/T005: `RecapPost` is `{ Week: Recap.Week; DaysWatched: int; MostWatched: Recap.MostWatched list }` (the figures are
  `Recap.Week` as `loadBetween` returns it, so the post and the dashboard share the type). `Recap` gained
  `loadDaysWatched` and `loadMostWatched` beside `loadBetween`; `loadWeek` now calls `loadBetween ... None`.
- T004: `queueAfterImport` takes the existing `WatchUpdates.Policy` (the same instance default), so `ImportProcessor`'s
  signature and `TestHelpers.processAllFor` are unchanged; the savepoint's log message now names both ("Watch update or
  recap post for import ... failed").
- T005: the worker's re-check also requires `enabled_at <= covered_from` (FR-004's opt-in rule) before every send. The
  Spanish values of the post's words exist but, as for 028, the post is always written in English.
- T001/T007: the eight-kind and six-kind upgrade tests share one helper; the existing 028 upgrade tests still pass because
  the 030 block runs after 028's in the same `upgrades` string.
- `SchemaBootstrapTests`: the fediverse table list gained `recap_post_settings` and `recap_posts`; the column count went
  from 55 to 67 (2 + 10 added).
