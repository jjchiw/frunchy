# Frunchy on the fediverse: episode comments and following

Status: design note, 2026-10-10, updated 2026-10-18. Of this, only the pieces named below as done are built (feature 009's local comments, feature 013's series page and username rule, feature 014's platform, the first half of feature 015's integration). Each step below becomes its own spec-kit feature.

Frunchy joins the fediverse through **Megáfono** (`../../Megafono`), a self-hosted ActivityPub sidecar.
Frunchy keeps its users, data and pages, and contains no ActivityPub code. Megáfono owns the protocol:
WebFinger, actors, keys, signatures, followers, delivery and inbound checks. Frunchy talks to it through
its internal API and its webhooks. Sign-in stays Frunchy's job, through Umbral and the other providers.
Megáfono is built by its own Claude session, so changes to it go through that session, never through
edits from here.

## The idea: a comment is a reply

On the fediverse, a comment is a reply to a post. So an episode becomes a post that people can reply to.

- **Series accounts.** Each series is a Group account, e.g. `@frieren@frunchy.example`, the way Lemmy
  does communities. Megáfono's actors can be Group or Service as well as Person (its vision, FR-003).
- **Episode threads.** For each episode, the series account publishes one public post, "Frieren S2 E5:
  Title", whose `url` is Frunchy's episode page.
- **Comments from the fediverse.** A Mastodon user replies to that post. Megáfono verifies the reply and
  sends Frunchy a `reply.received` webhook, and Frunchy stores the reply and shows it on the episode page.
- **Comments from Frunchy.** A Frunchy user writes a comment on the episode page. Frunchy stores it and
  publishes it through Megáfono as a reply to the thread, from that user's account, so the user's
  followers see it in the thread.

People on the fediverse reach a thread in two ways:

- **Following a series.** Each new episode thread arrives in their timeline, and they can reply there.
- **Pasting the episode page's URL into Mastodon's search.** Mastodon fetches the page and follows
  `<link rel="alternate" type="application/activity+json" href="<thread id>">` in its HTML, the same way
  Megáfono's pasted-profile discovery works.

Every Frunchy user is also a Person account (`@username@frunchy.example`), keyed on the immutable
`users.id`, never the username, so a rename keeps the account. People on the fediverse can follow them.

## What each side needs

**Megáfono** (its feature 001 leaves replies, likes and mentions as later features, vision US4):

1. **Inbound replies.** A `Create` of a Note whose `inReplyTo` is an object Megáfono published becomes
   a `reply.received` webhook, with the verified actor and the sanitized object. A `Delete` of it becomes
   `object.deleted`.
2. **Outbound replies.** Publishing with `inReplyTo` set to an object Megáfono published, addressed so
   that the thread owner and the author's followers receive it.
3. **A `replies` collection on published objects**, so Mastodon can load a thread's full set of replies,
   not only those from people the viewer follows.
4. **Non-user actors** (Group) answered by the app's actor lookup, with the same immutable-id rule.
5. **Content warnings preserved** both ways: `summary` and `sensitive` on outbound posts, passed through
   on inbound webhooks.
6. **Later, for following:** inbound `Create` from accounts a Frunchy user follows, as a webhook, so
   Frunchy can build a feed.

**Frunchy:**

1. **A public episode page**, `/anime/{seriesId}/episode/{episodeId}`, per catalog episode rather than
   per user. Every page today belongs to one user. Each watched-episode row on the anime page links to it.
2. **A `comments` table** holding local comments and remote ones from webhooks, with the author (a local
   user or a remote actor), the content, the spoiler flag, and the fediverse object id once published.
3. **Moderation by operators** (features 007 and 008): hide a comment, block a remote server.
4. **The Megáfono integration:** the actor lookup (`/megafono/actors`), the webhook receiver
   (`/megafono/webhooks`, signature checked and events deduplicated by id), and publishing.

## Megáfono 002 contract (agreed 2026-10-10)

