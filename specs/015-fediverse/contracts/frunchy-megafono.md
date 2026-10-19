# Contract: Frunchy ↔ Megáfono (Frunchy's side)

Frunchy implements Megáfono's app contract v1 (001 + 002 + 003 + 004) and calls its internal API v1.

## What Frunchy serves (federation on; 404 otherwise)

| Route | Auth | Answers |
|---|---|---|
| `GET /megafono/actors?handle={h}` | Bearer | 200 actor JSON, 404, 401 |
| `GET /megafono/actors/{id}` | Bearer | the same |
| `POST /megafono/webhooks` | Bearer + `Megafono-Signature` | 204 (applied, repeated, ignored), 401 (auth/signature), 400 (not JSON or no `id`/`type`), 413 (over 1 MB) |

Actor JSON:
- **Person**: `{ id, handle, name, profileUrl: "https://{domain}/u/{username}", type: "Person", locked: false }`.
- **Group**: `{ id: "series-{seriesId}", handle, name: series title, profileUrl: "https://{domain}/anime/{seriesId}", iconUrl?, type: "Group", locked: false }`.

## What Frunchy calls (outbox worker unless noted)

| Item | Call | Done on | Key |
|---|---|---|---|
| ensure_thread | `POST /internal/actors/series-{seriesId}/objects` `{audience:"public", object:{type:"Note", content, url}}` | 201, 200 | `thread:{seriesId}:{groupKey}` |
| publish_comment | `POST /internal/actors/{userId}/objects` `{audience:"public", object:{type:"Note", content, url, inReplyTo, summary, sensitive}}` | 201, 200 | `comment:{id}` |
| update_comment | `PUT /internal/objects/{objectId}` `{type:"Note", content, url, summary, sensitive}` | 200 (404, 410 permanent) | — |
| delete_comment | `DELETE /internal/objects/{objectId}` | 202 | — |
| delete_actor | `DELETE /internal/actors/{userId}` | 202, 404 | — |
| refresh_profile | `PUT /internal/actors/{userId}` | 202, 404, 410 | — |
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
