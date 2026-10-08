# Contract: How Frunchy uses MyAnimeList data (outgoing)

Two providers, chosen with `Frunchy:Mal:Provider` (research R13). The default is **`mal-api`**,
MyAnimeList's official API v2. **`jikan`** (Jikan v4) is the alternative. Both are GET only and read
public data. Frunchy sends `User-Agent: Frunchy-Backend/{assembly version}`.

| | `mal-api` (default) | `jikan` |
|---|---|---|
| Base URL (`Frunchy:Mal:BaseUrl`) | `https://api.myanimelist.net/v2/` | `https://api.jikan.moe/v4/` |
| Credentials | `X-MAL-CLIENT-ID: {Frunchy:Mal:ClientId}`, a secret (Bella `Frunchy__Mal__ClientId`, user secrets or the environment; never appsettings) | none |
| Search | `GET anime?q={q}&limit=10&fields=alternative_titles,media_type,start_date,start_season,mean,rating,nsfw,num_episodes,status` | `GET anime?q={q}&limit=10&sfw` |
| Details | `GET anime/{id}?fields=id,title,main_picture,alternative_titles,start_date,mean,media_type,status,genres,num_episodes,start_season,rating,nsfw,studios,related_anime` | `GET anime/{id}/full` |
| Published limits | none | 3 per second, 60 per minute |
| Query rule | queries over 64 characters are cut at the subtitle, then at a word boundary; under 3 characters no request is made | as is |

**Storage**: every body is stored verbatim with its provider (`mal_anime.source`, `mal_searches.source`).
A stored search is reused for 30 days, but only when it came from the current provider.

Page views and imports never call a provider (FR-011).

## Pacing

At most `Frunchy:Mal:RequestsPerSecond` (default 1) attempts per second per process, counting
retries (FR-010).

## Without a client id

With `mal-api` and no `Frunchy:Mal:ClientId`:
- the site starts normally;
- the enrichment worker logs "MyAnimeList enrichment is off: …" and does nothing;
- `mal-set` exits 4 with the same reason;
- the dashboards show what's already held, or today's view.

## How responses are classified

| Response | Class | Effect |
|---|---|---|
| 200 with a readable body | ok | stored; matching continues |
| 304 (only after `If-None-Match`) | unchanged | `checked_at` moves (User Story 4) |
| 404 on details | **definite not found** | lookup: that candidate is dropped; refresh: `missing_since` is set and the details kept; `mal-set`: refused (exit 3) |
| 403 from `mal-api` | **temporary** | MyAnimeList uses it both for a bad client id and for its DoS protection: backed off and shown in `last_error` ("bad client id, or MyAnimeList's DoS protection") |
| 400, 401 | **definite** (a Frunchy bug or configuration) | recorded in `last_error`; in the worker it's retried with the durable backoff like a temporary failure (it shows as `failed` in `mal-list`); `mal-set` exits 4 |
| 408, 429, 500, 502, 503, 504, connection failure, timeout | **temporary** | up to 3 short retries (honouring `Retry-After`), then the durable backoff of FR-014 |
| 200 whose body can't be read | **temporary** (unexpected shape) | recorded with the reason, retried with backoff |

**Error bodies**, read for `last_error` (cut to 300 characters):
- `mal-api`: `{ error, message }`;
- `jikan`: `{ status, type, message, error, report_url }`. `report_url` is never followed.

## Mapping onto the stored fields (`mal-api`)

MyAnimeList's values are mapped onto the shapes the matcher already uses:

| What | Mapping |
|---|---|
| `media_type` | `tv` → "TV", `movie` → "Movie", `ova`, `ona`, `special`, `tv_special`, `music`…; `unknown` → none |
| `status` | `finished_airing` → "Finished Airing", `currently_airing` → "Currently Airing" (sets airing), `not_yet_aired` → "Not yet aired" |
| `relation_type` | `sequel` → "Sequel", `prequel` → "Prequel", `side_story` → "Side Story", `parent_story` → "Parent Story", `summary` → "Summary", `alternative_version` → "Alternative Version", `spin_off` → "Spin-Off", `other` → "Other"… |
| `rating` | `rx` → "Rx - Hentai" (never a candidate, FR-005) |
| `genres` | one list on MyAnimeList, split by id into genres, explicit genres, demographics (Kids, Shoujo, Shounen, Seinen, Josei) and themes (everything else) |
| `start_date` | "YYYY-MM-DD", "YYYY-MM" or "YYYY" → the first day it can mean |
| `mean` | the score (0 or missing → none) |
| `main_picture` | `large`, else `medium` |
