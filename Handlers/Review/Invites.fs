/// Feature 019: operators create, list and revoke sign-up invites on the web (the CLI's `invite-*` commands, through
/// the shared `InviteActions`). Every handler starts with `requireOperator`, so anyone else gets the plain 404
/// before a form is read. The code is shown once, on the page the create POST renders: it never goes in a URL, a
/// redirect, a log line or a security event, and only its hash is stored.
module FrunchyBackend.Handlers.Review.Invites

open System
open System.Globalization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

/// How many invites the page lists
let private listLimit = 100

/// Success notices; every other key is a refusal
let private successKeys = set [ "revoked" ]

let private noticeKeys = set [ "revoked"; "not-active"; "bad-days"; "bad-input" ]

let private noticeView (t: Texts) (msg: string) : HtmlElement =
    if noticeKeys.Contains msg then
        div().data("invites-notice", msg) { alert (if successKeys.Contains msg then Success else Failure) (t.InvitesNotice msg) } :> HtmlElement
    else
        Fragment() :> HtmlElement

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

let private createForm (ctx: HttpContext) (t: Texts) =
    form(method = "post", action = "/review/invites/create", class' = "fr-card space-y-4").data("action", "create") {
        ctx.GetAntiforgeryInput()
        h2(class' = "text-headline-md text-on-surface") { t.InvitesCreateTitle }

        div(class' = "w-full sm:w-48") {
            fieldWith
                "number"
                []
                (fun i ->
                    i
                        .attr("required", "")
                        .attr("min", string InviteActions.minDays)
                        .attr("max", string InviteActions.maxDays)
                        .attr("step", "1")
                        .attr("inputmode", "numeric")
                    |> ignore)
                t.InvitesDaysLabel
                "days"
                "7"
                (Some t.InvitesDaysHint)
                None
                false
        }

        button(type' = "submit", class' = btn Primary) {
            icon Theme.Key
            t.InvitesCreateButton
        }
    }

let private stateTone (state: string) =
    match state with
    | "active" -> PrimaryTone
    | "used" -> TertiaryTone
    | _ -> Muted

let private rowView (ctx: HttpContext) (t: Texts) (row: InviteActions.InviteRow) =
    li(class' = "flex flex-wrap items-center justify-between gap-x-6 gap-y-3 py-4 first:pt-0 last:pb-0")
        .data("invite-row", string row.Id)
        .data("invite-state", row.State) {
        div(class' = "min-w-0 space-y-2") {
            pill (stateTone row.State) (t.InvitesState row.State)

            div(class' = "flex flex-wrap items-center gap-x-6 gap-y-1 text-label-md font-normal text-on-surface-variant") {
                span() {
                    $"{t.InvitesColCreated}: "
                    Layout.timeElement t "relative" row.CreatedAt
                }

                span() {
                    $"{t.InvitesColExpires}: "
                    Layout.timeElement t "relative" row.ExpiresAt
                }

                if row.State = "used" then
                    span().data("invite-used-by", defaultArg row.UsedBy "") {
                        $"{t.InvitesColUsedBy}: "

                        match row.UsedBy with
                        | Some username -> span(class' = "fr-mono") { username } :> HtmlElement
                        | None -> span() { t.InvitesUsedByDeleted } :> HtmlElement
                    }
            }
        }

        if row.State = "active" then
            form(method = "post", action = "/review/invites/revoke").data("action", "revoke") {
                ctx.GetAntiforgeryInput()
                input(type' = "hidden", name = "id", value = string row.Id)

                button(type' = "submit", class' = btnSm Secondary).attr("aria-label", t.InvitesRevokeLabel) {
                    icon Theme.LinkOff
                    t.InvitesRevokeButton
                }
            }
    }

let private view (ctx: HttpContext) (t: Texts) (msg: string) (rows: InviteActions.InviteRow list) =
    div(class' = "space-y-6").data("invites", "") {
        pageHeading t.InvitesTitle (Some t.InvitesIntro)
        noticeView t msg
        createForm ctx t

        div(class' = "fr-card space-y-4") {
            h2(class' = "text-headline-md text-on-surface") { t.InvitesListTitle }

            if rows.IsEmpty then
                p(class' = "py-6 text-center text-body-md text-on-surface-variant").data("invites-empty", "") { t.InvitesEmpty }
            else
                ul(class' = "divide-y divide-outline-variant/40") {
                    for row in rows do
                        rowView ctx t row
                }
        }
    }
    |> Layout.page ctx t t.InvitesTitle

/// The code in a selectable box with a copy button, shown this once. The code is the box's only text.
let private codeBox (t: Texts) (code: string) =
    div(class' = "space-y-3").attr("x-data", "{ copied: false, canCopy: !!navigator.clipboard }") {
        div(class' = "rounded bg-surface-container-lowest p-4 font-mono text-body-lg text-primary break-all select-all border border-outline-variant")
            .data("invite-code", "")
            .attr("x-ref", "code")
            .attr("translate", "no") {
            code
        }

        button(type' = "button", class' = btnSm Secondary)
            .attr("x-show", "canCopy")
            .attr("x-on:click", "navigator.clipboard.writeText($refs.code.textContent.trim()).then(() => { copied = true; setTimeout(() => copied = false, 2000) })") {
            icon Theme.ContentCopy
            span().attr("x-show", "!copied") { t.InvitesCopyCode }
            span().attr("x-show", "copied").attr("style", "display: none") { t.InvitesCodeCopied }
        }

        span(class' = "sr-only").attr("aria-live", "polite") { span().attr("x-show", "copied").attr("style", "display: none") { t.InvitesCodeCopied } }
    }

/// The only time the code is ever shown
let private shownView (ctx: HttpContext) (t: Texts) (invite: InviteActions.CreatedInvite) =
    let signIn = $"{ctx.Request.Host}/signin"

    narrow () {
        div(class' = "fr-card space-y-6").data("invite-shown", "") {
            pageHeading t.InvitesShownTitle None
            codeBox t invite.Code

            p(class' = "text-body-md text-on-surface-variant") {
                $"{t.InvitesExpiresLine} "
                Layout.timeElement t "absolute" invite.ExpiresAt
            }

            p(class' = "text-body-md text-on-surface") { t.InvitesHowTo signIn }
            alert Notice t.InvitesShownOnce

            a(href = "/review/invites", class' = btn Secondary) {
                icon Theme.ArrowBack
                t.InvitesBack
            }
        }
    }
    |> Layout.page ctx t t.InvitesShownTitle

// ---------------------------------------------------------------------------
// Handlers: each starts with requireOperator, before anything else is looked at. POSTs then read the form
// (antiforgery verdict first), validate the input, and only then touch the database.
// ---------------------------------------------------------------------------

/// GET /review/invites
let handler: EndpointHandler =
    Layout.requireOperator (fun _ ctx ->
        task {
            let t = Texts.current ()
            let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
            let! rows = InviteActions.list (ctx.GetService<DbConfig>()) (Some listLimit)
            return! ctx.WriteHtmlView(view ctx t msg rows)
        })

/// A whole number of days from 1 to 365, written in plain digits
let private parseDays (raw: string) : int option =
    match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, d when d >= InviteActions.minDays && d <= InviteActions.maxDays -> Some d
    | _ -> None

/// POST /review/invites/create: `days`. Renders the page that shows the code once (no redirect, no-store).
let createHandler: EndpointHandler =
    Layout.requireOperator (fun operatorId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx

            match parseDays (WebAuth.formValue form "days") with
            | None -> return! WebAuth.redirectTo ctx "/review/invites?msg=bad-days"
            | Some days ->
                let! invite = InviteActions.create (ctx.GetService<DbConfig>()) (Some operatorId) days
                // The invite's id and its operator may be logged; the code never is
                ctx.GetLogger().LogInformation("invites created {Invite} by {Operator}", invite.Id, operatorId)
                ctx.Response.Headers.CacheControl <- "no-store"
                return! ctx.WriteHtmlView(shownView ctx (Texts.current ()) invite)
        })

/// POST /review/invites/revoke: `id`
let revokeHandler: EndpointHandler =
    Layout.requireOperator (fun operatorId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx
            let log = ctx.GetLogger()

            match Guid.TryParse(WebAuth.formValue form "id") with
            | true, id ->
                match! InviteActions.revoke (ctx.GetService<DbConfig>()) (Some operatorId) id with
                | Ok() ->
                    log.LogInformation("invites revoked {Invite} by {Operator}", id, operatorId)
                    return! WebAuth.redirectTo ctx "/review/invites?msg=revoked"
                | Error _ ->
                    log.LogInformation("invites revoke {Invite} by {Operator} refused: not active", id, operatorId)
                    return! WebAuth.redirectTo ctx "/review/invites?msg=not-active"
            | _ -> return! WebAuth.redirectTo ctx "/review/invites?msg=bad-input"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("review-write")

let endpoint =
    [ GET [ route "/review/invites" handler ]
      POST [ route "/review/invites/create" createHandler |> limited
             route "/review/invites/revoke" revokeHandler |> limited ] ]
