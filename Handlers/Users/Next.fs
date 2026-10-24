module FrunchyBackend.Handlers.Users.Next

open System.Globalization
open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (specs/025-what-to-watch-next). No score and no percentage of the level is printed anywhere, not even in
// an attribute: only the level in words and the lists.
// ---------------------------------------------------------------------------

/// One named person: their display name links to their dashboard, the level in words follows (and the comma before
/// the next name, so it never wraps away from its name)
let private person (t: Texts) (f: Recommendations.Finisher) (comma: bool) =
    span(class' = "inline-flex min-w-0 flex-wrap items-baseline gap-x-1.5") {
        a(href = $"/u/{f.Username}", class' = "fr-link [overflow-wrap:anywhere]")
            .data("next-person", f.Username)
            .data("level", string f.Level) {
            f.DisplayName
        }

        span(class' = "text-label-md font-normal text-on-surface-variant") {
            "(" + t.CompatLevel f.Level + ")" + (if comma then "," else "")
        }
    }

let private seriesItem (t: Texts) (r: Recommendations.Recommendation) =
    let item =
        li(class' = "space-y-2 py-3").data("next-series", r.SeriesId).data("finisher-count", string r.Finishers.Length)

    let item =
        match r.MalScore with
        | Some score -> item.data("mal-score", score.ToString("0.00", CultureInfo.InvariantCulture))
        | None -> item

    let shown = r.Finishers |> List.truncate Recommendations.shownFinishers
    let more = r.Finishers.Length - shown.Length

    item {
        div(class' = "min-w-0 text-body-md font-semibold [overflow-wrap:anywhere]") {
            a(href = $"/anime/{r.SeriesId}", class' = "fr-link").data("series-page-link", "") { r.Title }
        }

        if r.MalScore.IsSome || not r.Genres.IsEmpty then
            div(class' = "flex flex-wrap items-center gap-1.5") {
                match r.MalScore with
                | Some score ->
                    (pill Muted ("★ " + score.ToString("N2", t.Culture)))
                        .attr("role", "group")
                        .attr("aria-label", t.ScoreLabel)
                | None -> Fragment()

                for genre in r.Genres do
                    pill TertiaryTone genre
            }

        p(class' = "flex flex-wrap items-baseline gap-x-2 gap-y-1 text-label-md font-normal text-on-surface-variant") {
            span() { t.NextWatchedBy }

            for i, f in List.indexed shown do
                person t f (i < shown.Length - 1)

            if more > 0 then
                span().data("next-more", string more) { t.CompatMore more }
        }
    }

let private emptyState (t: Texts) (kind: string) (text: string) =
    div(class' = "space-y-8").data("next", "").data("match-count", "0") {
        (emptyPageState Theme.Info t.NextTitle text None).data("next-empty", kind)
    }

let view (ctx: HttpContext) (t: Texts) (result: Recommendations.Result) =
    let content =
        match result with
        | Recommendations.NotEnough -> emptyState t "not-enough" t.NextNotEnough
        | Recommendations.NoMatches -> emptyState t "no-matches" t.NextNoMatches
        | Recommendations.NoSeries count ->
            div(class' = "space-y-8").data("next", "").data("match-count", string count) {
                (emptyPageState Theme.Info t.NextTitle t.NextNoSeries None).data("next-empty", "no-series")
            }
        | Recommendations.Series(count, series) ->
            div(class' = "space-y-8").data("next", "").data("match-count", string count) {
                section(class' = "space-y-3") {
                    h1(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance") { t.NextTitle }
                    p(class' = "max-w-prose text-body-md text-on-surface-variant") { t.NextIntro }
                    p(class' = "text-label-md text-on-surface-variant") { t.NextMatchCount count }
                }

                section(class' = "fr-card flex flex-col gap-4 p-5 md:p-6").attr("aria-label", t.NextListTitle) {
                    sectionHeading t.NextListTitle

                    ul(class' = "divide-y divide-outline-variant/40") {
                        for r in series do
                            seriesItem t r
                    }
                }
            }

    content |> Layout.page ctx t t.NextTitle

// ---------------------------------------------------------------------------
// Handler: signed in (signed out goes to /signin and back), then one bounded computation
// ---------------------------------------------------------------------------

/// /next
let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun viewerId ctx ->
        task {
            let t = Texts.current ()
            let db = ctx.GetService<DbConfig>()
            let settings = ctx.GetService<Profiles.ProfileSettings>()
            let! result = Recommendations.load db settings (Profiles.readGrants ctx) viewerId
            return! ctx.WriteHtmlView(view ctx t result)
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/next" handler ]