The Megáfono session drafted the shapes on its branch `002-replies-threads`, in
`specs/002-replies-threads/contracts/` (app-contract.md, internal-api.md and federation.md).

**Publishing a comment** uses the usual objects call, with `Idempotency-Key: comment:{id}`:

```
POST /internal/actors/{userId}/objects
{ "audience": "public",
  "object": { "type": "Note", "content": "<p>…</p>",
              "url": ".../anime/{s}/episode/{e}#comment-{id}",
              "inReplyTo": "<thread objectUrl, or a received reply's object.id>",
              "summary": "Spoilers: S2 E5", "sensitive": true } }
```

- Replies are `public` or `unlisted`.
- `inReplyTo` must be one of our objects or a reply Megáfono recorded. Anything else gets 400, and a
  deleted parent gets 410.
- The 201 response adds `thread: {objectId, id}`.

**Thread posts** use the same call, from the series Group actor, with `Idempotency-Key:
thread:{seriesId}:{episodeKey}`. The returned `objectUrl` goes in the episode page as `<link
rel="alternate" type="application/activity+json">` and in a matching `Link` header. Mastodon follows
it; GoToSocial doesn't, so its users paste the post id or the series handle.

**Webhooks** all go to the thread root's author, the series Group:

- `reply.received`: `object` = id, url, content (sanitized), summary, sensitive, language, published,
  updated, audience, inReplyTo, `parent {local, objectId?, userId?}`, `thread {objectId, id}` and
  attachment.
- `reply.updated`: the same payload, with `updated` set.
- `object.deleted`: `{object: {id, thread}, actor}`.

**Frunchy's side:**

- Public pages show only public and unlisted replies. Followers-only and direct replies are stored and
  not shown.
- The `replies` collection holds only replies Megáfono has seen. A reply to a remote reply that doesn't
  reach our inbox never arrives.
- **A remote reply must reach the series account to show up.** In practice it has to keep the
  `@series` mention, which clients prefill. GoToSocial sends nothing to Megáfono when the user deletes
  the mention (tested on 0.22.2); Mastodon wasn't tested without it. The episode page and the help
  text should say this.

**Settled with Megáfono (its commit 96f9008):**

- **Edit and delete.** Comment edits use `PUT /internal/objects/{objectId}`, which sends Update(Note) to
  the reply's recipients. `inReplyTo`, type and audience can't change. User deletes and operator
  removals use `DELETE /internal/objects/{objectId}`, which sends a Tombstone and takes the comment out
  of the replies collection. Copies already on other servers may stay.
- **Ids.** Ids match `^[A-Za-z0-9_-]{1,64}$` and are never reused. Series use `series-{crSeriesId}`.
  A Group lookup sends `type: "Group"`, `locked: false`, and the series page as `profileUrl`, which is
  `/anime/{seriesId}` (built in feature 013, 2026-10-17).
- **Handles.** Megáfono allows lowercase letters, digits and underscore, up to 30 characters, so
  series need a short handle, e.g. `frieren`, decided in Frunchy's spec and kept apart from usernames.
  Megáfono's handle fix (its commit 980a3cb, on its main since 2026-10-15 with 001 and 002, merge
  43fc4c5) allows `-` inside a handle but not at either end,
  because Mastodon refuses a remote username ending in `-`. **Done for new names** (feature 013,
  2026-10-17): sign-up and `user-create` use `Username.tryCreateNew`, `^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$`,
  so the last character is a letter, digit or `_`. Existing names keep working (lookups use the old rule,
  `^[a-z0-9][a-z0-9_-]{2,29}$`) and none with a trailing dash exist locally (checked 2026-10-10). If one
  ever does, it is asked to rename: its actor id is `users.id`, so a rename keeps the identity.
- **Blocks.** Built in Megáfono 004 (below). Frunchy doesn't announce series accounts widely until it
  is wired in, and until then hiding a comment hides it only on Frunchy.
- **Megáfono's next features** (as of 2026-10-15), both before series accounts are announced: actor
  deletion, then per-actor and per-instance blocks. Both are now built (below).

