module FrunchyBackend.Handlers.Guide

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View: the install guide for people who got an invite. Public, static text; the screenshots are
// wwwroot/guide/{name}-{lang}.png, one per language
// ---------------------------------------------------------------------------

/// The screenshot names, in the order they appear on the page (the tests pin this set)
let ImageNames =
    [ "signup"
      "account-extension"
      "sync-key"
      "extension-options"
      "extension-ready"
      "extension-scraping"
      "extension-synced"
      "dashboard"
      "dna"
      "account-visibility" ]

/// The extension's shots are 2x pictures of a 380px popup and a 480px settings page: never wider than that
let private widthOf (name: string) =
    match name with
    | "extension-ready"
    | "extension-scraping"
    | "extension-synced" -> " max-w-[380px]"
    | "extension-options" -> " max-w-[480px]"
    | _ -> ""

let private figureOf (t: Texts) (name: string) =
    figure(class' = "space-y-2").data("guide-image", name) {
        img(src = $"/guide/{name}-{t.Lang}.png", alt = t.GuideImageAlt name, loading = "lazy", class' = "w-full h-auto rounded border border-outline-variant" + widthOf name)

        figcaption(class' = "text-label-md text-on-surface-variant") { t.GuideImageCaption name }
    }

let private figures (t: Texts) (names: string list) =
    div(class' = "space-y-6") {
        for name in names do
            figureOf t name
    }

let private paragraphs (texts: string list) =
    Fragment() {
        for text in texts do
            p(class' = "text-body-lg text-on-surface") { text }
    }

let private steps (texts: string list) =
    ol(class' = "space-y-3") {
        for i, text in List.indexed texts do
            li(class' = "flex items-start gap-3 text-body-lg text-on-surface") {
                span(class' = "flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-surface-container-high text-label-md text-primary")
                    .attr("aria-hidden", "true") {
                    string (i + 1)
                }

                span() { text }
            }
    }

/// One section of the guide: an anchor, a heading and its content
let private guideSection (id': string) (title: string) (content: HtmlElement) =
    section(id = id', class' = "scroll-mt-24 space-y-5").data("guide-section", id').attr("aria-labelledby", $"guide-{id'}-title") {
        h2(id = $"guide-{id'}-title", class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface") { title }
        content
    }

let private install (t: Texts) =
    div(class' = "space-y-5") {
        paragraphs t.GuideInstallBody

        a(href = Home.ExtensionUrl, class' = btn Primary).data("guide-extension", "") {
            icon Theme.Extension
            t.GuideInstallButton
        }
    }

let private signUp (t: Texts) =
    div(class' = "space-y-5") {
        steps t.GuideSignUpSteps

        div(class' = "flex flex-wrap items-center gap-3") {
            a(href = "/signin", class' = btn Primary) {
                t.NavSignIn
                icon Theme.ArrowForward
            }
        }

        p(class' = "text-body-md text-on-surface-variant") {
            t.GuideSignUpClaim
            " "
            a(href = "/claim", class' = "fr-link") { t.HaveAccountLink }
        }

        figures t [ "signup" ]
    }

let private connect (t: Texts) =
    div(class' = "space-y-5") {
        steps t.GuideConnectSteps
        figures t [ "account-extension"; "sync-key"; "extension-options" ]
    }

let private sync (t: Texts) =
    div(class' = "space-y-5") {
        steps t.GuideSyncSteps

        p(class' = "flex max-w-prose items-start gap-2 text-body-md text-on-surface-variant") {
            span(class' = "text-primary") { icon Theme.Info }
            span() { t.GuideSyncNote }
        }

        figures t [ "extension-ready"; "extension-scraping"; "extension-synced" ]
    }

let private dashboard (t: Texts) =
    div(class' = "space-y-5") {
        paragraphs t.GuideDashboardBody
        figures t [ "dashboard"; "dna" ]
    }

let private visibility (t: Texts) =
    div(class' = "space-y-5") {
        paragraphs t.GuideVisibilityBody
        figures t [ "account-visibility" ]
    }

let private delete (t: Texts) =
    div(class' = "space-y-5") { paragraphs t.GuideDeleteBody }

/// The sections in order: anchor, title and content
let private sections (t: Texts) =
    [ "install", t.GuideInstallTitle, (install t :> HtmlElement)
      "sign-up", t.GuideSignUpTitle, (signUp t :> HtmlElement)
      "connect", t.GuideConnectTitle, (connect t :> HtmlElement)
      "sync", t.GuideSyncTitle, (sync t :> HtmlElement)
      "dashboard", t.GuideDashboardTitle, (dashboard t :> HtmlElement)
      "visibility", t.GuideVisibilityTitle, (visibility t :> HtmlElement)
      "delete", t.GuideDeleteTitle, (delete t :> HtmlElement) ]

let private contents (t: Texts) (all: (string * string * HtmlElement) list) =
    nav(class' = "fr-card space-y-3").attr("aria-labelledby", "guide-contents-title").data("guide-contents", "") {
        h2(id = "guide-contents-title", class' = "text-headline-md text-on-surface") { t.GuideContentsTitle }

        ol(class' = "space-y-1") {
            for id', title, _ in all do
                li() { a(href = $"#{id'}", class' = "fr-link") { title } }
        }
    }

let view (ctx: HttpContext) (t: Texts) =
    let all = sections t

    div(class' = "mx-auto w-full max-w-3xl space-y-10") {
        pageHeading t.GuideTitle (Some t.GuideIntro)
        contents t all

        div(class' = "space-y-16") {
            for id', title, content in all do
                guideSection id' title content
        }
    }
    |> Layout.page ctx t t.GuideTitle

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx -> ctx.WriteHtmlView(view ctx (Texts.current ()))

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/guide" handler ]
