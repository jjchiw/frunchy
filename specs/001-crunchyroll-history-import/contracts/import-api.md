# Contract: Import API (backend ↔ browser extension)

**Status**: v1. Final except for one pending item, `profileId` (see [History owner](#history-owner)).
**Consumers**: the browser extension (`../browser-extension`, its own Claude session) and the
integration tests in `tests/FrunchyBackend.Tests`.
**Base URL (dev)**: `http://localhost:5099` (launch profile `http`); `https://localhost:5001` also
works if the dev cert is trusted. Make it configurable in the extension.

General rules:
- Every endpoint requires `Authorization: Bearer <syncKey>`.
- JSON in and out, UTF-8, camelCase field names.
- Times are ISO-8601 UTC (`2026-09-30T22:46:02Z`).
- Errors always use the [error shape](#errors).
- Clients MUST NOT retry 4xx responses. They MAY retry 5xx responses and network errors with
  backoff; re-sending is always safe because the backend deduplicates.

---

## `POST /api/imports`: submit history

### Request

```http
POST /api/imports
Authorization: Bearer frk_3q2...Zg
Content-Type: application/json
```

```jsonc
{
  "exportedAt": "2026-10-02T08:44:53.031Z",   // optional, informational
  "profileId": "…",                           // optional: PENDING, see History owner
  "pages": [                                  // 1–100 pages
    {
      "url": "https://www.crunchyroll.com/content/v2/3b6dd336-f005-5b47-9b98-b26a174a2010/watch-history?locale=en-US&page=eyJjIjoi…&page_size=100",
      "capturedAt": "2026-10-02T08:42:33.056Z", // optional
      "body": { "total": 100, "data": [ /* entries, verbatim */ ], "meta": { /* verbatim */ } }
    }
  ]
}
```

- `pages[].body` is Crunchyroll's response **verbatim**. Don't strip or reshape anything. Entries
  without `panel` are normal.
- `pages[].url` is the request URL exactly as intercepted (absolute or relative). The backend only
  reads its path, `/content/v2/{accountId}/watch-history`. The query (cursor, locale, …) is treated
  as opaque.
- No limit on entries per page. Request body ≤ 30,000,000 bytes, so a full 64-page export fits in
  one request. Live syncs SHOULD send one page per request as pages are intercepted.
- This is exactly the extension's export file format (`{ exportedAt, pages: [ { url, body,
  capturedAt } ] }`), so an export can be uploaded unchanged.

### Responses

| Status | When | Body |
|---|---|---|
| **202 Accepted** | stored and queued | `{ "importId": "uuid", "status": "pending", "statusUrl": "/api/imports/{importId}", "entriesReceived": 100 }` |
| 400 `invalid_submission` | not JSON, no `pages`, 0 or >100 pages, a page without string `url` or without `body.data` array | error |
| 400 `history_owner_missing` | a page URL doesn't contain `/content/v2/{id}/watch-history`, pages disagree on the id, or `profileId` is required but missing | error |
| 401 `unauthorized` | missing, unknown or revoked key | error |
| 409 `history_owner_mismatch` | the user is bound to a different Crunchyroll history owner | error |
| 409 `history_owner_taken` | this history owner is bound to another Frunchy user | error |
| 413 `payload_too_large` | body over the limit | error |

Nothing is stored on any non-202 response. A user's first 202 binds them to the submission's
history owner.

---

## `GET /api/imports/{importId}`: submission status

Returns the status only to the owning user's key. Any other key, or an unknown id, gets **404
`not_found`**. A 401 is returned for a bad key.

```json
{
  "importId": "6f1c…",
  "status": "completed",
  "receivedAt": "2026-10-02T08:45:01Z",
  "completedAt": "2026-10-02T08:45:03Z",
  "counts": { "received": 400, "processed": 309, "unresolved": 91, "unsupported": 0, "failed": 0 },
  "issues": [
    { "page": 3, "index": 12, "episodeId": "G14U47QGW", "outcome": "unresolved", "reason": "Entry has no episode details (panel missing)" }
  ],
  "error": null
}
```

- `status`: `pending` | `processing` | `completed` | `failed`.
  - `failed` means the whole submission couldn't be processed, and `error` says why.
  - Problems with individual entries never fail the submission; they're listed in `issues`.
- `counts`: zero until `completed`. `received` = `processed + unresolved + unsupported + failed`.
- `issues`: every non-processed entry, with its page index (0-based, in submission order), entry
  index within that page, Crunchyroll episode id (when present), outcome, and reason in English.
- Suggested polling: every 2–5 s until `completed` or `failed`. A 64-page submission completes in
  seconds.

---

## `GET /api/me`: who am I

```json
{
  "username": "animenerd",
  "displayName": "AnimeNerd",
  "dashboardUrl": "http://localhost:5099/u/animenerd",
  "lastSyncAt": "2026-10-02T08:45:01Z",
  "historyOwnerBound": true
}
```

- `lastSyncAt` is the most recent accepted submission, `null` if none.
- `historyOwnerBound` is `false` until the first accepted submission.
- Use it to validate a pasted key (200 = valid, 401 = wrong or revoked) and for the popup's user
  name and "Open dashboard" link. `dashboardUrl` is absolute, built from the request's scheme and
  host.

---

## Errors

```json
{ "error": "history_owner_mismatch", "message": "This sync key belongs to a user bound to a different Crunchyroll profile. Ask the operator to reset the binding if you changed account or profile." }
```

`error` is a stable code for the client to map to its own localized text. `message` is English
and meant for logs and debugging.

| Code | HTTP |
|---|---|
| `unauthorized` | 401 |
| `invalid_submission` | 400 |
| `history_owner_missing` | 400 |
| `history_owner_mismatch` | 409 |
| `history_owner_taken` | 409 |
| `payload_too_large` | 413 |
| `not_found` | 404 |

## History owner

The history owner is `accountId` from the page URL path, plus `profileId` **if** Crunchyroll's
URL turns out not to identify the profile.

- **PENDING** until the extension's capture under two profiles is in (research R5). Until then,
  the backend accepts `profileId` as optional and includes it in the owner when present.
- If the capture shows the path id is per account, the backend will **require** `profileId`
  (400 `history_owner_missing` without it). The extension then reads it from wherever the capture
  shows it is available, never from or by forwarding the auth token.
- The backend session will message the extension session with the outcome. No other part of this
  contract depends on it.

## Change log

- v1 (2026-10-02): first version. Replaces the old `POST /imports` with
  `{ user_id, data: "<string>" }` and its snake_case responses.
