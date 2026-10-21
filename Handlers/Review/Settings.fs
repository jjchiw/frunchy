/// Feature 020: the root's site settings. Today one setting, the sign-up mode (`invite` or `open`), kept in
/// `instance_settings` (`SignUpMode`). Every handler starts with `requireRoot`, so anyone else (operators who aren't
/// root, regular users, visitors, and everyone when no root is configured) gets the plain 404 on every verb before
/// a form is read. The POST then needs a recent sign-in, the antiforgery check and a valid choice; the change and its
/// `signup_mode_changed` event commit in one transaction.
module FrunchyBackend.Handlers.Review.Settings

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

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

/// Success notices; every other key is a refusal
let private successKeys = set [ "saved" ]

let private noticeKeys = set [ "saved"; "bad-input" ]

let private noticeView (t: Texts) (msg: string) : HtmlElement =
    if noticeKeys.Contains msg then
        div().data("settings-notice", msg) { alert (if successKeys.Contains msg then Success else Failure) (t.SettingsNotice msg) } :> HtmlElement
    else
        Fragment() :> HtmlElement

let private signUpSection (ctx: HttpContext) (t: Texts) (mode: SignUpMode.SignUpMode) =
    let option (value: SignUpMode.SignUpMode) (label': string) (help: string) =
        label(class' = "flex cursor-pointer items-start gap-3 rounded border border-outline-variant p-4 has-[:checked]:border-primary") {
            let radio =
                input(type' = "radio", name = "mode", value = SignUpMode.toDb value, class' = "mt-1 h-4 w-4 accent-primary")
                    .data("signup-option", SignUpMode.toDb value)

            if mode = value then radio.attr("checked", "") else radio

            span(class' = "space-y-0.5") {
                span(class' = "block text-body-lg text-on-surface") { label' }
                span(class' = "block text-body-md text-on-surface-variant") { help }
            }
        }

    form(method = "post", action = "/review/settings/signup", class' = "fr-card space-y-5").data("action", "signup") {
        ctx.GetAntiforgeryInput()

        div(class' = "space-y-1") {
            h2(class' = "text-headline-md text-on-surface") { t.SettingsSignUpTitle }
            p(class' = "max-w-prose text-body-md text-on-surface-variant") { t.SettingsSignUpIntro }
        }

        fieldset(class' = "space-y-3") {
            legend(class' = "sr-only") { t.SettingsSignUpTitle }
            option SignUpMode.Invite t.SettingsSignUpInviteLabel t.SettingsSignUpInviteHelp
            option SignUpMode.Open t.SettingsSignUpOpenLabel t.SettingsSignUpOpenHelp
        }

        div(class' = "flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between") {
            p(class' = "text-body-md text-on-surface").data("signup-mode", SignUpMode.toDb mode) {
                t.SettingsSignUpState (mode = SignUpMode.Open)
            }

            button(type' = "submit", class' = btn Primary + " shrink-0") { t.SaveButton }
        }
    }

let private view (ctx: HttpContext) (t: Texts) (msg: string) (mode: SignUpMode.SignUpMode) =
    div(class' = "mx-auto w-full max-w-3xl space-y-6").data("settings", "") {
        pageHeading t.SettingsTitle (Some t.SettingsIntro)
        noticeView t msg
        signUpSection ctx t mode
    }
    |> Layout.page ctx t t.SettingsTitle

// ---------------------------------------------------------------------------
// Handlers: every one starts with requireRoot, before anything else is looked at
// ---------------------------------------------------------------------------

/// GET /review/settings
let handler: EndpointHandler =
    Layout.requireRoot (fun _ ctx ->
        task {
            let t = Texts.current ()
            let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
            let! mode = SignUpMode.current (ctx.GetService<DbConfig>())
            return! ctx.WriteHtmlView(view ctx t msg mode)
        })

/// POST /review/settings/signup: `mode` is `invite` or `open`. Root (404 for anyone else), then a recent sign-in,
/// then the antiforgery check and the form, then the choice (anything else changes nothing), then the change and
/// its event in one transaction. Always redirects with a notice.
let signUpHandler: EndpointHandler =
    Layout.requireRoot (fun rootUserId ->
        WebAuth.requireFresh "/review/settings" (fun _ ctx ->
            task {
                let! form = WebAuth.readValidatedForm ctx
                let log = ctx.GetLogger()
                let rootName = WebAuth.currentUsername ctx |> Option.defaultValue ""

                match SignUpMode.tryParseChoice (WebAuth.formValue form "mode") with
                | None ->
                    log.LogInformation("settings signup mode by {Root} refused: bad input", rootName)
                    return! WebAuth.redirectTo ctx "/review/settings?msg=bad-input"
                | Some mode ->
                    do! SignUpMode.set (ctx.GetService<DbConfig>()) rootUserId mode
                    log.LogInformation("settings signup mode set to {Mode} by {Root}", SignUpMode.toDb mode, rootName)
                    return! WebAuth.redirectTo ctx "/review/settings?msg=saved"
            }))

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/review/settings" handler ]
      POST [ route "/review/settings/signup" signUpHandler |> configureEndpoint _.RequireRateLimiting("operators-write") ] ]
