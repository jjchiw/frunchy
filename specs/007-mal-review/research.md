# Research: Review MyAnimeList Matches on the Web

## R1. Who is an operator

- **Decision**: the `Frunchy:Operators` setting holds usernames, read per request from
  `IConfiguration`. It accepts an array section (`Frunchy:Operators:0`, `Frunchy__Operators__0` in
  Bella or env vars) or one comma-separated value. Comparison is case-insensitive, since usernames
  are stored lowercase. Empty means nobody.
- **Username**: `WebAuth.validatePrincipal` already reads the user row on every request. It also
  selects `username` and keeps it in `ctx.Items`, next to the display name, so the check costs no
  extra query. `WebAuth.currentUsername ctx` and `WebAuth.isOperator ctx` expose it.
- **Rejected**: a username claim in the cookie, which would go stale until the next sign-in, and a
  role column, which the spec rules out.

## R2. Not found for everyone else

- **Decision**: `WebAuth.requireOperator` runs **first** in every review handler, GET and POST,
  before `readValidatedForm`. A signed-out visitor or non-operator gets `ctx.SetStatusCode 404` and
  `Layout.messagePage` with the not-found texts.
- **Why this ordering hides the endpoints**: the forgery check fails inside `readValidatedForm`, so
  it never runs for them, and an unauthorized POST gets the same 404 as a GET. It doesn't redirect
  to `/signin` the way `requireSignedIn` does, because a redirect would advertise the page.

## R3. Shared actions (FR-006)

- **Decision**: a new `Infrastructure/MalActions.fs`, compiled after `MalClient.fs` and before
  `Views` and `Handlers`, holds the four actions. The CLI commands in `Operations/` and the web
  handlers both call it:
  - `setMatch`: the body of today's `MalSet.setMatch`, moved. It fetches the anime once when it isn't
    held. Its results are `UnknownSeason`, `UnknownAnime`, `Unavailable` (also covers a missing
    client id) and `NotOpen`.
  - `setNone`: wraps `MalCatalog.writeManual db id None`.
  - `retry`: today's `MalRetry.retry`, moved. It takes a season or series id, and the web passes the
    season's series id.
  - `backToAutomatic`: **new**. In one transaction, it sets the manual row and the dub rows that
    follow it back to `pending` (clearing mal_id, method, shared_from, candidates, reason, attempts,
    next_attempt_at and claimed_at, with `requested_at = now()`), then drops the series' stored
    searches as `retry` does. A new CLI command, `mal-auto <crSeasonId>`, keeps parity.
- **Stale forms**: each action takes an `expect` argument:
  - `Open`: the status is ambiguous, no_match or failed, and the method isn't manual. The web
    review list uses it.
  - `Manual`: the method is manual. The web manual view uses it.
  - `Any`: today's CLI behaviour.

  The check and the write share one transaction with `SELECT … FOR UPDATE`, so a concurrent decision
  can't slip in between. A mismatch returns `NotOpen` and writes nothing.

## R4. The review list's queries (slice SQL)

- **Order**: the review slice has its own list SQL, with the same order as `MalList.list`:
  requested first, then the most recent watch across all users (NULLs last), then the season id.
  Statuses: `= ANY(@statuses)`, by default the three open ones.
- **Each row also carries**:
  - the series id;
  - the number of distinct users and of fully watched episodes with a known duration;
  - the username of the user with the most recent watch, for the anime page link;
  - the candidates JSON, as stored.
- **Limit**: 50, as the CLI default, plus a `count(*)` of all matching rows for the "N more" note.
- **Manual view**: `method = 'manual'` rows with series and season titles, the decision (MyAnimeList
  id and title, or "not on MyAnimeList") and `decided_at`, newest first.

## R5. Inputs

- **Ids**: a typed value is either digits (1 to 9) or a link matching
  `^(https?://)?(www\.)?myanimelist\.net/anime/(\d{1,9})(/.*)?$`. Anything else is refused before
  any request.
- **Seasons**: the season id comes from a hidden field and is validated as `^[A-Z0-9]{6,20}$`.

## R6. Security activity, logs, limits

- **Events**: four new `SecurityEventType` cases, `mal_set`, `mal_none`, `mal_retry` and
  `mal_auto`, with `Actor.User` (it is the account's own action). They're recorded in a short
  transaction after a successful action, with labels in both languages.
- **Logs**: refused actions (stale, bad input) aren't security events. They're logged at
  information level, like every action with its season and MyAnimeList id.
- **Rate limit**: a new policy, `review-write`, allows 30 per minute per IP on every POST.

## R7. Notices (post/redirect/get)

- **Decision**: every POST redirects to the page it came from with `?msg=<key>&season=<id>`. The
  slice maps each key to a localized notice, success or error, rendered with `alert` and
  `data-review-notice=<key>`.
- **Keys**: `set`, `none`, `retry`, `auto`, `unknown-season`, `unknown-anime`, `unavailable`,
  `not-open`, `bad-input`.

## R8. Account link

- **Decision**: `AccountPage.view` gets an `isOperator` flag from `WebAuth.isOperator ctx`. When it is
  true, the page shows a "Review MyAnimeList matches" link to `/review/mal` with `data-review-link`.
