# Contract: How Frunchy uses Jikan (outgoing)

Jikan REST API v4, base URL `Frunchy:Jikan:BaseUrl` (default `https://api.jikan.moe/v4/`). It's
unauthenticated and GET only. Frunchy sends a `User-Agent: Frunchy-Backend/{assembly version}` header so the service can
identify it. Research R1, R4, R7.

## Requests

| Purpose | Request | Stored as |
|---|---|---|
| Candidate search | `GET anime?q={urlencoded title}&limit=10&sfw` | `mal_searches(query, raw)` |
| Details and relations | `GET anime/{malId}/full`, plus `If-None-Match: {etag}` when one is held | `mal_anime.raw` and the derived columns, tags and relations |

No other endpoint is called. Page views and imports never call it (FR-011).

## Pacing

At most `Frunchy:Jikan:RequestsPerSecond` (default 1) attempts per second per process, counting
retries. That's against the published 3 per second and 60 per minute (FR-010).

## How responses are classified

| Response | Class | Effect |
|---|---|---|
| 200 with `data` | ok | stored; matching continues |
| 304 (only after `If-None-Match`) | unchanged | `checked_at` moves (User Story 4) |
| 404 on `anime/{id}/full` | **definite not found** | lookup: that candidate is dropped; refresh: `missing_since` is set and the details kept; `mal-set`: refused (exit 3) |
| 400 | **definite** (a Frunchy bug) | recorded as failed with the message; not retried automatically |
| 408, 429, 500, 502, 503, 504, connection failure, timeout | **temporary** | up to 3 short retries (honouring `Retry-After`), then the durable backoff of FR-014 |
| 200 whose body can't be read | **temporary** (unexpected shape) | recorded with the reason, retried with backoff (edge case "unexpected response shape") |

An error body (`{ status, type, message, error, report_url }`) is read for `last_error` when present:
`type` and `message`, cut to 300 characters. `report_url` is never followed.