**Megáfono 002 is implemented** (2026-10-10) and on Megáfono's main since 2026-10-15 (merge 43fc4c5).
It was verified against GoToSocial 0.22.2 and Mastodon 4.7.3. What a Frunchy integration should know:

- Series Groups show on Mastodon as a group, not a bot, and follows are accepted at once.
- A Mastodon reader who follows nobody sees our own replies at once, and replies from other servers
  after Mastodon's crawl, about 5 minutes.
- Pasting the episode URL finds the post on Mastodon. GoToSocial users paste the post's own URL.
- On GoToSocial, a reply in a series thread shows in the thread view, not in followers' home timelines.
- **References:** the contracts are in `specs/002-replies-threads/contracts/` and the "Replies and
  threads" section of `docs/operating.md`, in the Megáfono repo. `samples/SampleApp/Megafono.fs` shows
  the basic calls in about 70 lines.

**Megáfono 003 and 004 are implemented** and on Megáfono's main (ba58955) as of 2026-10-17.

- **Actor deletion (003).** `DELETE /internal/actors/{id}` answers 202, and repeating it answers the
  same. A 404 means it is done, and the actor then answers 410 gone and is never revived. Frunchy's
  `/account/delete` will call it.
- **Blocks (004).**
  - A user manages their own with `POST|GET|DELETE /internal/actors/{id}/blocks`, taking `{actor}` or
    `{domain}`. An operator blocks an instance with `/internal/blocks`.
  - A new block answers 201 and a repeat 200.
  - Following and remote-actor lookups answer 409 `blocked` for a blocked actor or domain.
  - `follow.removed` now carries `follow.direction`, including `outbound`.
- **Not built yet:** the feed of posts from followed accounts (Megáfono item 6).
- **Decided (feature 014):** how Frunchy runs Megáfono. Locally, an F# Aspire AppHost; self-hosted, a
  compose file with an optional `federation` profile. See "The platform" below.

## The platform (feature 014, done)

The ground for the integration is in place, with no federation code yet:

- **Topology.** The AppHost (`AppHost/`, `dotnet run --project AppHost`) starts one Postgres with two
  databases (`frunchydb` and `megafono`), Megáfono, its gateway and Frunchy. The compose file
  (`deploy/compose.yaml`, profile `federation`) is the same shape for self-hosters.
- **One domain through the gateway.** The gateway is the only public entrance. Megáfono's routes
  (`/.well-known/webfinger`, `/.well-known/nodeinfo`, `/nodeinfo/*`, `/ap/*`, `/inbox`) go to Megáfono and
  everything else to Frunchy, with the original `Host` preserved. Megáfono's internal API (port 8081) is
  reachable by Frunchy only: never external, never a gateway destination, never published.
- **Actor ids** are `https://{domain}/ap/actors/{users.id}`, keyed on the immutable `users.id`. The domain
  is permanent once federating: changing it orphans every follower.
- **Settings.** `Frunchy:Federation:Enabled` is off by default; when on, `Frunchy:Federation:Domain` and
  `Frunchy:Megafono:Url|Token|WebhookSecret` are required (startup names the missing key, never a value),
  and the `FederationSettings` singleton is what the integration reads. `Frunchy:TrustedProxies` makes
  Frunchy believe the gateway's `X-Forwarded-*` headers, from nobody else.
- **Also in 014:** Discord sign-in removed, and a clean schema bootstrap for an empty database.

## Watch updates (feature 028)

Following a person used to show almost nothing: their comments are replies in the series' episode threads, and
Mastodon hides replies from home timelines unless the follower also follows the series. A **watch update** is
one Note from the user's own account listing what they finished recently, so following someone shows what they
watch. The full rules are in `specs/028-watch-updates/watch-updates.spec.md`; the short version:

- **Opt-in, off by default** (`/account#watch-updates`, a row in `watch_update_settings`), and only for a profile
  whose effective visibility is Public and a username that can federate. Both are checked when the post is
  queued and again when the worker sends it.
