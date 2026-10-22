module FrunchyBackend.Handlers.Auth.SignIn

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/web-account.md; design/frunchy_sign_in_signin, per design/REVIEW.md)
// ---------------------------------------------------------------------------

/// The provider's own mark, for the providers that have one (the tests' fake providers don't)
let private providerMark (provider: string) : HtmlElement =
    match provider with
    | "google" ->
        raw
            """<svg class="h-[18px] w-[18px] shrink-0" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"/><path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/><path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z"/><path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z"/></svg>"""
    | _ -> Fragment()

/// Feature 020: the dialog an invite-only instance opens before leaving for the provider. Alpine state:
/// `provider` is the provider link that was clicked (its continue link shows), `skip` the "don't show again" box.
/// Every localStorage access is in try/catch, and the dialog only takes over a click when `showModal` worked, so
/// a missing Alpine, a blocked storage or an old browser leaves the provider links working as plain links.
let private inviteDialogState =
    """{ provider: '', skip: false,
  seen() { try { return window.localStorage.getItem('frunchy.inviteNoticeSeen') === '1' } catch (e) { return false } },
  ask(e) {
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0 || this.seen()) return
    const d = this.$refs.inviteDialog
    if (!d || typeof d.showModal !== 'function') return
    this.provider = e.currentTarget.dataset.provider
    try { d.showModal(); e.preventDefault() } catch (x) {}
  },
  go() { if (this.skip) { try { window.localStorage.setItem('frunchy.inviteNoticeSeen', '1') } catch (e) {} } } }"""

let private inviteDialog (t: Texts) (providers: AuthConfig.FrunchyProvider list) (hrefOf: AuthConfig.FrunchyProvider -> string) =
    dialog(class' = "fr-card m-auto w-[calc(100%-2rem)] max-w-lg text-on-surface shadow-level-2 backdrop:bg-surface/80")
        .data("invite-only-dialog", "")
        .attr("x-ref", "inviteDialog")
        .attr("x-on:close", "skip = false")
        .attr("aria-labelledby", "invite-dialog-title")
        .attr("aria-describedby", "invite-dialog-body") {
        div(class' = "space-y-5") {
            h2(id = "invite-dialog-title", class' = "text-headline-md text-on-surface") { t.InviteDialogTitle }
            p(id = "invite-dialog-body", class' = "text-body-md text-on-surface-variant") { t.InviteDialogBody }

            label(class' = "flex cursor-pointer items-center gap-3 text-body-md text-on-surface") {
                input(type' = "checkbox", class' = "h-4 w-4 accent-primary").attr("x-model", "skip").data("invite-remember", "")
                span() { t.InviteDialogRemember }
            }

            div(class' = "flex flex-col-reverse gap-3 sm:flex-row sm:justify-end") {
                button(type' = "button", class' = btn Secondary).data("invite-cancel", "").attr("x-on:click", "$refs.inviteDialog.close()") {
                    t.InviteDialogCancel
                }

                for provider in providers do
                    a(href = hrefOf provider, class' = btn Primary)
                        .data("invite-continue", provider.Name)
                        .attr("x-show", $"provider === '{provider.Name}'")
                        .attr("style", "display: none")
                        .attr("x-on:click", "go()") {
                        t.InviteDialogContinue provider.DisplayName
                    }
            }
        }
    }

let view
    (ctx: HttpContext)
    (t: Texts)
    (mode: SignUpMode.SignUpMode)
    (providers: AuthConfig.FrunchyProvider list)
    (returnUrl: string)
    (failed: bool)
    =
    let inviteOnly = (mode = SignUpMode.Invite)
    let hrefOf (provider: AuthConfig.FrunchyProvider) = $"/signin/{provider.Name}?returnUrl={Uri.EscapeDataString returnUrl}"

    Fragment() {
        pageHeading t.SignInTitle (Some t.SignInIntro)

        // Plain text, so nobody depends on the dialog (and the only copy there is without JavaScript)
        if inviteOnly then
            // Information, not a warning: a plain line like the home page's notes, so the sign-in buttons stay first
            p(class' = "flex max-w-prose items-start gap-2 text-body-md text-on-surface-variant").data("invite-only-note", "") {
                span(class' = "text-primary") { icon Theme.Info }
                span() { t.SignInInviteOnlyNote }
            }

        if failed then
            (alert Failure t.SignInFailed).data("signin-error", "")

        match providers with
        | [] -> (alert Notice t.SignInUnavailable).data("signin-unavailable", "") :> HtmlElement
        | _ ->
            let wrapper = div()

            if inviteOnly then
                wrapper.attr("x-data", inviteDialogState) |> ignore

            wrapper {
                div(class' = "flex flex-col gap-3") {
                    for provider in providers do
                        let link =
                            a(href = hrefOf provider, class' = btn Secondary + " w-full").data("provider", provider.Name)

                        if inviteOnly then
                            link.data("invite-dialog", "").attr("x-on:click", "ask($event)") |> ignore

                        link {
                            providerMark provider.Name
                            t.SignInWith provider.DisplayName
                        }
                }

                if inviteOnly then
                    inviteDialog t providers hrefOf
            }

        div(class' = "border-t border-outline-variant/40 pt-4") {
            // Ghost button pulled left so its text lines up with the card's content
            a(href = "/", class' = btnSm Ghost + " -ml-3.5") {
                icon Theme.ArrowBack
                t.BackHome
            }
        }
    }
    |> centredCard
    |> Layout.page ctx t t.SignInTitle

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private providers (ctx: HttpContext) =
    ctx.RequestServices.GetServices<AuthConfig.FrunchyProvider>() |> List.ofSeq

let private returnUrlOf (ctx: HttpContext) =
    WebAuth.localReturnUrl (ctx.TryGetQueryValue "returnUrl" |> Option.defaultValue "") "/account"

let page: EndpointHandler =
    fun ctx ->
        task {
            let failed = ctx.TryGetQueryValue "error" |> Option.isSome
            // Read per request, so a change by the root shows at once
            let! mode = SignUpMode.current (ctx.GetService<DbConfig>())
            return! ctx.WriteHtmlView(view ctx (Texts.current ()) mode (providers ctx) (returnUrlOf ctx) failed)
        }

/// Start the provider round trip in sign-in mode
let start (provider: string) : EndpointHandler =
    fun ctx ->
        match providers ctx |> List.tryFind (fun p -> p.Name = provider) with
        | Some p -> WebAuth.challenge ctx p.Name p.Scheme "signin" (returnUrlOf ctx) None
        | None ->
            ctx.SetStatusCode StatusCodes.Status404NotFound
            let t = Texts.current ()
            ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    GET [
        route "/signin" page |> configureEndpoint _.RequireRateLimiting("signin")
        routef "/signin/{%s}" start |> configureEndpoint _.RequireRateLimiting("signin")
    ]
