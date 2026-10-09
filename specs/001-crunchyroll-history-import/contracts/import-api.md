# Contract: Import API (backend ↔ browser extension)

**Status**: v1.2. Final. `profileId` is **required** (see [History owner](#history-owner)).
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
  "profileId": "…",                           // REQUIRED: the active Crunchyroll profile id
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
| 400 `history_owner_missing` | `profileId` missing or empty, a page URL doesn't contain `/content/v2/{id}/watch-history`, or pages disagree on the id | error |
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
- `counts`: `received` is known when the submission is accepted; the others are 0 until `completed`, when
  `received` = `processed + unresolved + unsupported + failed`.
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

## Web pages the extension may link to

Fixed paths under the configured backend URL. They're stable, like the API routes. A signed-out
visitor goes through `/signin` and comes back to the same page.

| Path | Use it for |
|---|---|
| `/account` | the owner's account page; it holds "Reset Crunchyroll binding" (for `history_owner_mismatch` / `history_owner_taken`) |
| `/account/key` | "Connect extension": confirm, then create a new sync key (it revokes the old one) |
| `/u/{username}/dna` | the user's public Anime DNA (feature 004); `username` comes from `GET /api/me`. Below 15 fully watched episodes it shows a "not enough data yet" state, never an error |

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
| `server_error` | 500 (unexpected; safe to retry with backoff) |

## History owner

The history owner is **`(accountId, profileId)`**: `accountId` comes from the page URL path and
`profileId` from the envelope.

- A capture of a second profile of the same account showed that Crunchyroll's history URL carries
  the **account** id, which is the same for every profile, and the payload contains no profile field
  (research R5). Without `profileId`, two profiles' histories would be merged.
- `profileId` is therefore **required** (400 `history_owner_missing` when missing or empty). Send the
  id of the Crunchyroll profile that was active when the pages were captured. The extension reads it
  from wherever it's available, never from or by forwarding the auth token.
- Two profiles of one account are different history owners, so each can belong to a different
  Frunchy user. A user bound to profile A who submits profile B's pages gets 409
  `history_owner_mismatch`.

## Change log

- v1.4 (2026-10-09): added `/u/{username}/dna` to the web pages the extension may link to
  (feature 004). Additive; no API change.
- v1.3 (2026-10-08): documented the web pages the extension may link to (`/account`,
  `/account/key`). Additive; no API change.
- v1.2 (2026-10-02): **`profileId` is required** (breaking for clients that omit it): the history URL
  doesn't identify the profile. See History owner.
- v1.1 (2026-10-02): added the `server_error` (500) code; `counts.received` is set when the submission is accepted; the other counts
  stay 0 until `completed`. `profileId` is implemented as optional (owner = account + profile
  when present); see research R5 status.
- v1 (2026-10-02): first version. Replaces the old `POST /imports` with
  `{ user_id, data: "<string>" }` and its snake_case responses.
