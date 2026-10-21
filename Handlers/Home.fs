module FrunchyBackend.Handlers.Home

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (design/frunchy_home, per design/REVIEW.md: no sample figures, no demo or docs links)
// ---------------------------------------------------------------------------

/// The headline, its part after the comma in the primary colour ("…identity, / in numbers")
let private headline (text: string) =
    match text.LastIndexOf ',' with
    | i when i > 0 && i < text.Length - 1 ->
        Fragment() {
            text.Substring(0, i + 1)
            br()
            span(class' = "text-primary") { text.Substring(i + 1).TrimStart() }
        }
    | _ -> Fragment() { text }

/// Signed out: Sign in. Signed in: the account page, where the extension is connected.
let private callToAction (ctx: HttpContext) (t: Texts) =
    match WebAuth.currentNav ctx with
    | WebAuth.SignedOut ->
        a(href = "/signin", class' = btn Primary).data("home-cta", "signin") {
            t.NavSignIn
            icon Theme.ArrowForward
        }
    | WebAuth.SignedIn _ ->
        a(href = "/account", class' = btn Primary).data("home-cta", "account") {
            t.NavAccount
            icon Theme.ArrowForward
        }

let private hero (ctx: HttpContext) (t: Texts) =
    section(class' = "grid grid-cols-1 items-center gap-10 lg:grid-cols-12").attr("aria-labelledby", "home-title") {
        div(class' = "space-y-6 lg:col-span-7") {
            h1(id = "home-title", class' = "text-display-hero-mobile md:text-display-hero text-on-surface text-balance") {
                headline t.HomeHeadline
            }

            p(class' = "max-w-prose text-body-lg text-on-surface-variant md:text-headline-md md:font-normal") { t.HomeIntro }

            div(class' = "flex flex-wrap gap-3") {
                callToAction ctx t
                a(href = "#how-to-sync", class' = btn Secondary) { t.HomeHowToSyncTitle }
            }

            p(class' = "flex max-w-prose items-start gap-2 text-body-md text-on-surface-variant") {
                span(class' = "text-primary") { icon Theme.Info }
                span() {
                    match (ctx.GetService<Profiles.ProfileSettings>()).DefaultVisibility with
                    | Profiles.Private -> t.HomeProfilesPrivateNote
                    | Profiles.Members -> t.HomeProfilesMembersNote
                    | Profiles.Public -> t.HomeProfilesPublicNote
                }
            }
        }

        // The mark, large: decoration only (no sample figures, REVIEW.md)
        div(class' = "hidden justify-center lg:col-span-5 lg:flex").attr("aria-hidden", "true") {
            span(class' = "block w-60 xl:w-72") { raw Theme.markSvg }
        }
    }

let private stepIcons = [ Theme.Extension; Theme.History; Theme.Sync ]
// Amber throughout: the steps are the reader's own, and rose is for other people
let private stepTone = "bg-primary/10 text-primary"

let private howToSync (t: Texts) =
    section(id = "how-to-sync", class' = "scroll-mt-24 space-y-6").attr("aria-labelledby", "how-to-sync-title") {
        h2(id = "how-to-sync-title", class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface") { t.HomeHowToSyncTitle }

        ol(class' = "grid grid-cols-1 gap-4 md:grid-cols-3") {
            for i, step in List.indexed t.HomeSteps do
                li(class' = "fr-card flex flex-col gap-5").data("home-step", string (i + 1)) {
                    div(class' = "flex items-center justify-between").attr("aria-hidden", "true") {
                        span(class' = $"flex h-10 w-10 items-center justify-center rounded-full text-headline-md {stepTone}") {
                            string (i + 1)
                        }

                        span(class' = "text-headline-md text-on-surface-variant") { icon stepIcons[i % 3] }
                    }

                    p(class' = "text-body-lg text-on-surface") { step }
                }
        }

        p(class' = "flex max-w-prose items-start gap-2 text-body-md text-on-surface-variant") {
            span(class' = "text-primary") { icon Theme.Lock }
            span() { t.HomeNoPassword }
        }
    }

let view (ctx: HttpContext) (t: Texts) (deleted: bool) =
    div(class' = "space-y-10") {
        if deleted then
            (alert Success t.AccountDeletedNotice).data("account-deleted", "")

        div(class' = "space-y-16 md:space-y-24") {
            hero ctx t
            howToSync t
        }
    }
    |> Layout.page ctx t t.HomeTitle

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        let deleted = ctx.TryGetQueryValue "deleted" |> Option.isSome
        ctx.WriteHtmlView(view ctx (Texts.current ()) deleted)

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/" handler ]
