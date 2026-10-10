module FrunchyBackend.Handlers.Review.MalReview

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql
open NpgsqlTypes
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Model (specs/007-mal-review/data-model.md, contracts/review-pages.md)
// ---------------------------------------------------------------------------

type Candidate =
    { MalId: int
      Title: string
      Type: string
      Year: string }

/// A season nobody has decided: what the matcher stored, and who watched it
type OpenRow =
    { SeasonId: string
      SeriesId: string
      SeriesTitle: string
      SeasonTitle: string
      Status: string
      Users: int
      Fully: int
      /// The user with the most recent watch, for the series link
      Watcher: string option
      Candidates: Candidate list }

/// A season decided by hand
type ManualRow =
    { SeasonId: string
      SeriesId: string
      SeriesTitle: string
      SeasonTitle: string
      /// None = "not on MyAnimeList"
      Decision: (int * string) option
      DecidedAt: DateTime option
      Watcher: string option }

/// What a page shows: the rows (at most `pageSize`) and how many matched in all
type Listing<'Row> = { Rows: 'Row list; Total: int }

let private pageSize = 50
let private openStatuses = [ "ambiguous"; "no_match"; "failed" ]

// ---------------------------------------------------------------------------
// Input rules (research R5)
// ---------------------------------------------------------------------------

/// Crunchyroll's id alphabet, checked before anything else
let private seasonIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

let private malDigits = Regex(@"\A\d{1,9}\z", RegexOptions.CultureInvariant)

