# Frunchy on the fediverse: episode comments and following

Status: design note, 2026-10-10. Nothing here is built. Each step below becomes its own spec-kit feature.

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

**Settled with Megáfono (its commit 96f9008):**

- **Edit and delete.** Comment edits use `PUT /internal/objects/{objectId}`, which sends Update(Note) to
  the reply's recipients. `inReplyTo`, type and audience can't change. User deletes and operator
  removals use `DELETE /internal/objects/{objectId}`, which sends a Tombstone and takes the comment out
  of the replies collection. Copies already on other servers may stay.
- **Ids.** Ids match `^[A-Za-z0-9_-]{1,64}$` and are never reused. Series use `series-{crSeriesId}`.
  A Group lookup sends `type: "Group"`, `locked: false`, and the series page as `profileUrl`.
- **Handles.** Megáfono allows lowercase letters, digits and underscore, up to 30 characters, so
  series need a short handle, e.g. `frieren`, decided in Frunchy's spec and kept apart from usernames.
  Frunchy usernames (`^[a-z0-9][a-z0-9_-]{2,29}$`) can contain dashes, including a trailing one, so
  Megáfono's pending handle fix must accept that rule. Asked on 2026-10-10.
- **Blocks.** A separate Megáfono task. Frunchy doesn't announce series accounts widely until it
  lands, and until then hiding a comment hides it only on Frunchy.

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

## Open questions

- **Edits.** Megáfono v1 only handles `Update` for actors, so an edited remote comment keeps its first
  version. Acceptable at first; revisit with Megáfono.
- **Episodes Frunchy hasn't seen.** A thread exists only for episodes in the catalog, which grows from
  imports. Should a series account announce every new episode, or only those someone has watched?
- **Who may comment.** Any signed-in Frunchy user, or only someone who has watched the episode? Watched
  only would be a nice anti-spoiler rule, and it's checkable from `watch_events`.
- **Removing a remote comment.** Hiding it on Frunchy is easy, but the copies on other servers stay. The
  docs should say so, as Megáfono's do for followers-only posts.

## Order

1. **Frunchy: the episode page and local comments**, with spoilers and operator moderation. It works
   without the fediverse and fixes the data model first.
2. **Megáfono: replies**, meaning items 1 to 5 above, in its own session.
3. **Frunchy: the Megáfono integration.** User accounts, series accounts with episode threads, and
   comments both ways. Then following remote accounts and a feed, once Megáfono item 6 exists.
