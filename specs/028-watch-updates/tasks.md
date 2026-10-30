# Tasks: Watch updates on the fediverse

**Input**: [watch-updates.spec.md](watch-updates.spec.md)

**Tests**: `data-*` hooks only, `FakeMegafono` for every Megáfono call. The suite must stay green.

- [x] T001 Schema: `watch_update_settings` and `watch_updates` tables; `fediverse_outbox.kind` check with
  `post_watch_update` and `delete_watch_updates` in the `CREATE TABLE` and as a guarded `upgrades` block (find the check
  that names `kind` and lacks `post_watch_update`, drop it by its real name, add `fediverse_outbox_kind_check`; a second
  run does nothing). SCHEMA.md. `SchemaUpgradeTests`: an older outbox with the six-kind check is upgraded, its rows kept,
  and running `initializeSchema` twice is a no-op
  · Infrastructure/Database.fs, SCHEMA.md, tests/FrunchyBackend.Tests/SchemaUpgradeTests.fs
- [x] T002 `Domains/Identity.fs`: `SecurityEventType.WatchUpdatesEnabled|WatchUpdatesDisabled`
  (`watch_updates_enabled|disabled`, in the list of all types); labels in `Texts` en and es
  · Domains/Identity.fs, Views/Texts.fs
- [x] T003 `Infrastructure/WatchUpdates.fs` (after `Profiles.fs` and `Fediverse.fs` in the fsproj):
  - `recentWindow = TimeSpan.FromHours 48.0`; `type Policy = { DefaultVisibility: Profiles.Visibility }`;
    `policyOf (federation: Federation.FederationSettings) (profiles: Profiles.ProfileSettings) : Policy option`
    (Some only when federation is on);
  - `isOn connection userId` (for the account page);
  - `queueAfterImport connection tx (policy: Policy) (userId: Guid)`: one statement checks opt-in, effective
    visibility Public (`coalesce(profile_settings.visibility, @default) = 'public'`), and computes the window
    (`greatest(now() - @window, enabled_at, max previous covered_until)`), then inserts the row only when a qualifying
    watch event exists (`INSERT … SELECT … WHERE EXISTS … ON CONFLICT (user_id, day) DO NOTHING RETURNING`); the
    username check (`Fediverse.isValidHandle`) in F#; when inserted, `Fediverse.enqueue … "post_watch_update"`;
  - `setEnabled connection tx userId (on: bool) : Task<bool>` (true when the state changed);
  - `queueRemoval connection tx userId`: pending rows → `skipped`; when any row is `pending` or `published` (read
    before the update), enqueue `delete_watch_updates`
  · Infrastructure/WatchUpdates.fs, FrunchyBackend.fsproj
- [x] T004 `Workers/ImportProcessor.fs`: `processNextWith (db) (watchUpdates: WatchUpdates.Policy option)` calls
  `queueAfterImport` in the import's transaction after the completed update (only with `Some`); `processNext db =
  processNextWith db None`; *(review: `processNextWith` takes a logger first and runs the hook behind a savepoint, so a watch-update failure is logged and never fails the import)*; `ImportProcessorService` takes `FederationSettings` and `ProfileSettings` from DI and passes
  `WatchUpdates.policyOf`. Test helper `processAllFor (app: TestApp)` that uses the app's settings
  · Workers/ImportProcessor.fs, Program.fs (if needed), tests/FrunchyBackend.Tests/TestHelpers.fs
- [x] T005 `Workers/FediverseOutbox.fs`:
  - public pure `watchUpdateHtml (domain: string) (series: WatchedSeries list) : string` (records with series id,
    title, episode count, and the numbered episodes `(number, episodeId)`), `Texts.en` words, the caps of the spec,
    everything escaped;
  - `post_watch_update` and `delete_watch_updates` as in the spec (re-check; build from `watch_events` at send time;
    `audience = "unlisted"`; key `watch:{userId}:{yyyy-MM-dd}`; url `https://{domain}/u/{username}#watched-{day}`;
    store object id/url whatever the state, `published` only from `pending`);
  - `processNextWith`/`processNext` take the instance default visibility (`Profiles.Visibility`) after `values`;
    `FediverseOutboxService` gets `Profiles.ProfileSettings` from DI; `drainOutboxAt` passes the app's
  · Workers/FediverseOutbox.fs, tests/FrunchyBackend.Tests/TestHelpers.fs
- [x] T006 `POST /account/watch-updates` (`Handlers/Account/WatchUpdates.fs`, in `Program.federationEndpoints`) and the
  `/account` section with its messages; `/account/visibility` calls `queueRemoval` when the effective level leaves
  Public (federated). Texts en and es
  · Handlers/Account/WatchUpdates.fs, Handlers/Account/AccountPage.fs, Handlers/Account/Visibility.fs, Views/Texts.fs,
  Program.fs, FrunchyBackend.fsproj
- [x] T007 Tests:
  - `WatchUpdateHtmlTests` (pure): one series, several (joined with commas and "and"), plural/singular, more than 5
    series ("and N more series"), more than 6 numbers ("…"), an unnumbered episode counted not listed, a title with
    `<script>` and `&` escaped, links absolute, no episode title;
  - `WatchUpdatesTests` (Postgres, `newFederatedApp`/`createFederatedAppWith`, `processAllFor`, `drainOutbox`):
    public + on + recent watching → exactly one `POST /internal/actors/{userId}/objects` with key
    `watch:{userId}:{day}`, `audience` unlisted, no `inReplyTo`, series and episode links, row `published`;
    a 300-episode recent sync → one post; a second import the same day → no second post; old history (> 48 h) →
    nothing; episodes watched before turning on → not in the post; not on → nothing; members/private (pass `""` or
    `private` as `Frunchy:Profiles:DefaultVisibility`) → nothing; a post queued then the profile made members before
    the worker runs → nothing published and the row `skipped`; turning off after a post → `DELETE
    /internal/objects/{id}`, rows `deleted`, events recorded (once, not on a repeat); visibility Public→Members after a
    post → delete; `user-rebuild` (reset to pending + reprocess) → no new post; a dub and its original count once;
    federation off → `POST /account/watch-updates` is the plain 404, no section, nothing queued;
    account page hooks (`data-watch-updates-state`, `data-watch-updates-not-public`), invalid value, Spanish
  · tests/FrunchyBackend.Tests/WatchUpdateHtmlTests.fs, tests/FrunchyBackend.Tests/WatchUpdatesTests.fs,
  tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj
- [x] T008 Docs: `docs/fediverse.md` "Watch updates" section (and the order list), CLAUDE.md federation line,
  `changelog.d/028-watch-updates.md` (Added; Upgrade not needed: the schema upgrade runs at start), full suite and
  `.github/scripts/changelog.sh check`

## Notes from implementation

- T006: the section hook is `data-account="watch-updates"` (what the `accountSection` helper emits for every section,
  e.g. `data-account="blocks"`), not `data-account-section`; the id is `watch-updates` as specified.
- T001/FR-006 (added during implementation): `watch_updates` also has `content text NULL` and `url text NULL`, the
  request frozen at the first attempt; `SCHEMA.md` documents them.
- T005: `post_watch_update` processes every pending row of the user in one item (oldest first); a permanent refusal
  of a row sets that row `skipped`.
