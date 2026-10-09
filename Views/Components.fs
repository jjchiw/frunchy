module FrunchyBackend.Views.Components

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

/// A labelled text input with an optional hint and error (aria-invalid / aria-describedby wired)
let field (label': string) (name: string) (value: string) (hint: string option) (error: string option) (mono: bool) =
    let hintId = $"{name}-hint"
    let errorId = $"{name}-error"

    let describedBy =
        [ if hint.IsSome then hintId
          if error.IsSome then errorId ]
        |> String.concat " "

    let input' =
        input(type' = "text", id = name, name = name, value = value, class' = (if mono then "fr-input fr-mono" else "fr-input"))

    if describedBy <> "" then
        input'.attr("aria-describedby", describedBy) |> ignore

    if error.IsSome then
        input'.attr("aria-invalid", "true") |> ignore

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

/// A narrow centred page column (sign-in, forms, confirmations)
let narrow () = div(class' = "mx-auto w-full max-w-lg space-y-6")

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
