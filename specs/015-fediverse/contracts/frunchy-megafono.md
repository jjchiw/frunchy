# Contract: Frunchy ↔ Megáfono (Frunchy's side)

Frunchy implements Megáfono's app contract v1 (001 + 002 + 003 + 004) and calls its internal API v1.

## What Frunchy serves (federation on; 404 otherwise)

| Route | Auth | Answers |
|---|---|---|
| `GET /megafono/actors?handle={h}` | Bearer | 200 actor JSON, 404, 401 |
| `GET /megafono/actors/{id}` | Bearer | the same |
| `POST /megafono/webhooks` | Bearer + `Megafono-Signature` | 204 (applied, repeated, ignored), 401 (auth/signature), 400 (not JSON or no `id`/`type`), 413 (over 1 MB) |

**Events handled (028)**, besides the 015 ones:

| Event | What Frunchy does |
|---|---|
| `follow.accepted`, `follow.rejected`, `follow.removed` | with `follow.direction` `outbound` and a user `series-{ID}`, the matching `series_follows` row (`series_id = ID`, `remote_actor = actor.id`) takes the state and Megáfono's follow id; no row: ignored |
| `post.received`, `post.updated` | a thread of a followed series account (no `inReplyTo`, public or unlisted, ids from `extensions` under our namespace, else the URL fallback, the series the follow's) is upserted into `remote_threads`; one whose `inReplyTo` is a live remote thread, or a comment stored under one, is a remote comment (`comments.remote_thread`) on the thread's local episode, `unknown_thread` when there is none; anything else is ignored |
| `post.deleted` | a remote thread gets `deleted_at`, a remote comment that came through one is tombstoned; anything else is ignored |

Actor JSON:
- **Person**: `{ id, handle, name, profileUrl: "https://{domain}/u/{username}", type: "Person", locked: false, discoverable, indexable }`.
- **Group**: `{ id: "series-{seriesId}", handle, name: series title, profileUrl: "https://{domain}/anime/{seriesId}", iconUrl?, type: "Group", locked: false, discoverable: true, indexable: true }`.
- **`discoverable` and `indexable`** (added by feature 021): optional JSON booleans, exactly these names; any non-boolean value is invalid. A user gets both `true` when the effective profile visibility (their choice, else `Frunchy:Profiles:DefaultVisibility`) is Public, and both `false` for Members and Private; a series (Group) gets both `true`. Absent, Megáfono keeps its hardcoded `discoverable: true, indexable: false`. Optional: Megáfono ignores them until it renders them. A change of a user's visibility queues `refresh_profile` for that user (only with federation on, only when the effective level changed), so Megáfono looks the actor up again.

## What Frunchy calls (outbox worker unless noted)

| Item | Call | Done on | Key |
|---|---|---|---|
| ensure_thread | `POST /internal/actors/series-{seriesId}/objects` `{audience:"public", object:{type:"Note", content, url}}` | 201, 200 | `thread:{seriesId}:{groupKey}` |
| publish_comment | `POST /internal/actors/{userId}/objects` `{audience:"public", object:{type:"Note", content, url, inReplyTo, summary, sensitive}}` | 201, 200 | `comment:{id}` |
| update_comment | `PUT /internal/objects/{objectId}` `{type:"Note", content, url, summary, sensitive}` | 200 (404, 410 permanent) | — |
| delete_comment | `DELETE /internal/objects/{objectId}` | 202 | — |
| delete_actor | `DELETE /internal/actors/{userId}` | 202, 404 | — |
| refresh_profile | `PUT /internal/actors/{userId}` | 202, 404, 410 | — |
| follow_series (028) | `POST /internal/actors/series-{seriesId}/following` `{target: "https://{domain}/ap/actors/series-{seriesId}"}` | 2xx (`followId`, `status`); 404 sets the follow `not_found`; 409 and 400 set it `failed` (refused) | — |
| unfollow_series (028) | `DELETE /internal/actors/series-{seriesId}/following?actor={actor URL}` | 2xx, 404 | — |
| ensure_thread (028) | as above, and the body also carries `extensions: {namespace: "https://github.com/jjchiw/frunchy/ns#", prefix: "frunchy", data: {episode: {seriesId, episodeId, groupKey}}}` (`extensions: null` on a publish without it) | | |
| remote actor lookup (sync, 028) | `GET /internal/remote-actors?handle=` or `?url=` | 200 `{id, handle, type}`; 404, 409 `blocked`, 400, 502 | — |
| user blocks (sync) | `GET/POST/DELETE /internal/actors/{userId}/blocks` | as in 004 | — |
| instance blocks (sync) | `GET/POST/DELETE /internal/blocks` | as in 004 | — |

## Test hooks (pages)

| Element | Hook |
|---|---|
| Handle line (dashboard, series page) | `data-fediverse-handle` = `@handle@domain` |
| Remote comment | the comment hooks of 009 plus `data-remote` and `data-author` = `@user@host` |
| Remote author link | `data-remote-author`, href = profile URL, `rel="noopener nofollow ugc"`, `target="_blank"` |
| Account blocks | `data-blocks`, each `data-block` = actor id or domain, form `data-block-add`, `data-block-remove`, error `data-block-error` = code |
| Operator page | `data-instance-blocks` (and the same item hooks) |
| Follows page (028) | `data-follows-review`, form `data-follow-add` (`target`), notice `data-follow-notice`, `li[data-instance-follow]` (`data-series-count`), `li[data-series-follow="{seriesId}@{domain}"]` (`data-follow-state`, `data-follow-source`), `data-follow-remove` (`domain`, `series`), `data-follows-empty`; `/account` link `data-follows-link` |
| Episode page (028) | `li[data-remote-thread="{domain}"]` with an `a[href]`; a comment through a remote thread has `data-comment-via="{domain}"` |
