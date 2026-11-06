# Feature Specification: Following the same series on other Frunchy instances

**Feature Branch**: `028-instance-follows` (worked on `alta/balmy-cardamom-5md4`)

**Created**: 2026-10-29

**Status**: Implemented (threads); remote comments wait for Megáfono (see "Depends on Megáfono")

**Input**: the follow-up task "Let operators follow the same series on other Frunchy instances": Frunchy instances
share episode discussions. `@frieren@frunchy.lacalabaza.xyz` follows `@frieren@otherfrunchy.io`, and the other
instance's episode threads and comments show on our episode pages. Only an operator sets this up, only toward other
Frunchy instances. Nobody reads a feed or timeline for it.

## Depends on Megáfono

- **Threads work with Megáfono 0.2.0**: our series Group follows theirs (`POST /internal/actors/{id}/following`), their
  Group's thread posts reach us as `post.received` (its feature 005), with the `extensions` both instances publish
  with.
- **Comments need Megáfono's task "Group actors announce the replies they receive"** (not built as of 2026-10-29): a
  reply in their thread is addressed to their Group and the replier's followers, so it never reaches us today. This
  feature already stores and shows a `post.received` / `post.updated` / `post.deleted` whose `inReplyTo` is a
  remote thread we hold (or a comment under one), whoever its author is, since Megáfono only passes on posts from
  accounts we follow or (once built) announced by a Group we follow. If Megáfono ships the announce as a new event
  type, mapping that type onto the same code is a small follow-up.

## Clarifications

### Session 2026-10-29 (decided by the main session)

- Q: How is "the same series" found on another instance? → A: By id, never by title. Every Frunchy keys its series
  actor on the Crunchyroll series id: `https://{domain}/ap/actors/series-{seriesId}` (feature 015). So the series
  `GG5H5XQX4` on `otherfrunchy.io` is the actor `https://otherfrunchy.io/ap/actors/series-GG5H5XQX4`, followed by our
  `series-GG5H5XQX4`. A handle is only resolved to that URL.
- Q: What does the operator type? → A: One field, read by its shape like the block field:
  - a bare domain (`otherfrunchy.io`): follow **every series watched here** on that instance (an instance follow);
  - a handle (`@frieren@otherfrunchy.io`, `frieren@otherfrunchy.io`) or an `https://` actor URL: follow that one
    series. Megáfono resolves it (`GET /internal/remote-actors`); its actor id must be exactly
    `https://{host}/ap/actors/series-{ID}` with `ID` matching `^[A-Z0-9]{6,20}$`, and `ID` must be a series of our
    catalog.
- Q: "Every series of another instance"? → A: Frunchy has no way to list another instance's series, and following
  thousands of unknown actors is waste. An instance follow covers every series of **our** catalog with at least one
  watched episode of known length here (the series our people watch), the same actor URL on the other instance. A
  sweep adds series that start being watched later (at most once an hour, from the outbox worker) and retries series
  the other instance didn't know (`not_found`) after 7 days.
- Q: How do we know the target is Frunchy? → A: Its NodeInfo: `https://{host}/.well-known/nodeinfo`, the 2.x schema
  link on the same host, `software.name` equal to `frunchy` (case-insensitive). Frunchy sets this name through
  Megáfono (`Megafono__Software__Name=frunchy`) in the AppHost (run and publish) and `deploy/compose.yaml`. Anything
  else is refused before any follow. The fetch is the one HTTP call Frunchy makes to an operator-typed host, so it is
  guarded (FR-016).
- Q: "Ignore posts from anything else"? → A: A `post.received` is a thread only when its actor is the remote actor of
  one of our series follows; it is a comment only when it replies to a remote thread we hold (or to a comment under
  one). Everything else is acknowledged and ignored.