- **At most one post per user and UTC day** (`watch_updates`, primary key `(user_id, day)`), and never one per
  episode: a sync of 300 episodes gives one post. It is queued in the import's own transaction, only when
  something qualifies, and only for recent watching: the window is `(covered_from, covered_until]` on
  `watched_at`, from the latest of 48 hours ago, the moment the user turned updates on and the previous update's
  `covered_until`. An old history, or anything watched before opting in, is never posted.
- **The text** is English ("Watched 3 episodes of Frieren (5, 6, 7) and 1 episode of The Apothecary Diaries
  (12)."): series titles link to `/anime/{seriesId}`, episode numbers to the episode page, at most 5 series and
  6 numbers each, titles escaped, no episode title or thumbnail, no content warning. It goes out as an
  `unlisted` Note (it reaches every follower's home timeline and stays out of public timelines) with the `url`
  `https://{domain}/u/{username}#watched-{yyyy-MM-dd}`, key `watch:{userId}:{yyyy-MM-dd}`.
- **The request is frozen at the first attempt.** Megáfono answers 409 `conflict` when a key is reused with a
  different body, so the worker builds `content` and `url` once, stores them on the `watch_updates` row, and
  every retry sends the stored values.
- **Turning it off, or the profile leaving Public, deletes the earlier posts** (`delete_watch_updates`:
  `DELETE /internal/objects/{id}` for each; 404 and 410 count as done) and stops new ones. Copies other servers
  already fetched may stay. Turning it back on posts new updates only. A change of the instance default
  visibility deletes nothing; the worker's re-check stops new posts.
- Outbox kinds `post_watch_update` and `delete_watch_updates`, subject = the user id, so they run in order with
  the user's `refresh_profile` and `delete_actor`.

## Weekly recap posts (feature 030)

The weekly digest to go with the daily watch updates: one Note a week with the figures of the dashboard's recap card
(feature 029) over a completed week. It is a separate opt-in from the watch updates (two tables, two sections on
`/account`, two removals), so someone can have a quiet weekly digest without daily posts, or the reverse. The full
rules are in `specs/030-recap-posts/recap-posts.spec.md`; the short version:

- **Opt-in, off by default** (`/account#recap-posts`, a row in `recap_post_settings`), and only for a profile
  whose effective visibility is Public and a username that can federate, all checked when the post is queued and
  again when the worker sends it.
- **At most one post per user and completed UTC week** (`recap_posts`, primary key `(user_id, week_start)`, the
  Monday). The week is always the last completed one as of the import's `now()` (Monday 00:00 to the next Monday
  00:00 UTC). It is queued in the import's own transaction, in the savepoint of the watch update (a recap never
  fails an import), only when the week has a fully watched episode of known length. **The user must have opted in
  before the week began** (`enabled_at <= week_start`), so nothing watched before opting in is posted and the first
  recap is for the first full week after turning it on. There is no backfill: a week nobody synced in the
  following week is never posted, and later imports or `user-rebuild` never post a week twice.
