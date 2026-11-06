# Tasks: Following the same series on other Frunchy instances

**Input**: [instance-follows.spec.md](instance-follows.spec.md)

**Tests**: `data-*` hooks only, `FakeMegafono` and a fake NodeInfo handler (no real network). The suite must stay green.

## Data model

New tables (in `initializeSchema`'s `CREATE TABLE` list, after the feature 015 tables, and in `SCHEMA.md`):

```sql
-- Instance follows (feature 028): every series watched here, followed on another Frunchy instance
CREATE TABLE IF NOT EXISTS instance_follows (
    domain     text PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Our series account following the same series' account on another Frunchy instance
CREATE TABLE IF NOT EXISTS series_follows (
    series_id    text NOT NULL,
    domain       text NOT NULL,
    remote_actor text NOT NULL UNIQUE,            -- https://{domain}/ap/actors/series-{series_id}
    source       text NOT NULL CHECK (source IN ('series', 'instance')),
    state        text NOT NULL DEFAULT 'queued'
                 CHECK (state IN ('queued', 'pending', 'accepted', 'rejected', 'removed', 'not_found', 'failed')),
    follow_id    text NULL,
    last_error   text NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (series_id, domain)
);

-- Episode threads published by a followed series account on another Frunchy instance
CREATE TABLE IF NOT EXISTS remote_threads (
    object_id         text PRIMARY KEY,
    domain            text NOT NULL,
    remote_actor      text NOT NULL,
    series_id         text NOT NULL,
    remote_episode_id text NOT NULL,
    remote_group_key  text NULL,
    cr_episode_id     text NULL REFERENCES cr_episodes(id),
    url               text NULL,
    published_at      timestamptz NOT NULL,
    updated_at        timestamptz NULL,
    deleted_at        timestamptz NULL,
    received_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_remote_threads_episode ON remote_threads (cr_episode_id);
CREATE INDEX IF NOT EXISTS ix_remote_threads_remote_episode ON remote_threads (remote_episode_id);
CREATE INDEX IF NOT EXISTS ix_remote_threads_group ON remote_threads (remote_group_key);
```

Changes to existing tables (deployed databases have them):

- `comments.remote_thread text NULL` (the remote thread's object id a comment came through): in the `CREATE TABLE`,
  and `ALTER TABLE comments ADD COLUMN IF NOT EXISTS remote_thread text NULL` in the `upgrades` string.
- `fediverse_outbox.kind` accepts `follow_series` and `unfollow_series`: in the `CREATE TABLE`, and a guarded block in
  `upgrades` that replaces the kind check only when it doesn't allow them yet (read `pg_constraint` like the existing
  `fediverse_outbox_state_check` block does; a second run is a no-op).
- `SchemaUpgradeTests`: build the previous shape (old kind check, `comments` without the column), run
  `initializeSchema` twice, assert the new kinds insert and the column exists.

## Tasks

- [x] T001 Settings and NodeInfo name:
  - `Infrastructure/Federation.fs`: `Frunchy:Federation:AllowPrivateAddresses` (bool, default false), read into the
    federation values; `true` in the Production environment refuses startup naming the key (follow how the module
    reports a missing key; never print a value). Tests in `FederationSettingsTests`.
  - AppHost: `.WithEnvironment("Megafono__Software__Name", "frunchy")` on every Megáfono resource (run mode, the
    dev-fediverse mode, publish mode) and, in the dev-fediverse mode only, `Frunchy__Federation__AllowPrivateAddresses`
    `true` on Frunchy; `deploy/compose.yaml`: `Megafono__Software__Name: frunchy`. Add an assertion to
    `tests/Frunchy.AppHost.Tests` that the publish graph's Megáfono has it.
  · Infrastructure/Federation.fs, AppHost/*.fs, deploy/compose.yaml, tests
  Note: `Federation.read` doesn't know the environment, so the Production refusal is `Federation.refuseInProduction`,
  called from `Program.buildApp` beside `DataProtectionStore.register`. The AppHost has two graphs (run, publish): the
  run graph is the "dev-fediverse mode" (it already sets `Megafono__Federation__AllowPrivateAddresses`), so it gets
  `Frunchy__Federation__AllowPrivateAddresses`; the publish graph gets only the software name.

- [x] T002 `Infrastructure/NodeInfo.fs` (after `MegafonoClient.fs` in the fsproj):
  - `ClientName = "nodeinfo"`; `register` (only with federation on, from where `MegafonoClient.register` is called):
    `AddHttpClient` with an explicit `Action<HttpClient>` (5 s timeout, `MaxResponseContentBufferSize` 262144,
    a `User-Agent` naming Frunchy), and `ConfigurePrimaryHttpMessageHandler` returning a `SocketsHttpHandler` with
    `AllowAutoRedirect = false` and a `ConnectCallback` that resolves the host, refuses any address for which the pure
    `isPublicAddress` is false (unless `AllowPrivateAddresses`), and connects to the first allowed one.
  - pure `isPublicAddress (ip: IPAddress)`: false for loopback, 0.0.0.0/8, 10/8, 100.64/10, 127/8, 169.254/16,
    172.16/12, 192.168/16, 224/4 and above, `::`, `::1`, fc00::/7, fe80::/10, ff00::/8, and IPv4-mapped forms of
    those.
  - `check (client: HttpClient) (host: string) : Task<NodeInfoResult>` with `NodeInfoResult = IsFrunchy | NotFrunchy
    | Unreachable`: GET `https://{host}/.well-known/nodeinfo`; a JSON `links` entry whose `rel` starts with
    `http://nodeinfo.diaspora.software/ns/schema/2.` and whose `href` is `https` on the same host (else `NotFrunchy`);
    GET it; `software.name` equal to `frunchy` ignoring case → `IsFrunchy`; any other name or shape → `NotFrunchy`;
    no answer, timeout, non-2xx or a refused connection → `Unreachable`. Bodies larger than the cap → `Unreachable`.
    Never logs a body.
  - Tests: a `FakeNodeInfo` handler in `tests/.../Infrastructure/` (route by host: frunchy, mastodon, unreachable,
    off-host link, oversize) swapped in like `FakeMal`/`FakeMegafono` (`useHandler`), and `NodeInfoTests` for `check`
    and `isPublicAddress`.
  · Infrastructure/NodeInfo.fs, Program.fs, FrunchyBackend.fsproj, tests

- [x] T003 Schema (the data model above) and `SCHEMA.md` · Infrastructure/Database.fs, SCHEMA.md, SchemaUpgradeTests

- [x] T004 `MegafonoClient`: `lookupRemoteActor client (target: BlockTarget)` (`GET /internal/remote-actors?handle=` or
  `?url=`, page timeout) returning the result plus a parsed `{ id; handle; type' }` option; `follow client actorId
  targetUrl` (`POST /internal/actors/{id}/following`, body `{target}`), with a parser for `{followId, status}`;
  `unfollow client actorId remoteActorUrl` (`DELETE .../following?actor=`). `FakeMegafono` answers them by default:
  remote-actors 200 `{id: https://{host}/ap/actors/series-{X}, handle, type: "Group"}` derived from the request (or a
  scripted route), following 202 `{followId: "f-n", status: "pending", actor}`, unfollow 202.
  · Infrastructure/MegafonoClient.fs, tests/.../Infrastructure/FakeMegafono.fs

- [x] T005 `Infrastructure/InstanceFollows.fs` (after `Fediverse.fs`, `NodeInfo.fs`): the shared data access:
  - `remoteSeriesActor domain seriesId` (= `Fediverse.actorUrl domain $"series-{seriesId}"`) and the pure
    `seriesIdOfActor (actorId: string) : (string * string) option` (host, series id) for
    `https://{host}/ap/actors/series-{ID}` exactly (no query, fragment, port or extra path; `ID` `^[A-Z0-9]{6,20}$`);
  - `addInstance connection tx domain` (insert `instance_follows`, then the sweep for that domain), `addSeries
    connection tx seriesId domain` (insert or turn `instance` into `series`), `removeInstance`, `removeSeries`: each
    writes the outbox items (`Fediverse.enqueue … "follow_series"|"unfollow_series" $"{seriesId}@{domain}"`) in the
    same transaction and returns whether anything changed;
  - `sweep connection tx (domain: string option) (now)`: for every instance follow (or the one domain), insert the
    missing `series_follows` rows (`source = 'instance'`) for every series with at least one watch event on an episode
    of known length (`cr_episodes.duration_ms IS NOT NULL`), plus requeue instance rows in `not_found` with
    `updated_at` older than 7 days (state back to `queued`); enqueue `follow_series` for each;
  - `list connection` for the page (instance follows with series counts; the 500 newest series follows joined to
    `cr_series` for the title and `remote_actors` for the handle).
  - `localEpisodeOf connection tx (remoteEpisodeId) (remoteGroupKey option) : Task<string option>` (spec, "Which local
    episode").
  Unit tests for the pure parts in `FediverseRulesTests` or a new `InstanceFollowRulesTests`.
  Deviations: `addInstance`, `addSeries` and `sweep` also take `now` (the sweep's 7-day rule needs it); `localEpisodeOf`
  also takes the follow's `seriesId` and only maps within that series; `addSeries` also re-queues a row that ended in
  `not_found|failed|rejected|removed`, so an operator typing it again retries it; the sweep's inserts and outbox items
  are set-based CTEs (same transaction). A series follow removed by hand while its instance follow stays comes back with
  the next sweep (the spec's "whatever made it").
  · Infrastructure/InstanceFollows.fs, FrunchyBackend.fsproj

- [x] T006 Outbox: `follow_series`, `unfollow_series` (spec FR-007; update the row's state, `follow_id`, `last_error`,
  `updated_at`), `ensure_thread` sends the `extensions` (FR-009; add an optional `Extensions` field to
  `NoteToPublish`, serialized as `extensions: null` when absent so existing publishes don't change). The outbox
  service runs `InstanceFollows.sweep` at most once an hour (keep the time of the last run in the service; expose a
  function the tests call). `Fediverse.ExtensionNamespace`, `ExtensionPrefix = "frunchy"`.
  Tests in `FediverseOutboxTests` (or a new `InstanceFollowOutboxTests`): the follow request's path and body, states
  for 202 pending/accepted, 404, 409 blocked, 500 retry; unfollow; the thread publish carries the extensions.
  · Workers/FediverseOutbox.fs, Infrastructure/Fediverse.fs, Infrastructure/MegafonoClient.fs

- [x] T007 Webhooks (FR-008, FR-010, FR-011): `follow.accepted|rejected|removed`; `post.received`, `post.updated`,
  `post.deleted` as the spec says. Keep the existing order-independence style (each event in its transaction, outcome
  recorded in `megafono_events`). Validate every field (lengths, https only for `url`, ids by pattern) before use.
  Remote comments reuse `upsertComment` / `tombstone` with `remote_thread` set; the thread's local episode comes from
  `remote_threads.cr_episode_id`, else `InstanceFollows.localEpisodeOf` (store it on the thread when found).
  Tests in a new `InstanceFollowWebhooksTests` (signed bodies like `MegafonoWebhooksTests`): follow state changes; a
  thread with extensions maps to the episode, to a dub's original by group key, by the URL fallback; ignored when the
  actor isn't followed, when the series id differs from the follow's, when it has an `inReplyTo`, when the namespace
  is another and the URL is on another host; a comment replying to the thread and one replying to that comment are
  stored with `remote_thread`; `post.updated` edits (a stale copy doesn't); `post.deleted` tombstones a comment and
  marks a thread deleted; a comment on a thread without a local episode is `unknown_thread`.
  · Handlers/Megafono/Webhooks.fs
  Note: extensions under our namespace decide the ids; a bad or foreign one is no thread (the URL fallback only applies
  when the post carries no extensions of ours). `follow.*` events also keep the remote actor's handle in `remote_actors`
  (the page shows it). `post.deleted` only tombstones comments that came through a remote thread (`remote_thread` set).

- [x] T008 The page `Handlers/Review/InstanceFollows.fs` (after `FediverseBlocks.fs`; template: `FediverseBlocks.fs`
  and `Invites.fs`): FR-002 to FR-006, FR-014; notices for every key in FR-003/FR-004; registered in `Program`
  next to the block routes (federation on only); `/account` link `data-follows-link` for operators with federation on;
  every text in `Views/Texts.fs` (en and es), the security event names too. States shown in words.
  Tests `InstanceFollowsPageTests`: visitor, signed-in non-operator and (federation off) operator get the plain 404 on
  GET and both POSTs; the root and a table operator can use it; instance add (Frunchy NodeInfo) creates the instance
  row, a series row for each watched series and the outbox items and one `instance_follow_added` event; a Mastodon
  host → `follow-not-frunchy` and nothing written; unreachable; own domain → `follow-self`; a handle → one series row
  (`series_follow_added`); handle resolving to a non-series actor → `follow-not-series`; unknown local series →
  `follow-unknown-series`; repeat → `follow-already`; FR-005; remove instance keeps explicit rows; remove series; the
  listing hooks and states after a `follow.accepted` webhook; Spanish page.
  Note: the test hosts register their fake NodeInfo handler before the app registers the real one, so `FakeNodeInfo`
  installs itself as a post-configuration of the client's options (it runs last and wins).
  · Handlers/Review/InstanceFollows.fs, Handlers/Account/*.fs, Views/Texts.fs, Program.fs, FrunchyBackend.fsproj

- [x] T009 Episode page (FR-012): load the live remote threads of the page's group (`cr_episode_id = ANY(@group) OR
  remote_episode_id = ANY(@group) OR remote_group_key = @groupKey`, deleted excluded, domain order) and the comments'
  `remote_thread` (its domain = the remote thread's `domain`); render `li[data-remote-thread]` ("Also discussed on
  {domain}", link with `rel="nofollow noopener noreferrer"`, only an https `url`, else the object id when https) and
  the "via {domain}" mark with `data-comment-via`. Only with federation on. Texts en/es. Tests in
  `InstanceFollowWebhooksTests` or a page test: the link shows on the episode and its dub; a deleted thread doesn't
  show; the via mark; an operator can hide such a comment and it's a placeholder for others.
  · Handlers/Episodes/Episode.fs, Views/Texts.fs

- [x] T010 `fediverse-status` (FR-015) and its test · Operations/FediverseStatus.fs, FediverseOperationsTests

- [x] T011 Docs: `docs/fediverse.md` (a "Following other Frunchy instances (feature 028)" section: what it does, the
  Megáfono dependency for comments, privacy FR-017, the NodeInfo name), `specs/015-fediverse/contracts/frunchy-megafono.md`
  (the new calls and events), the CLAUDE.md routes line, `changelog.d/028-instance-follows.md` (Added; Upgrade: the
  NodeInfo name is set by the AppHost and compose; a self-hoster with their own compose sets
  `Megafono__Software__Name=frunchy`). `SqlGuardrailTests` must pass. Full suite.

## Review (main session, 2026-10-31)

- [x] T012 Fixes after review:
  - removing one series follow while the domain's instance follow stays kept getting undone by the hourly sweep: the
    row now stays as an `unfollowed` marker (`source = 'series'`), which the sweep, the follow worker and the
    `follow.*`/`post.*` webhooks leave alone; adding it by hand follows it again; removing the instance follow clears
    the markers without a call. New state in the `series_follows` check (a new table, so no upgrade block), Texts en/es,
    `SCHEMA.md`, a rules test;
  - the episode page shows a remote thread only for the page's series, and its link only when it is https on the
    thread's own instance (a followed instance can't point readers at another host);
  - the NodeInfo guard also refuses 192.0.0.0/24, 198.18.0.0/15 and the IPv6 forms that carry a refused IPv4 address
    (6to4 `2002::/16`, NAT64 `64:ff9b::/96`); `NodeInfoTests` cases.
  · Infrastructure/InstanceFollows.fs, Infrastructure/Database.fs, Infrastructure/NodeInfo.fs, Handlers/Episodes/Episode.fs,
    Handlers/Megafono/Webhooks.fs, Workers/FediverseOutbox.fs, Views/Texts.fs, SCHEMA.md, tests