- Q: The ids on our threads? → A: `ensure_thread` publishes with Megáfono `extensions`: namespace
  `https://github.com/jjchiw/frunchy/ns#` (`Fediverse.ExtensionNamespace`, the same on every instance, an identifier
  only), prefix `frunchy`, data `{ "episode": { "seriesId", "episodeId", "groupKey" } }` (the Crunchyroll series id,
  the thread's episode id, our dub group key `{originalSeasonId}#{number}` or the episode id). Megáfono only passes
  `extensions` back once this instance has published with the namespace, and threads published before this feature
  carry none, so a thread without them falls back to its `object.url` when it is
  `https://{actor's host}/anime/{seriesId}/episode/{episodeId}` (ids again, from the same verified post).
- Q: Which local episode does a remote thread belong to? → A: Its `episodeId` when our catalog has that episode;
  otherwise an episode of ours with the `groupKey`'s number whose season is the key's season or a season matched as
  sharing it (`cr_season_matches.shared_from`). The page also finds threads by its group's episode ids and group
  key, so a thread stored before our catalog knew the episode shows once it does.
- Q: What does the episode page show? → A: A line per remote thread, "Also discussed on otherfrunchy.io", linking to
  the thread's page there (its `url`, https only, else its object id), and the remote comments in the page's own
  thread, text only (feature 015's `htmlToText`), with the author's name and `@user@host`, a "via otherfrunchy.io"
  mark, spoilers kept (`summary`/`sensitive` → the spoiler toggle). Operators hide them like any remote comment
  (hidden here only). Edits (`post.updated`) and deletions (`post.deleted`) follow.
- Q: Can our users reply into their thread? → A: No. Our users comment in our own thread, which their instance can
  follow the same way. Each instance moderates its own.
- Q: Through the outbox or synchronous? → A: Follow and unfollow calls go through the outbox (`follow_series`,
  `unfollow_series`), like every call for threads and accounts. The two reads the form needs to answer at once (the
  NodeInfo check, the remote-actor lookup) are synchronous, like the block calls.

## User Scenarios

### US1: An operator follows a series, or a whole instance (P1)

An operator opens `/review/follows`, types `otherfrunchy.io` and gets "Following every series watched here on
otherfrunchy.io"; the list shows each series follow and its state (queued, pending, accepted, rejected, removed, not
found, failed). They type `@frieren@otherfrunchy.io` and get one series follow. They remove either. A visitor, a
regular user and an operator on an instance with federation off get the plain 404.

**Acceptance**: typing a Mastodon domain answers "not a Frunchy instance" and writes nothing; typing our own domain is
refused; an unreachable host answers "unreachable"; a handle whose actor isn't a Frunchy series, or a series our
catalog doesn't have, is refused with its own message.

### US2: The other instance's threads show on our episode pages (P1)

Once accepted, each new episode thread `otherfrunchy.io` publishes for that series arrives; the episode page of the
same episode (or of its dub) says "Also discussed on otherfrunchy.io" with a link there.

### US3: The other instance's comments show on our episode pages (P2, needs Megáfono)

Comments in their thread show in ours, marked "via otherfrunchy.io", text only, spoilers kept; edits and deletions
follow; an operator can hide one.

## Requirements

- **FR-001**: Frunchy's Megáfono advertises `frunchy` in NodeInfo: `Megafono__Software__Name=frunchy` wherever the
  AppHost (run mode, the dev-fediverse mode, publish mode) or `deploy/compose.yaml` starts Megáfono.
- **FR-002**: `GET /review/follows`, `POST /review/follows/add`, `POST /review/follows/remove` exist only with
  federation on (off: absent, the plain 404). **Every handler is wrapped in `Layout.requireOperator`, outermost**,
  before any form is read; the POSTs use the `review-write` limiter and `WebAuth.readValidatedForm`.
- **FR-003**: Add (`target`), checked in this order, each failure a redirect to `/review/follows?msg=<key>` with nothing
  written:
  1. the shape (`follow-invalid`): a host name, a handle or an `https://` URL (`MegafonoClient.parseBlockTarget`'s
     rules);
  2. one series only: Megáfono's remote-actor lookup (`follow-not-found` 404, `follow-blocked` 409 `blocked`,
     `follow-unreachable` 502 or no answer, `follow-invalid` 400); the actor id shape and `type` `Group`
     (`follow-not-series`);
  3. the host (the domain typed, or the actor id's host) is not our `Frunchy:Federation:Domain` (`follow-self`);
  4. NodeInfo says `frunchy` (`follow-not-frunchy`; `follow-unreachable` when it can't be read);
  5. one series only: the series is in our catalog (`follow-unknown-series`);
  6. one transaction: the rows, their `follow_series` outbox items and the security event, then `follow-added`, or
     `follow-already` when nothing was new.
- **FR-004**: Remove (`domain`, optional `series`): without `series`, the instance follow and every series follow of
  that domain it made (`source = 'instance'`) go; with it, that one series follow goes, whatever made it. Each removed
  series follow queues `unfollow_series`. While the domain's instance follow stays, a removed series follow is kept as an
  `unfollowed` marker so the sweep doesn't follow it again (adding it by hand does). `follow-removed`, or
  `follow-not-followed`; bad input `follow-invalid`.
- **FR-005**: An explicit series follow of a series an instance follow already covers turns it into `source =
  'series'`, so removing the instance follow keeps it.
- **FR-006**: The page lists the instance follows (domain, since, series count) and the 500 newest series follows
  (series title linking to `/anime/{id}`, the remote handle when known else the actor URL, domain, how it was made,
  state, since, the last error code); hooks below. `/account` links to it for operators when federation is on.
- **FR-007**: `follow_series` (subject `{seriesId}@{domain}`): no row → done. Else make sure our series has a handle
  (`Fediverse.ensureSeriesHandle`), `POST /internal/actors/series-{id}/following {"target": "<actor URL>"}`; 2xx
  stores `followId` and the state from `status` (`accepted`, else `pending`); 404 → `not_found`; 409 or 400 → `failed`
  with the code (the item is refused); transient answers retry as the outbox does. `unfollow_series`: `DELETE
  /internal/actors/series-{id}/following?actor=<URL>`; 2xx and 404 are done.
- **FR-008**: `follow.accepted|rejected|removed` webhooks whose `follow.direction` is `outbound` and whose `user.id`
  is `series-{ID}` set the matching row's (`series_id = ID`, `remote_actor = actor.id`) state and `follow_id`. No
  row: ignored.
- **FR-009**: `ensure_thread` publishes with the `extensions` above.
- **FR-010**: `post.received` / `post.updated` from the remote actor of a series follow, with no `inReplyTo`, audience
  `public` or `unlisted`, and ids from `extensions` (namespace exactly ours, ids `^[A-Z0-9]{6,20}$`, group key that or
  `^[A-Z0-9]{6,20}#[0-9]{1,6}$`) else from the URL fallback, whose `seriesId` is the follow's series: upserted into
  `remote_threads` by object id (a copy older than the stored one changes nothing; a deleted thread stays deleted).
- **FR-011**: `post.received` / `post.updated` whose `inReplyTo` is the object id of a live remote thread, or of a
  comment stored under one: stored as a remote comment (`comments.remote_thread` = the thread's object id) on the
  thread's local episode, with feature 015's upsert rules (newer copy wins, tombstones stay); no local episode →
  `unknown_thread`, nothing stored. `post.deleted`: a remote thread gets `deleted_at`; a remote comment gets feature
  015's tombstone; anything else is ignored.
- **FR-012**: The episode page shows each live remote thread of its group (`data-remote-thread`) and marks comments
  that came through one (`data-comment-via`). Nothing else changes for local comments or feature 015's replies.
- **FR-013**: A sweep (`InstanceFollows.sweep`) adds the missing series follows of every instance follow and requeues
  instance-made `not_found` rows untouched for 7 days; it runs after an instance follow is added and from the outbox
  service at most once an hour.
- **FR-014**: Security events on the operator's account (`Actor.User`): `instance_follow_added|removed` (detail: the
  domain), `series_follow_added|removed` (detail: `{seriesId}@{domain}`), named in English and Spanish.
- **FR-015**: `fediverse-status` adds the series follow counts by state and the instance follows.
- **FR-016**: The NodeInfo fetch (`Infrastructure/NodeInfo.fs`, named client `nodeinfo`): https only, port 443, no
  redirects, 5 s overall, at most 256 KB per answer, the schema link only on the same host, and a connect callback that
  refuses loopback, private, link-local, CGNAT, multicast, unspecified and IPv6 unique-local addresses (IPv4-mapped
  too), unless `Frunchy:Federation:AllowPrivateAddresses` is `true`, which startup refuses in Production (naming the
  key). Nothing about the answer but the outcome is logged or stored.
- **FR-017**: Privacy: nothing about any person's watch history leaves the instance; the follows are series accounts
  following series accounts, and an instance follow reveals only which series are watched here to the instances an
  operator chose (stated in `docs/fediverse.md`).

## Hooks (tests read only these)

- `/review/follows`: `[data-follows-review]`, form `[data-follow-add]` (field `target`), notice
  `[data-follow-notice="<key>"]`, `li[data-instance-follow="<domain>"]` (`data-series-count`),
  `li[data-series-follow="<seriesId>@<domain>"]` (`data-follow-state`, `data-follow-source`), remove forms
  `[data-follow-remove]` (fields `domain`, `series`), empty states `[data-follows-empty]`.
- `/account`: `a[data-follows-link]`.
- Episode page: `li[data-remote-thread="<domain>"]` with an `a[href]` to the remote page; a comment that came through a
  remote thread has `data-comment-via="<domain>"`.

## Success Criteria

- An operator follows a whole instance in one step; non-Frunchy targets and their own instance are refused with
  nothing written.
- A thread published by the followed instance for an episode we have shows on that episode's page and its dub's,
  matched by ids only.
- With a payload shaped as Megáfono will send announced replies, comments show, edit, delete and hide as specified.
- Visitors and non-operators can't tell the page exists (plain 404 on every route and verb).
- The full suite stays green.