let private malLink =
    Regex(@"\A(https?://)?(www\.)?myanimelist\.net/anime/(\d{1,9})(/.*)?\z", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

/// A MyAnimeList id typed as digits or pasted as an anime link
let private parseMalId (raw: string) : int option =
    let digits =
        if malDigits.IsMatch raw then Some raw
        else
            let m = malLink.Match raw
            if m.Success then Some m.Groups[3].Value else None

    digits |> Option.map int |> Option.filter (fun id -> id > 0)

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

type private Tab =
    | ToReview
    | Decided

let private statusLabel (t: Texts) (status: string) =
    match status with
    | "ambiguous" -> t.ReviewFilterAmbiguous
    | "no_match" -> t.ReviewFilterNoMatch
    | "failed" -> t.ReviewFilterFailed
    | other -> other

let private statusTone (status: string) =
    match status with
    | "ambiguous" -> SecondaryTone
    | "failed" -> TertiaryTone
    | _ -> Muted

/// Success notices; every other key is a refusal
let private successKeys = set [ "set"; "none"; "retry"; "auto" ]

let private noticeView (t: Texts) (msg: string) (season: string) : HtmlElement =
    let known = successKeys.Contains msg || set [ "unknown-season"; "unknown-anime"; "unavailable"; "not-open"; "bad-input" ] |> Set.contains msg

    if not known then
        Fragment() :> HtmlElement
    else
        let text = if seasonIdPattern.IsMatch season then $"{t.ReviewNotice msg} ({season})" else t.ReviewNotice msg
        div().data("review-notice", msg) { alert (if successKeys.Contains msg then Success else Failure) text } :> HtmlElement

/// A tab or filter: a pill link, the current one marked for assistive tech; `hook` is its data-review-filter
let private tabLink (hook: string option) (href: string) (label': string) (current: bool) =
    let link =
        a(href = href, class' = (if current then "fr-pill fr-pill-primary" else "fr-pill fr-pill-muted hover:text-on-surface"))

    hook |> Option.iter (fun h -> link.data("review-filter", h) |> ignore)
    if current then link.attr("aria-current", "page") { label' } else link { label' }

let private tabs (t: Texts) (tab: Tab) (statuses: string list) =
    div(class' = "flex flex-wrap items-center justify-between gap-x-6 gap-y-3") {
        nav(class' = "flex flex-wrap items-center gap-2").attr("aria-label", t.ReviewTitle) {
            tabLink None "/review/mal" t.ReviewTabOpen (tab = ToReview)
            tabLink None "/review/mal/manual" t.ReviewTabManual (tab = Decided)
        }

        match tab with
        | Decided -> Fragment()
        | ToReview ->
            let all = statuses.Length <> 1

            nav(class' = "flex flex-wrap items-center gap-2").attr("aria-label", t.ReviewColStatus) {
                tabLink (Some "all") "/review/mal" t.ReviewFilterAll all

                for status in openStatuses do
                    tabLink (Some status) $"/review/mal?status={status}" (statusLabel t status) (statuses = [ status ])
            }
    }

let private seasonCell (t: Texts) (seriesId: string) (seriesTitle: string) (seasonTitle: string) (seasonId: string) (watcher: string option) =
    div(class' = "min-w-0 space-y-0.5") {
        p(class' = "text-body-md font-semibold text-on-surface [overflow-wrap:anywhere]") {
            match watcher with
            | Some username -> seriesLink username seriesId seriesTitle :> HtmlElement
            | None -> span() { seriesTitle }
        }

        p(class' = "text-body-md text-on-surface-variant [overflow-wrap:anywhere]") { if seasonTitle = "" then seasonId else seasonTitle }
        p(class' = "fr-mono text-label-md font-normal text-on-surface-variant") { seasonId }
    }

/// A figure with its column label, which only shows inside the row below the md breakpoint
let private figureCell (label': string) (value: HtmlElement) =
    p(class' = "flex items-baseline gap-2 text-body-md text-on-surface md:block") {
        span(class' = "text-label-md font-normal text-on-surface-variant md:hidden") { label' }
        value
    }

let private rowGrid = "grid gap-x-6 gap-y-2 md:grid-cols-[minmax(0,1fr)_7rem_5rem_6rem] md:items-start"

let private postForm (ctx: HttpContext) (action: string) (hook: string) (season: string) (fields: (string * string) list) (content: HtmlElement) =
    form(method = "post", action = action).data("action", hook) {
        ctx.GetAntiforgeryInput()
        input(type' = "hidden", name = "season", value = season)

        for name, value in fields do
            input(type' = "hidden", name = name, value = value)

        content
    }

let private candidateView (ctx: HttpContext) (t: Texts) (season: string) (c: Candidate) =
    li(class' = "flex flex-wrap items-center justify-between gap-x-4 gap-y-2").data("candidate", string c.MalId) {
        div(class' = "min-w-0") {
            a(href = $"https://myanimelist.net/anime/{c.MalId}", target = "_blank", class' = "fr-link inline-flex items-center gap-1.5 text-body-md [overflow-wrap:anywhere]")
                .attr("rel", "noopener") {
                c.Title
                icon Theme.OpenInNew
            }

            p(class' = "fr-mono text-label-md font-normal text-on-surface-variant") { $"{c.Type} · {c.Year} · #{c.MalId}" }
        }

        postForm ctx "/review/mal/set" "set" season [ "mal", string c.MalId ] (
            button(type' = "submit", class' = btnSm Secondary) {
                icon Theme.CheckCircle
                t.ReviewUseThis
            }
        )
    }

/// The id field with its Set match button; the hint it points to is rendered by the caller
let private idForm (ctx: HttpContext) (t: Texts) (season: string) (hintId: string) =
    let inputId = $"mal-{season}"

    div(class' = "w-full sm:w-auto") {
        postForm ctx "/review/mal/set" "set" season [] (
            div(class' = "space-y-1.5") {
                label(for' = inputId, class' = "fr-label") { t.ReviewMalIdLabel }

                div(class' = "flex gap-2") {
                    input(type' = "text", id = inputId, name = "mal", class' = "fr-input fr-mono min-w-0 flex-1 sm:w-60 sm:flex-none", required = true)
                        .attr("aria-describedby", hintId)
                        .attr("autocomplete", "off")

                    button(type' = "submit", class' = btnSm Primary) {
                        icon Theme.Link
                        t.ReviewSetButton
                    }
                }
            }
        )
    }

let private openRowView (ctx: HttpContext) (t: Texts) (r: OpenRow) =
    let hintId = $"mal-{r.SeasonId}-hint"

    li(class' = "space-y-4 py-5 first:pt-0 last:pb-0")
        .data("review-season", r.SeasonId)
        .data("status", r.Status)
        .data("users", string r.Users)
        .data("fully", string r.Fully) {
        div(class' = rowGrid) {
            seasonCell t r.SeriesId r.SeriesTitle r.SeasonTitle r.SeasonId r.Watcher
            div() { pill (statusTone r.Status) (statusLabel t r.Status) }
            figureCell t.ReviewColUsers (span() { string r.Users })
            figureCell t.ReviewColFullyWatched (span() { string r.Fully })
        }

        match r.Candidates with
        | [] -> p(class' = "text-body-md text-on-surface-variant") { t.ReviewNoCandidates }
        | candidates ->
            div(class' = "space-y-2") {
                p(class' = "text-label-md text-on-surface-variant") { t.ReviewColCandidates }

                ul(class' = "max-w-2xl space-y-3") {
                    for c in candidates do
                        candidateView ctx t r.SeasonId c
                }
            }

        div(class' = "space-y-1.5") {
            div(class' = "flex flex-wrap items-end gap-x-6 gap-y-3") {
                idForm ctx t r.SeasonId hintId

                // the quieter ways out sit beside the field, the destructive-ish one last and plainest
                div(class' = "flex flex-wrap gap-2") {
                    postForm ctx "/review/mal/retry" "retry" r.SeasonId [] (
                        button(type' = "submit", class' = btnSm Secondary) {
                            icon Theme.Sync
                            t.ReviewRetryButton
                        }
                    )

                    postForm ctx "/review/mal/none" "none" r.SeasonId [] (
                        button(type' = "submit", class' = btnSm Ghost) {
                            icon Theme.LinkOff
                            t.ReviewNoneButton
                        }
                    )
                }
            }

            p(id = hintId, class' = "fr-hint") { t.ReviewMalIdHint }
        }
    }

let private moreNote (t: Texts) (shown: int) (total: int) : HtmlElement =
    if total > shown then
        p(class' = "text-label-md font-normal text-on-surface-variant").data("review-more", string (total - shown)) { t.ReviewMore(total - shown) } :> HtmlElement
    else
        Fragment()

let private emptyView (text: string) =
    div(class' = "fr-card py-10 text-center text-body-md text-on-surface-variant").data("review-empty", "") { text }

let private openList (ctx: HttpContext) (t: Texts) (listing: Listing<OpenRow>) =
    if listing.Rows.IsEmpty then
        emptyView t.ReviewEmpty
    else
        div(class' = "space-y-3") {
            div(class' = "fr-card") {
                div(class' = $"{rowGrid} hidden border-b border-outline-variant/40 pb-3 text-label-md text-on-surface-variant md:grid") {
                    span() { t.ReviewColSeason }
                    span() { t.ReviewColStatus }
                    span() { t.ReviewColUsers }
                    span() { t.ReviewColFullyWatched }
                }

                ul(class' = "divide-y divide-outline-variant/40 md:pt-5") {
                    for r in listing.Rows do
                        openRowView ctx t r
                }
            }

            moreNote t listing.Rows.Length listing.Total
        }

let private manualRowView (ctx: HttpContext) (t: Texts) (r: ManualRow) =
    li(class' = "space-y-3 py-5 first:pt-0 last:pb-0")
        .data("review-season", r.SeasonId)
        .data("mal", (match r.Decision with Some(id, _) -> string id | None -> "none")) {
        div(class' = "grid gap-x-6 gap-y-2 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_11rem] md:items-start") {
            seasonCell t r.SeriesId r.SeriesTitle r.SeasonTitle r.SeasonId r.Watcher

            p(class' = "text-body-md text-on-surface [overflow-wrap:anywhere]") {
                match r.Decision with
                | Some(malId, title) ->
                    a(href = $"https://myanimelist.net/anime/{malId}", target = "_blank", class' = "fr-link inline-flex items-center gap-1.5")
                        .attr("rel", "noopener") {
                        title
                        icon Theme.OpenInNew
                    }
                | None -> span(class' = "text-on-surface-variant") { t.ReviewManualNone }
            }

            figureCell
                t.ReviewColDecidedAt
                (match r.DecidedAt with
                 | Some at -> Layout.timeElement t "absolute" at :> HtmlElement
                 | None -> span() { "-" })
        }

        postForm ctx "/review/mal/auto" "auto" r.SeasonId [] (
            button(type' = "submit", class' = btnSm Secondary) {
                icon Theme.History
                t.ReviewAutoButton
            }
        )
    }

let private manualList (ctx: HttpContext) (t: Texts) (listing: Listing<ManualRow>) =
    if listing.Rows.IsEmpty then
        emptyView t.ReviewManualEmpty
    else
        div(class' = "space-y-3") {
            div(class' = "fr-card") {
                div(class' = "hidden grid-cols-[minmax(0,1fr)_minmax(0,1fr)_11rem] gap-x-6 border-b border-outline-variant/40 pb-3 text-label-md text-on-surface-variant md:grid") {
                    span() { t.ReviewColSeason }
                    span() { t.ReviewColDecision }
                    span() { t.ReviewColDecidedAt }
                }

                ul(class' = "divide-y divide-outline-variant/40 md:pt-5") {
                    for r in listing.Rows do
                        manualRowView ctx t r
                }
            }

            moreNote t listing.Rows.Length listing.Total
        }

let private shell (ctx: HttpContext) (t: Texts) (tab: Tab) (statuses: string list) (msg: string) (season: string) (body: HtmlElement) =
    let title, intro, hook =
        match tab with
        | ToReview -> t.ReviewTitle, t.ReviewIntro, "open"
        | Decided -> t.ReviewManualTitle, t.ReviewManualIntro, "manual"

    div(class' = "space-y-6").data("review", hook) {
        pageHeading title (Some intro)
        tabs t tab statuses
        noticeView t msg season
        body
    }
    |> Layout.page ctx t title

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private parseCandidates (json: string) : Candidate list =
    use doc = JsonDocument.Parse json

    [ for c in doc.RootElement.EnumerateArray() ->
          let text (name: string) =
              match c.TryGetProperty name with
              | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
              | true, v when v.ValueKind = JsonValueKind.Number -> string (v.GetInt32())
              | _ -> "-"

          { MalId = c.GetProperty("malId").GetInt32()
            Title = text "title"
            Type = text "type"
            Year = text "year" } ]

let private optString (r: System.Data.Common.DbDataReader) (i: int) = if r.IsDBNull i then None else Some(r.GetString i)

/// The seasons nobody has decided, in the CLI's order (requested first, then the latest watch);
/// the total counts every one, not just the page
let private loadOpen (db: DbConfig) (statuses: string list) : Task<Listing<OpenRow>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT m.season_id, se.series_id,
                       coalesce(sr.title,
                                (SELECT min(e.series_title) FROM cr_episodes e WHERE e.series_id = se.series_id),
                                se.series_id),
                       coalesce(se.title, ''), m.status, m.candidates::text,
                       coalesce(w.users, 0), coalesce(w.fully, 0), w.watcher,
                       count(*) OVER ()::int
                FROM cr_season_matches m
                JOIN cr_seasons se ON se.id = m.season_id
                LEFT JOIN cr_series sr ON sr.id = se.series_id
                LEFT JOIN LATERAL (
                    SELECT count(DISTINCT we.user_id)::int AS users,
                           (count(*) FILTER (WHERE we.fully_watched AND e.duration_ms IS NOT NULL))::int AS fully,
                           max(we.watched_at) AS latest,
                           (array_agg(u.username ORDER BY we.watched_at DESC))[1] AS watcher
                    FROM cr_episodes e
                    JOIN watch_events we ON we.cr_episode_id = e.id
                    JOIN users u ON u.id = we.user_id
                    WHERE e.season_id = m.season_id) w ON true
                WHERE m.status = ANY(@statuses) AND m.method IS DISTINCT FROM 'manual'
                ORDER BY (m.requested_at IS NULL), m.requested_at, w.latest DESC NULLS LAST, m.season_id
                LIMIT @limit
                """,
                connection
            )

        cmd.Parameters.Add(NpgsqlParameter("statuses", NpgsqlDbType.Array ||| NpgsqlDbType.Text, Value = Array.ofList statuses)) |> ignore
        cmd.Parameters.AddWithValue("limit", pageSize) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<OpenRow>()
        let mutable total = 0

        while r.Read() do
            total <- r.GetInt32 9

            rows.Add
                { SeasonId = r.GetString 0
                  SeriesId = r.GetString 1
                  SeriesTitle = r.GetString 2
                  SeasonTitle = r.GetString 3
                  Status = r.GetString 4
                  Candidates = if r.IsDBNull 5 then [] else parseCandidates (r.GetString 5)
                  Users = r.GetInt32 6
                  Fully = r.GetInt32 7
                  Watcher = optString r 8 }

        return { Rows = List.ofSeq rows; Total = total }
    }

/// The manual decisions, newest first
let private loadManual (db: DbConfig) : Task<Listing<ManualRow>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT m.season_id, se.series_id,
                       coalesce(sr.title,
                                (SELECT min(e.series_title) FROM cr_episodes e WHERE e.series_id = se.series_id),
                                se.series_id),
                       coalesce(se.title, ''), m.mal_id, coalesce(a.title_english, a.title), m.decided_at,
                       (SELECT u.username
                        FROM cr_episodes e
                        JOIN watch_events we ON we.cr_episode_id = e.id
                        JOIN users u ON u.id = we.user_id
                        WHERE e.season_id = m.season_id
                        ORDER BY we.watched_at DESC LIMIT 1),
                       count(*) OVER ()::int
                FROM cr_season_matches m
                JOIN cr_seasons se ON se.id = m.season_id
                LEFT JOIN cr_series sr ON sr.id = se.series_id
                LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                WHERE m.method = 'manual'
                ORDER BY m.decided_at DESC NULLS LAST, m.season_id
                LIMIT @limit
                """,
                connection
            )

        cmd.Parameters.AddWithValue("limit", pageSize) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<ManualRow>()
        let mutable total = 0

        while r.Read() do
            total <- r.GetInt32 8

            rows.Add
                { SeasonId = r.GetString 0
                  SeriesId = r.GetString 1
                  SeriesTitle = r.GetString 2
                  SeasonTitle = r.GetString 3
                  Decision = if r.IsDBNull 4 then None else Some(r.GetInt32 4, (if r.IsDBNull 5 then $"#{r.GetInt32 4}" else r.GetString 5))
                  DecidedAt = if r.IsDBNull 6 then None else Some(r.GetDateTime 6)
                  Watcher = optString r 7 }

        return { Rows = List.ofSeq rows; Total = total }
    }

/// The account's own record of what it did (research R6), in its own short transaction
let private recordEvent (db: DbConfig) (userId: Guid) (event: SecurityEventType) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        do! SecurityEvents.record connection tx userId event None Actor.User
        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Handlers: every one starts with requireOperator, before any form is read
// ---------------------------------------------------------------------------

let private query (ctx: HttpContext) (name: string) = ctx.TryGetQueryValue name |> Option.defaultValue ""

/// GET /review/mal
let handler: EndpointHandler =
    Layout.requireOperator (fun _ ctx ->
        task {
            let t = Texts.current ()
            let statuses = if List.contains (query ctx "status") openStatuses then [ query ctx "status" ] else openStatuses
            let! listing = loadOpen (ctx.GetService<DbConfig>()) statuses
            return! ctx.WriteHtmlView(shell ctx t ToReview statuses (query ctx "msg") (query ctx "season") (openList ctx t listing))
        })

/// GET /review/mal/manual
let manualHandler: EndpointHandler =
    Layout.requireOperator (fun _ ctx ->
        task {
            let t = Texts.current ()
            let! listing = loadManual (ctx.GetService<DbConfig>())
            return! ctx.WriteHtmlView(shell ctx t Decided openStatuses (query ctx "msg") (query ctx "season") (manualList ctx t listing))
        })

/// The notice key for an action that changed nothing
let private refusal (error: MalActions.ActionError) =
    match error with
    | MalActions.UnknownSeason -> "unknown-season"
    | MalActions.UnknownAnime -> "unknown-anime"
    | MalActions.Unavailable _ -> "unavailable"
    | MalActions.NotOpen -> "not-open"

/// A POST: operator, then the antiforgery check, then the input rules (`work` runs only for a valid
/// season id), then the action; success is recorded as a security event. Always redirects with a notice.
let private post (back: string) (event: SecurityEventType) (success: string) (work: HttpContext -> string -> IFormCollection -> Task<Result<string, string>>) : EndpointHandler =
    Layout.requireOperator (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx
            let season = WebAuth.formValue form "season"
            let log = ctx.GetLogger()

            if not (seasonIdPattern.IsMatch season) then
                log.LogInformation("review {Event} refused: bad season id", SecurityEventType.toDb event)
                return! WebAuth.redirectTo ctx $"{back}?msg=bad-input"
            else
                match! work ctx season form with
                | Ok detail ->
                    do! recordEvent (ctx.GetService<DbConfig>()) userId event
                    log.LogInformation("review {Event} {Season} {Detail}", SecurityEventType.toDb event, season, detail)
                    return! WebAuth.redirectTo ctx $"{back}?msg={success}&season={season}"
                | Error key ->
                    log.LogInformation("review {Event} {Season} refused: {Key}", SecurityEventType.toDb event, season, key)
                    return! WebAuth.redirectTo ctx $"{back}?msg={key}&season={season}"
        })

let private clockOf (ctx: HttpContext) =
    match ctx.RequestServices.GetService<TimeProvider>() with
    | null -> TimeProvider.System
    | clock -> clock

let private mapError (result: Result<'a, MalActions.ActionError>) (detail: 'a -> string) =
    match result with
    | Ok value -> Ok(detail value)
    | Error e -> Error(refusal e)

/// POST /review/mal/set: `mal` is an id or a MyAnimeList link
let setHandler: EndpointHandler =
    post "/review/mal" SecurityEventType.MalSet "set" (fun ctx season form ->
        task {
            match parseMalId (WebAuth.formValue form "mal") with
            | None -> return Error "bad-input"
            | Some malId ->
                let! result =
                    MalActions.setMatch
                        (ctx.GetService<DbConfig>())
                        (ctx.GetService<Net.Http.IHttpClientFactory>())
                        (clockOf ctx)
                        (MalClient.readSettings (ctx.GetService<IConfiguration>()))
                        MalActions.Open
                        season
                        malId

                return mapError result (fun _ -> $"mal {malId}")
        })

/// POST /review/mal/none
let noneHandler: EndpointHandler =
    post "/review/mal" SecurityEventType.MalNone "none" (fun ctx season _ ->
        task {
            let! result = MalActions.setNone (ctx.GetService<DbConfig>()) (clockOf ctx) MalActions.Open season
            return mapError result (fun () -> "not on MyAnimeList")
        })

/// POST /review/mal/retry
let retryHandler: EndpointHandler =
    post "/review/mal" SecurityEventType.MalRetry "retry" (fun ctx season _ ->
        task {
            let! result = MalActions.retry (ctx.GetService<DbConfig>()) MalActions.Open season
            return mapError result (fun (seasons, _) -> $"queued {seasons}")
        })

/// POST /review/mal/auto: a manual decision goes back to the matcher
let autoHandler: EndpointHandler =
    post "/review/mal/manual" SecurityEventType.MalAuto "auto" (fun ctx season _ ->
        task {
            let! result = MalActions.backToAutomatic (ctx.GetService<DbConfig>()) MalActions.Manual season
            return mapError result (fun () -> "back to automatic")
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("review-write")

let endpoint =
    [ GET [ route "/review/mal" handler; route "/review/mal/manual" manualHandler ]
      POST [ route "/review/mal/set" setHandler |> limited
             route "/review/mal/none" noneHandler |> limited
             route "/review/mal/retry" retryHandler |> limited
             route "/review/mal/auto" autoHandler |> limited ] ]
