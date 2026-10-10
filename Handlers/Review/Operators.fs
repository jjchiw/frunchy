module FrunchyBackend.Handlers.Review.Operators

open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (specs/008-root-operator/contracts/operators.md): the root, who is configuration only, the
// operators in the table with who added them and when, and the form to add one
// ---------------------------------------------------------------------------

/// Success notices; every other key is a refusal
let private successKeys = set [ "added"; "removed" ]

let private noticeKeys = set [ "added"; "removed"; "unknown-user"; "is-root"; "already-operator"; "not-operator"; "bad-input" ]

let private noticeView (t: Texts) (msg: string) : HtmlElement =
    if noticeKeys.Contains msg then
        div().data("operators-notice", msg) { alert (if successKeys.Contains msg then Success else Failure) (t.OperatorNotice msg) } :> HtmlElement
    else
        Fragment() :> HtmlElement

let private rootCard (t: Texts) (root: string) =
    div(class' = "fr-card flex flex-wrap items-center justify-between gap-x-6 gap-y-2").data("operators-root", root) {
        div(class' = "min-w-0 space-y-0.5") {
            p(class' = "text-label-md text-on-surface-variant") { t.OperatorsRootLabel }
            p(class' = "fr-mono text-body-md font-semibold text-on-surface [overflow-wrap:anywhere]") { root }
        }

        p(class' = "max-w-md text-body-md text-on-surface-variant") { t.OperatorsRootNote }
    }

let private addForm (ctx: HttpContext) (t: Texts) =
    form(method = "post", action = "/review/operators/add", class' = "fr-card space-y-3").data("action", "add") {
        ctx.GetAntiforgeryInput()

        div(class' = "space-y-4") {
            div(class' = "w-full sm:w-72") {
                fieldWith
                    "text"
                    []
                    (fun i -> i.attr("required", "").attr("autocomplete", "off").attr("autocapitalize", "none").attr("spellcheck", "false") |> ignore)
                    t.OperatorsAddLabel
                    "username"
                    ""
                    (Some t.OperatorsAddHint)
                    None
                    true
            }

            button(type' = "submit", class' = btn Primary) {
                icon Theme.Link
                t.OperatorsAddButton
            }
        }
    }

let private rowView (ctx: HttpContext) (t: Texts) (row: OperatorActions.OperatorRow) =
    let addedAt = row.AddedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)

    li(class' = "flex flex-wrap items-center justify-between gap-x-6 gap-y-3 py-4 first:pt-0 last:pb-0")
        .data("operator", row.Username)
        .data("added-by", defaultArg row.AddedBy "")
        .data("added-at", addedAt) {
        div(class' = "min-w-0 space-y-0.5") {
            p(class' = "text-body-md font-semibold text-on-surface [overflow-wrap:anywhere]") { row.DisplayName }
            p(class' = "fr-mono text-label-md font-normal text-on-surface-variant") { row.Username }
        }

        div(class' = "flex flex-wrap items-center gap-x-6 gap-y-1 text-label-md font-normal text-on-surface-variant") {
            span() { $"{t.OperatorsColAddedBy}: {defaultArg row.AddedBy t.OperatorsAddedFromServer}" }
            span() { Layout.timeElement t "relative" row.AddedAt }
        }

        form(method = "post", action = "/review/operators/remove").data("action", "remove") {
            ctx.GetAntiforgeryInput()
            input(type' = "hidden", name = "username", value = row.Username)

            button(type' = "submit", class' = btnSm Secondary).attr("aria-label", t.OperatorsRemoveLabel row.Username) {
                icon Theme.LinkOff
                t.OperatorsRemoveButton
            }
        }
    }

let private view (ctx: HttpContext) (t: Texts) (root: string) (msg: string) (rows: OperatorActions.OperatorRow list) =
    div(class' = "space-y-6").data("operators", "") {
        pageHeading t.OperatorsTitle (Some t.OperatorsIntro)
        noticeView t msg
        rootCard t root
        addForm ctx t

        if rows.IsEmpty then
            div(class' = "fr-card py-10 text-center text-body-md text-on-surface-variant").data("operators-empty", "") { t.OperatorsEmpty }
        else
            div(class' = "fr-card") {
                ul(class' = "divide-y divide-outline-variant/40") {
                    for row in rows do
                        rowView ctx t row
                }
            }
    }
    |> Layout.page ctx t t.OperatorsTitle

// ---------------------------------------------------------------------------
// Handlers: every one starts with requireRoot, before anything else is looked at
// ---------------------------------------------------------------------------

/// GET /review/operators
let handler: EndpointHandler =
    Layout.requireRoot (fun _ ctx ->
        task {
            let t = Texts.current ()
            let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
            let! rows = OperatorActions.list (ctx.GetService<DbConfig>())
            let root = WebAuth.currentUsername ctx |> Option.defaultValue ""
            return! ctx.WriteHtmlView(view ctx t root msg rows)
        })

/// The notice key for a change that did nothing
let private refusal (error: OperatorActions.ActionError) =
    match error with
    | OperatorActions.UnknownUser -> "unknown-user"
    | OperatorActions.IsRoot -> "is-root"
    | OperatorActions.AlreadyOperator -> "already-operator"
    | OperatorActions.NotOperator -> "not-operator"

/// A POST: root (404 for anyone else), then a recent sign-in, then the antiforgery check and the form,
/// then the username rule (no query for a bad one), then the action, which records the security events
/// in its own transaction. Always redirects with a notice.
let private post
    (event: SecurityEventType)
    (success: string)
    (work: DbConfig -> string option -> string -> OperatorActions.By -> Task<Result<unit, OperatorActions.ActionError>>)
    : EndpointHandler =
    Layout.requireRoot (fun rootUserId ->
        WebAuth.requireFresh "/review/operators" (fun _ ctx ->
            task {
                let! form = WebAuth.readValidatedForm ctx
                let log = ctx.GetLogger()
                let rootName = WebAuth.currentUsername ctx |> Option.defaultValue ""

                match Username.tryCreate (WebAuth.formValue form "username") with
                | None ->
                    log.LogInformation("operators {Event} by {Root} refused: bad username", SecurityEventType.toDb event, rootName)
                    return! WebAuth.redirectTo ctx "/review/operators?msg=bad-input"
                | Some username ->
                    let root = WebAuth.rootOperator (ctx.GetService<IConfiguration>())

                    match! work (ctx.GetService<DbConfig>()) root username (OperatorActions.Web rootUserId) with
                    | Ok() ->
                        log.LogInformation("operators {Event} {User} by {Root}", SecurityEventType.toDb event, username, rootName)
                        return! WebAuth.redirectTo ctx $"/review/operators?msg={success}"
                    | Error error ->
                        let key = refusal error
                        log.LogInformation("operators {Event} {User} by {Root} refused: {Key}", SecurityEventType.toDb event, username, rootName, key)
                        return! WebAuth.redirectTo ctx $"/review/operators?msg={key}"
            }))

/// POST /review/operators/add: `username`
let addHandler: EndpointHandler = post SecurityEventType.OperatorAdded "added" OperatorActions.add

/// POST /review/operators/remove: `username`
let removeHandler: EndpointHandler = post SecurityEventType.OperatorRemoved "removed" OperatorActions.remove

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("operators-write")

let endpoint =
    [ GET [ route "/review/operators" handler ]
      POST [ route "/review/operators/add" addHandler |> limited
             route "/review/operators/remove" removeHandler |> limited ] ]