- **The text** is English, two paragraphs ("My week on Frunchy (Nov 2 – Nov 8, 2026): 14 episodes, 5 h 20 min, 4
  series (2 new, 2 continued). Watched on 5 of 7 days." then "Most watched: Frieren (6), The Apothecary Diaries (4)
  and Dandadan (2)."): the figures are `Recap.loadBetween` over the week (the dashboard's undeduplicated rules),
  the days are the week's UTC streak days, the most watched are up to 3 series by fully watched episodes, titles
  link to `https://{domain}/anime/{seriesId}` and are escaped, no content warning. It goes out as an `unlisted`
  Note with the `url` `https://{domain}/u/{username}#recap-{yyyy-MM-dd}` and key `recap:{userId}:{yyyy-MM-dd}`.
- **The request is frozen at the first attempt**, as for watch updates (Megáfono answers 409 when a key returns
  with a different body): `content` and `url` are stored on the `recap_posts` row before the call, and every retry
  sends them. A week that turns out empty at build time, or a permanent refusal, makes that row `skipped`.
- **Turning it off, or the profile leaving Public, deletes the earlier recaps** (`delete_recaps`:
  `DELETE /internal/objects/{id}` for each; 404 and 410 count as done) and stops new ones. Turning recaps off
  deletes only recaps (and watch updates off only watch updates); leaving Public removes both. Copies other
  servers already fetched may stay.
- Outbox kinds `post_recap` and `delete_recaps`, subject = the user id.

## Decisions taken

- **Spoilers are content warnings.** A spoiler comment carries `summary` (e.g. "Spoilers: S2 E5") and
  `sensitive: true`, which Mastodon shows as a content warning. Incoming content warnings are shown as
  spoilers on Frunchy. Episode comments start hidden behind a spoiler toggle by default.
- **Dubs share a thread.** On Crunchyroll a dub and its original are separate episodes. One thread per
  original episode, keyed the way the DNA page deduplicates, so the discussion isn't split by audio
  language.
- **One actor per series, not per episode.** Nobody follows an episode, and per-episode actors would mean
  thousands of accounts.
- **Threads are created lazily**, the first time an episode page is shown with federation on or when the
  first comment is written, so the catalog doesn't publish posts nobody reads.
- **Any signed-in user may comment** (feature 009, 2026-10-15), whether or not they watched the
  episode. Remote replies can't be checked against a watch history either, so one open rule fits both.
  "Contains spoilers" starts ticked.
- **Remote edits are kept.** Megáfono 002 sends an edited reply as `reply.updated`, with the same
  payload and `updated` set; GoToSocial's reused activity ids are handled. (This closes the old
  "Edits" question.)

## Open questions

- **Episodes Frunchy hasn't seen.** A thread exists only for episodes in the catalog, which grows from
  imports. Should a series account announce every new episode, or only those someone has watched?
- **Removing a remote comment.** Hiding it on Frunchy is easy, but the copies on other servers stay. The
  docs should say so, as Megáfono's do for followers-only posts.

## Order

1. **Frunchy: the episode page and local comments**, with spoilers and operator moderation. It works
   without the fediverse and fixes the data model first. **Done** (feature 009, 2026-10-15); the table
   shape is in `specs/009-episode-comments/data-model.md`.
2. **Megáfono: replies**, meaning items 1 to 5 above, in its own session. **Done** (Megáfono 002 to 004,
   the last two being actor deletion and blocks; on its main as of 2026-10-17).
3. **Frunchy: the platform** (feature 014). **Done**: the AppHost, the federation switch, the compose file.
4. **Frunchy: the Megáfono integration** (feature 015). **First half done** (2026-10-18): user and series
   accounts (`/megafono/actors`), episode threads, comments both ways with edits and deletes, account
   deletion, display-name refresh, blocks (a user's own on `/account`, the instance's on
   `/review/fediverse`), the handle lines and actor links on the dashboard and series pages, and the
   `fediverse-status` and `series-handle` commands. Everything outbound goes through a Postgres outbox and
   a worker; blocks are synchronous.
5. **Frunchy: watch updates** (feature 028): a user's account posts what they finished watching, once a day at
   most, when they opt in and their profile is public (see "Watch updates" above).
   **Weekly recap posts** (feature 030): the same, one post a week with the recap figures (see "Weekly recap
   posts" above).
6. **Next: following and a feed**, once Megáfono item 6 exists (inbound posts from followed accounts as a
   webhook): follow remote accounts from Frunchy, follower and following lists, and a feed.

**Known gap.** A comment whose thread was refused permanently (for example Megáfono answering 400 or 410
for the thread) is never published, and its `publish_comment` item waits for the thread and retries every
hour forever. Nothing is lost and the comment stays visible on Frunchy, but the queue keeps one such item
until an operator looks (`fediverse-status` shows it as the oldest waiting item with its error).
