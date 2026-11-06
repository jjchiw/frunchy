/// Feature 028 (FR-002 to FR-006, FR-014): operators follow the same series on other Frunchy instances. Same rules as
/// the other review pages: every handler is wrapped in `Layout.requireOperator`, outermost, before any form is read,
/// so everyone else gets the plain 404 on every route and verb; the `review-write` limiter counts operators only, so
/// an outsider never sees a 429. Served only with federation on. The two reads the form needs at once (the NodeInfo
/// check, the remote-actor lookup) are synchronous; the follow calls themselves go through the outbox.
module FrunchyBackend.Handlers.Review.InstanceFollows

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.InstanceFollows
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View: the intro, the last notice, the add form, the instances and the series followed
// ---------------------------------------------------------------------------

let private successKeys = set [ "follow-added"; "follow-removed" ]
let private neutralKeys = set [ "follow-already"; "follow-not-followed" ]

let private failureKeys =
    set [ "follow-invalid"; "follow-not-found"; "follow-blocked"; "follow-unreachable"; "follow-not-series"; "follow-self"
          "follow-not-frunchy"; "follow-unknown-series" ]

let private notice (t: Texts) (msg: string) : HtmlElement =
    if successKeys.Contains msg then
        (alert Success (t.FollowNotice msg)).data("follow-notice", msg) :> HtmlElement
    elif neutralKeys.Contains msg then
        (alert Notice (t.FollowNotice msg)).data("follow-notice", msg) :> HtmlElement
    elif failureKeys.Contains msg then
        (alert Failure (t.FollowNotice msg)).data("follow-notice", msg) :> HtmlElement
    else
        Fragment()

let private stateTone (state: string) =
    match state with
    | "accepted" -> PrimaryTone
    | "rejected"
    | "failed" -> SecondaryTone
    | _ -> Muted

