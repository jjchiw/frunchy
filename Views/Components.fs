module FrunchyBackend.Views.Components

open System.Globalization
open Oxpecker.ViewEngine
open FrunchyBackend.Views.Theme
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Shared components of the design system (contracts/design-system.md). Styles live in
// Theme.componentCss; these functions only put the markup and the accessibility attributes in place.
// Each takes the text it shows: views contain no literals. Pages add their data-* test hooks to the
// returned element with .data(...).
// ---------------------------------------------------------------------------

let icon (i: Icon) =
    span(class' = "material-symbols-outlined").attr("aria-hidden", "true") { i.Name }

/// The mark (an inline SVG) next to the wordmark
let brand (siteName: string) =
    a(href = "/", class' = "inline-flex items-center gap-2.5 rounded text-headline-md font-extrabold tracking-tight text-on-surface") {
        span(class' = "block h-8 w-8") { raw markSvg }
        siteName
    }

type ButtonKind =
    | Primary
    | Secondary
    | Ghost
    | Destructive

/// Classes for a <button> or a link styled as one
let btn (kind: ButtonKind) =
    match kind with
    | Primary -> "fr-btn fr-btn-primary"
    | Secondary -> "fr-btn fr-btn-secondary"
    | Ghost -> "fr-btn fr-btn-ghost"
    | Destructive -> "fr-btn fr-btn-destructive"

let btnSm kind = btn kind + " fr-btn-sm"

/// Provider sign-in buttons keep the provider's own colour (Discord blurple; Google on a neutral surface)
let providerBtn (provider: string) =
    match provider with
    | "discord" -> "fr-btn w-full text-white bg-[#5865F2] hover:bg-[#4752c4]"
    | _ -> btn Secondary + " w-full"

let card () = div(class' = "fr-card")

let sectionTitle (text: string) =
    h2(class' = "text-headline-md text-on-surface") { text }

/// A labelled figure: overline label above a big tabular value. The value element carries
/// data-stat and data-value (the dashboard's test hooks) and holds the display text directly.
let metricTile (label: string) (name: string) (invariantValue: string) (display: string) (caption: string option) =
    div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6") {
        p(class' = "fr-overline") { label }
        div(class' = "text-stat-counter text-on-surface").data("stat", name).data("value", invariantValue) { display }

        match caption with
        | Some c -> p(class' = "text-label-md font-normal text-on-surface-variant") { c }
        | None -> Fragment()
    }

type PillTone =
    | PrimaryTone
    | SecondaryTone
    | TertiaryTone
    | Muted

let pill (tone: PillTone) (text: string) =
    let toneClass =
        match tone with
        | PrimaryTone -> "fr-pill-primary"
        | SecondaryTone -> "fr-pill-secondary"
        | TertiaryTone -> "fr-pill-tertiary"
        | Muted -> "fr-pill-muted"

    span(class' = $"fr-pill {toneClass}") { text }

/// A share bar (genres, formats, decades): 6px track, secondary → primary fill
let shareBar (percent: decimal) =
    let scale = (max 0m (min 100m percent) / 100m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
    div(class' = "fr-track").attr("aria-hidden", "true") { div(class' = "fr-fill").attr("style", $"transform: scaleX({scale})") }

type AlertKind =
    | Success
    | Failure
    | Notice

let alert (kind: AlertKind) (text: string) =
    let cls, role, i =
        match kind with
        | Success -> "fr-alert fr-alert-success", "status", CheckCircle
        | Failure -> "fr-alert fr-alert-error", "alert", ErrorMark
        | Notice -> "fr-alert fr-alert-info", "status", Info

    div(class' = cls).attr("role", role) {
        span(class' = (if kind = Failure then "text-error" else "text-primary")) { icon i }
        span() { text }
    }

/// "Working on it" line: a spinner (static when motion is reduced) and the text
let processing (text: string) =
    div(class' = "fr-alert fr-alert-info items-center").attr("role", "status") {
        span(class' = "motion-safe:animate-spin text-secondary inline-flex") { icon ProgressActivity }
        span() { text }
    }

/// Empty state: icon, title, body, optional call to action
let emptyState (i: Icon) (title: string) (body: string) (action: HtmlElement option) =
    div(class' = "fr-card flex flex-col items-center gap-3 py-12 text-center") {
        span(class' = "flex h-12 w-12 items-center justify-center rounded-full bg-surface-container-high text-headline-md text-on-surface-variant") {
            icon i
        }

        h2(class' = "text-headline-md text-on-surface") { title }
        p(class' = "max-w-prose text-body-md text-on-surface-variant") { body }

        match action with
        | Some a -> div(class' = "mt-2") { a }
        | None -> Fragment()
    }

/// `field` with an input type ("text", "password"…), ids of other elements describing the input (put first
/// in aria-describedby) and the input's own extra attributes (required, autocomplete, Alpine…). `.attr`
/// appends, so `configure` must not set type, id, name, value, class, aria-describedby or aria-invalid again.
let fieldWith
    (inputType: string)
    (describedBy: string list)
    (configure: input -> unit)
    (label': string)
    (name: string)
    (value: string)
    (hint: string option)
    (error: string option)
    (mono: bool)
    =
    let hintId = $"{name}-hint"
    let errorId = $"{name}-error"

    let describedBy =
        [ yield! describedBy
          if hint.IsSome then hintId
          if error.IsSome then errorId ]
        |> String.concat " "

    let input' =
        input(type' = inputType, id = name, name = name, value = value, class' = (if mono then "fr-input fr-mono" else "fr-input"))

    if describedBy <> "" then
        input'.attr("aria-describedby", describedBy) |> ignore

    if error.IsSome then
        input'.attr("aria-invalid", "true") |> ignore

    configure input'

    div() {
        label(for' = name, class' = "fr-label") { label' }
        input'

        match hint with
        | Some h -> p(id = hintId, class' = "fr-hint") { h }
        | None -> Fragment()

        match error with
        | Some e ->
            p(id = errorId, class' = "fr-field-error") {
                icon ErrorMark
                span() { e }
            }
        | None -> Fragment()
    }

/// A labelled text input with an optional hint and error (aria-invalid / aria-describedby wired)
let field label' name value hint error mono = fieldWith "text" [] ignore label' name value hint error mono

/// A narrow centred page column (sign-in, forms, confirmations)
let narrow () = div(class' = "mx-auto w-full max-w-lg space-y-6")

/// A single card centred on the page (sign-in, sign-up, claim): top-aligned on phones, centred above
let centredCard (content: HtmlElement) =
    div(class' = "flex justify-center md:min-h-[60vh] md:items-center") {
        div(class' = "fr-card w-full max-w-lg space-y-6 shadow-level-2") { content }
    }

/// Page heading with an optional lead paragraph
let pageHeading (title: string) (lead: string option) =
    div(class' = "space-y-2") {
        h1(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance") { title }

        match lead with
        | Some l -> p(class' = "max-w-prose text-body-lg text-on-surface-variant") { l }
        | None -> Fragment()
    }

/// Copy a page's address (the dashboard and the DNA page). Falls back to a selectable field when the
/// Clipboard API is unavailable. What starts hidden carries an inline display:none, which Alpine's
/// x-show removes when it appears, so nothing flashes before Alpine loads.
let copyLink (t: Texts) (address: string) =
    div(class' = "flex flex-col items-start gap-2")
        .data("copy-link", "")
        .attr("x-data", "{ copied: false, failed: false }") {
        button(type' = "button", class' = btn Secondary)
            .attr(
                "x-on:click",
                "navigator.clipboard ? navigator.clipboard.writeText($el.dataset.address).then(() => { copied = true; setTimeout(() => copied = false, 2000) }, () => failed = true) : failed = true"
            )
            .data("address", address) {
            icon ContentCopy
            span().attr("x-show", "!copied") { t.CopyLink }
            span().attr("x-show", "copied").attr("style", "display: none") { t.LinkCopied }
        }

        span(class' = "sr-only").attr("aria-live", "polite").data("copy-done", "") {
            span().attr("x-show", "copied").attr("style", "display: none") { t.LinkCopied }
        }

        label(class' = "w-full").attr("x-show", "failed").attr("style", "display: none") {
            span(class' = "fr-label") { t.CopyLinkFallback }
            input(type' = "text", value = address, class' = "fr-input fr-mono").attr("readonly", "").attr("x-on:focus", "$el.select()")
        }
    }

// ---------------------------------------------------------------------------
// Figure pages (the Anime DNA and the year in review): sections, shares, coverage
// ---------------------------------------------------------------------------

let inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)
/// A share as shown: 1 decimal, and "<0.1%" for a real but tiny share (rows exist only with minutes)
let pctText (t: Texts) (d: decimal) =
    if d = 0m then "<" + (0.1m).ToString("0.0", t.Culture) + "%" else d.ToString("0.0", t.Culture) + "%"

let coverageNote (text: string) =
    p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant") {
        icon Theme.Info
        span() { text }
    }

let notEnough (t: Texts) =
    p(class' = "text-body-md text-on-surface-variant").data("not-enough", "") { t.DnaSectionNotEnough }

/// A titled card for one figure; MyAnimeList-based ones carry data-coverage
let figureSection (hook: string) (title: string) (coverage: decimal option) (extra: string) =
    let el =
        section(class' = $"fr-card flex flex-col gap-5 {extra}").data(hook, "")

    match coverage with
    | Some c -> el.data("coverage", inv c)
    | None -> el
    |> fun el -> el.attr("aria-label", title)

let sectionHeading (title: string) =
    h2(class' = "text-headline-md text-on-surface") { title }

let shareLine (name: string) (detail: HtmlElement option) (share: decimal) (t: Texts) =
    div(class' = "space-y-2") {
        div(class' = "flex items-baseline justify-between gap-4") {
            div(class' = "min-w-0") {
                span(class' = "text-body-md font-semibold text-on-surface") { name }

                match detail with
                | Some d -> span(class' = "ml-2 text-label-md font-normal text-on-surface-variant") { d }
                | None -> Fragment()
            }

            span(class' = "shrink-0 text-label-md text-on-surface tabular-nums") { pctText t share }
        }

        shareBar share
    }

/// A series title that leads to its anime page (feature 006)
let seriesLink (username: string) (seriesId: string) (title: string) =
    a(href = $"/u/{username}/anime/{seriesId}", class' = "fr-link").data("series-link", seriesId) { title }

/// Years with history as links, newest first; the shown year is marked for assistive tech
let yearPicker (label: string) (username: string) (years: int list) (current: int) =
    nav(class' = "flex flex-wrap items-center gap-2").attr("aria-label", label).data("year-picker", "") {
        for y in years do
            let link =
                a(href = $"/u/{username}/year/{y}",
                  class' = (if y = current then "fr-pill fr-pill-primary" else "fr-pill fr-pill-muted hover:text-on-surface"))
                    .data("year-option", string y)

            if y = current then link.attr("aria-current", "page") { string y } else link { string y }
    }

/// A season's name with its audio language and dub mark, shared by the anime page and the episode page
let seasonNameLine (t: Texts) (name: string) (audio: string option) (dub: bool) =
    div(class' = "flex flex-wrap items-center gap-x-3 gap-y-2") {
        h3(class' = "text-headline-md text-on-surface [overflow-wrap:anywhere]") { name }

        match audio with
        | Some audio -> pill (if dub then SecondaryTone else Muted) (t.AudioLanguage audio)
        | None -> Fragment()

        if dub then
            pill SecondaryTone t.AnimeDub
    }

/// A MyAnimeList entry shown as just its link (a season whose entry an earlier season already showed)
let malCompact (t: Texts) (malId: int) (url: string) =
    div().data("mal", string malId).data("mal-compact", "") {
        a(href = url, target = "_blank", class' = "fr-link inline-flex min-h-6 items-center gap-1 text-label-md")
            .attr("rel", "noopener")
            .data("mal-link", "") {
            t.MyAnimeListLink
            icon Theme.OpenInNew
        }
    }
