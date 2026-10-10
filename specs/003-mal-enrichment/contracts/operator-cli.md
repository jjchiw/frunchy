# Contract: Operator CLI for MyAnimeList enrichment

Base contract: `specs/001-crunchyroll-history-import/contracts/operator-cli.md`. Invocation is
`dotnet run -- <command> [args]`, and the exit codes are unchanged: 0 ok, 1 usage, 2 unknown
target, 3 invalid input. A new code 4 means the service failed temporarily: nothing changed, try
again.

Every command reads and writes the same tables as the worker. Only `mal-set` can call the service
(one by-id request, through the same paced, retried client; research R4). The rest never do.

| Command | Effect | Output |
|---|---|---|
| `mal-status` | summary | seasons per status (`pending`, `matched` split by method, `ambiguous`, `no_match`, `failed`); anime held; refreshes due; the earliest `next_attempt_at`; the last 5 `last_error` values |
| `mal-list [--status S] [--limit N]` | seasons not `matched` (or only status S), ordered like the queue (FR-002a); default limit 50 | season id, series and season titles, status, reason, attempts, next attempt, and for `ambiguous` the candidates (`malId`, title, type, year) |
| `mal-show <crSeriesId>` | every season of a series | per season: status, method, MyAnimeList id, title, type, year, score, genres; exit 2 for an unknown series |
| `mal-set <crSeasonId> <malId>` | manual match (FR-006). Fetches the anime unless it's held; dubs that follow this season follow it too | confirmation with the anime's title; exit 2 for an unknown season; **exit 3 when MyAnimeList says that id doesn't exist** (the season is unchanged); exit 4 on a temporary failure |
| `mal-none <crSeasonId>` | marks the season "not on MyAnimeList" (`no_match`, method `manual`, reason `manual-none`) | confirmation; exit 2 for an unknown season |
| `mal-retry <crSeasonId \| crSeriesId>` | queues the season, or every season of the series, at the front (`requested_at`). An automatic row goes back to `pending` (attempts reset). For a manual row the match is kept, and its anime is queued for refresh instead | how many seasons and anime were queued; exit 2 for an unknown id |
| `mal-auto <crSeasonId>` (feature 007) | sends a manual decision back to the matcher: the season and the dubs that follow it go to `pending` (attempts reset, queued at the front), and the series' stored searches are dropped | confirmation; exit 2 for an unknown season; **exit 3 when the season isn't a manual decision** (nothing changes); exit 1 for bad usage |
| `mal-rebuild` | re-derives the stored fields from the stored responses, then re-decides every automatic match from stored data only (FR-021). **No requests** | counts: anime re-derived, matches unchanged, changed, and left because the data needed isn't held |

Notes:
- Ids are Crunchyroll ids as stored (`cr_series.id`, `cr_seasons.id`), as `mal-list` and
  `mal-show` print them.
- A manual decision is undone only by another manual command (`mal-set` or `mal-none` again), or by
  `mal-auto`, which hands it back to the matcher; `mal-retry` keeps manual rows.
- `mal-set`, `mal-none`, `mal-retry` and `mal-auto` call `Infrastructure/MalActions.fs`, the same
  functions the web review pages (`/review/mal`, feature 007) call, so both leave identical rows. The
  CLI passes no state check (`Any`; `mal-auto` requires a manual row); the web checks the state the
  form was shown in and refuses a stale form.