let private removeForm (ctx: HttpContext) (t: Texts) (label: string) (domain: string) (series: string option) : HtmlElement =
    form(method = "post", action = "/review/follows/remove").data("follow-remove", "") {
        ctx.GetAntiforgeryInput()
        input(type' = "hidden", name = "domain", value = domain)

        match series with
        | Some id -> input(type' = "hidden", name = "series", value = id)
        | None -> Fragment()

        button(type' = "submit", class' = btnSm Secondary).attr("aria-label", t.FollowsRemoveLabel label) {
            icon Theme.LinkOff
            t.FollowsRemoveButton
        }
    }
    :> HtmlElement

let private instanceRow (ctx: HttpContext) (t: Texts) (row: InstanceRow) : HtmlElement =
    li(class' = "flex flex-wrap items-center justify-between gap-x-4 gap-y-3 py-3 first:pt-0 last:pb-0")
        .data("instance-follow", row.Domain)
        .data("series-count", string row.SeriesCount) {
        div(class' = "flex min-w-0 flex-col items-start gap-1.5") {
            pill TertiaryTone t.FollowsInstanceKind
            span(class' = "fr-mono text-body-md text-on-surface [overflow-wrap:anywhere]") { row.Domain }

            span(class' = "text-label-md font-normal text-on-surface-variant") {
                t.FollowsInstanceSeries row.SeriesCount
                " · "
                Layout.timeElement t "relative" row.Since
            }
        }

        removeForm ctx t row.Domain row.Domain None
    }
    :> HtmlElement

let private seriesRow (ctx: HttpContext) (t: Texts) (row: SeriesRow) : HtmlElement =
    let title = row.Title |> Option.defaultValue row.SeriesId
    let remote = row.Handle |> Option.defaultValue row.RemoteActor

    li(class' = "flex flex-wrap items-center justify-between gap-x-4 gap-y-3 py-3 first:pt-0 last:pb-0")
        .data("series-follow", $"{row.SeriesId}@{row.Domain}")
        .data("follow-state", row.State)
        .data("follow-source", row.Source) {
        div(class' = "flex min-w-0 flex-col items-start gap-1.5") {
            a(href = $"/anime/{row.SeriesId}", class' = "text-body-md text-on-surface hover:underline [overflow-wrap:anywhere]")
                .data("series-page-link", "") {
                title
            }

            span(class' = "fr-mono text-label-md font-normal text-on-surface-variant [overflow-wrap:anywhere]") { remote }

            div(class' = "flex flex-wrap items-center gap-2") {
                pill (stateTone row.State) (t.FollowState row.State)
                pill Muted row.Domain
                pill Muted (t.FollowSource row.Source)
            }

            span(class' = "text-label-md font-normal text-on-surface-variant") { Layout.timeElement t "relative" row.Since }

            match row.LastError with
            | Some code -> span(class' = "fr-mono text-label-md font-normal text-on-surface-variant").data("follow-error", "") { t.FollowLastError code }
            | None -> Fragment()
        }

        removeForm ctx t $"{row.SeriesId}@{row.Domain}" row.Domain (Some row.SeriesId)
    }
    :> HtmlElement

let private view (ctx: HttpContext) (t: Texts) (msg: string) (instances: InstanceRow list) (series: SeriesRow list) =
    div(class' = "mx-auto w-full max-w-3xl space-y-6").data("follows-review", "") {
        pageHeading t.FollowsTitle (Some t.FollowsIntro)
        notice t msg

        div(class' = "fr-card") {
            form(method = "post", action = "/review/follows/add", class' = "space-y-4").data("follow-add", "") {
                ctx.GetAntiforgeryInput()

                div(class' = "w-full sm:max-w-lg") {
                    fieldWith
                        "text"
                        []
                        (fun i -> i.attr("required", "").attr("autocomplete", "off").attr("autocapitalize", "none").attr("spellcheck", "false") |> ignore)
                        t.FollowsAddLabel
                        "target"
                        ""
                        (Some t.FollowsAddHint)
                        None
                        true
                }

                button(type' = "submit", class' = btn Primary) {
                    icon Theme.Link
                    t.FollowsAddButton
                }
            }
        }

        div(class' = "fr-card space-y-4") {
            h2(class' = "text-headline-md text-on-surface") { t.FollowsInstancesTitle }

            match instances with
            | [] -> p(class' = "text-body-md text-on-surface-variant").data("follows-empty", "instances") { t.FollowsInstancesEmpty }
            | rows ->
                ul(class' = "divide-y divide-outline-variant/40") {
                    for row in rows do
                        instanceRow ctx t row
                }
        }

        div(class' = "fr-card space-y-4") {
            h2(class' = "text-headline-md text-on-surface") { t.FollowsSeriesTitle }

            match series with
            | [] -> p(class' = "text-body-md text-on-surface-variant").data("follows-empty", "series") { t.FollowsSeriesEmpty }
            | rows ->
                ul(class' = "divide-y divide-outline-variant/40") {
                    for row in rows do
                        seriesRow ctx t row
                }
        }
    }
    |> Layout.page ctx t t.FollowsTitle

// ---------------------------------------------------------------------------
// Adding
// ---------------------------------------------------------------------------

/// The change and its security event, in one transaction. True when anything changed.
let private change
    (db: DbConfig)
    (userId: Guid)
    (event: SecurityEventType)
    (detail: string)
    (work: NpgsqlConnection -> NpgsqlTransaction -> Task<bool>)
    : Task<bool> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let! changed = work connection tx

        if changed then
            do! SecurityEvents.record connection tx userId event (Some detail) Actor.User

        do! tx.CommitAsync()
        return changed
    }

/// Whether our catalog has the series (a catalog series, or an episode of it)
let private knownSeries (db: DbConfig) (seriesId: string) : Task<bool> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM cr_series WHERE id = @id) OR EXISTS (SELECT 1 FROM cr_episodes WHERE series_id = @id)",
                connection
            )

        cmd.Parameters.AddWithValue("id", seriesId) |> ignore
        let! found = cmd.ExecuteScalarAsync()
        return unbox<bool> found
    }

/// What Megáfono's lookup said about an account, as the notice key that stops the add, or the actor
let private lookup (client: HttpClient) (target: MegafonoClient.BlockTarget) : Task<Result<MegafonoClient.RemoteActor, string>> =
    task {
        let! result, actor = MegafonoClient.lookupRemoteActor client target

        match result.Status, result.ErrorCode, actor with
        | _, _, Some a when result.IsSuccess -> return Ok a
        | 404, _, _ -> return Error "follow-not-found"
        | 409, Some "blocked", _ -> return Error "follow-blocked"
        | 400, _, _ -> return Error "follow-invalid"
        | _ -> return Error "follow-unreachable"
    }

/// Whether the host is a Frunchy instance, as the notice key that stops the add when it isn't
let private checkHost (ctx: HttpContext) (host: string) : Task<string option> =
    task {
        let client = ctx.GetService<IHttpClientFactory>().CreateClient NodeInfo.ClientName

        match! NodeInfo.check client host with
        | NodeInfo.IsFrunchy -> return None
        | NodeInfo.NotFrunchy -> return Some "follow-not-frunchy"
        | NodeInfo.Unreachable -> return Some "follow-unreachable"
    }

/// `target` -> the notice key (FR-003, in this order; each failure writes nothing)
let private add (ctx: HttpContext) (values: Federation.FederationValues) (userId: Guid) (typed: string) : Task<string> =
    task {
        let db = ctx.GetService<DbConfig>()
        let now = ctx.GetService<TimeProvider>().GetUtcNow()
        let isSelf (host: string) = String.Equals(host, values.Domain, StringComparison.OrdinalIgnoreCase)

        match MegafonoClient.parseBlockTarget typed with
        | None -> return "follow-invalid"
        | Some(MegafonoClient.Domain host) ->
            if isSelf host then
                return "follow-self"
            else
                match! checkHost ctx host with
                | Some key -> return key
                | None ->
                    let! changed = change db userId SecurityEventType.InstanceFollowAdded host (fun c tx -> addInstance c tx host now)
                    return (if changed then "follow-added" else "follow-already")
        | Some(MegafonoClient.Actor _ as target) ->
            let client = ctx.GetService<IHttpClientFactory>().CreateClient MegafonoClient.ClientName

            match! lookup client target with
            | Error key -> return key
            | Ok actor ->
                match (if actor.Type = "Group" then seriesIdOfActor actor.Id else None) with
                | None -> return "follow-not-series"
                | Some(host, seriesId) ->
                    if isSelf host then
                        return "follow-self"
                    else
                        match! checkHost ctx host with
                        | Some key -> return key
                        | None ->
                            match! knownSeries db seriesId with
                            | false -> return "follow-unknown-series"
                            | true ->
                                let! changed =
                                    change db userId SecurityEventType.SeriesFollowAdded $"{seriesId}@{host}" (fun c tx -> addSeries c tx seriesId host now)

                                return (if changed then "follow-added" else "follow-already")
    }

/// `domain` and an optional `series` -> the notice key (FR-004)
let private remove (ctx: HttpContext) (userId: Guid) (domainValue: string) (seriesValue: string) : Task<string> =
    task {
        let db = ctx.GetService<DbConfig>()

        match MegafonoClient.parseBlockTarget domainValue with
        | Some(MegafonoClient.Domain host) ->
            if String.IsNullOrEmpty seriesValue then
                let! changed = change db userId SecurityEventType.InstanceFollowRemoved host (fun c tx -> removeInstance c tx host)
                return (if changed then "follow-removed" else "follow-not-followed")
            elif isSeriesId seriesValue then
                let! changed =
                    change db userId SecurityEventType.SeriesFollowRemoved $"{seriesValue}@{host}" (fun c tx -> removeSeries c tx seriesValue host)

                return (if changed then "follow-removed" else "follow-not-followed")
            else
                return "follow-invalid"
        | _ -> return "follow-invalid"
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// GET /review/follows
let handler: EndpointHandler =
    Layout.requireOperator (fun _ ctx ->
        task {
            let t = Texts.current ()
            let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
            use connection = getConnection (ctx.GetService<DbConfig>())
            do! connection.OpenAsync()
            let! instances, series = list connection
            return! ctx.WriteHtmlView(view ctx t msg instances series)
        })

/// A POST: operator (404 for anyone else), then the antiforgery check and the form. Always redirects back with a notice.
let private post (action: string) (work: HttpContext -> Federation.FederationValues -> Guid -> IFormCollection -> Task<string>) : EndpointHandler =
    Layout.requireOperator (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx

            match ctx.GetService<Federation.FederationSettings>() with
            | Federation.Disabled -> return! WebAuth.redirectTo ctx "/review/follows"
            | Federation.Enabled values ->
                let! key = work ctx values userId form
                ctx.GetLogger().LogInformation("review follows {Action} by {User}: {Key}", action, userId, key)
                return! WebAuth.redirectTo ctx $"/review/follows?msg={key}"
        })

/// POST /review/follows/add: `target` (a server name, a handle or a series account's address)
let addHandler: EndpointHandler =
    post "add" (fun ctx values userId form -> add ctx values userId (WebAuth.formValue form "target"))

/// POST /review/follows/remove: `domain`, and `series` to remove one series follow only
let removeHandler: EndpointHandler =
    post "remove" (fun ctx _ userId form -> remove ctx userId (WebAuth.formValue form "domain") (WebAuth.formValue form "series"))

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("review-write")

let endpoint =
    [ GET [ route "/review/follows" handler ]
      POST [ route "/review/follows/add" addHandler |> limited
             route "/review/follows/remove" removeHandler |> limited ] ]
