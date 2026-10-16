module FrunchyBackend.Handlers.Charts.Charts

open System
open System.Net
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Charts
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (specs/012-charts-trends/contracts/charts.md). No person's identity appears on a chart: the
// only user-specific thing is the viewer's own link on a series they watched.
// ---------------------------------------------------------------------------

let private windowName (window: Window) =
    match window with
    | Week -> "week"
    | Season -> "season"
    | AllTime -> "all"

let private tabs (t: Texts) (current: Window) =
    nav(class' = "flex flex-wrap items-center gap-2").attr("aria-label", t.ChartsTitle).data("chart-tabs", "") {
        for window, path, label in
            [ Week, "/charts", t.ChartsTabWeek
              Season, "/charts/season", t.ChartsTabSeason
              AllTime, "/charts/all", t.ChartsTabAll ] do
            let link =
                a(href = path, class' = (if window = current then "fr-pill fr-pill-primary" else "fr-pill fr-pill-muted hover:text-on-surface"))
                    .data("chart-tab", windowName window)

            if window = current then link.attr("aria-current", "page") { label } else link { label }
    }

// --- the trend line: an inline SVG built only from integers we compute (no text from anyone enters it) ---

/// The 8 weekly values as the points of one polyline in an 80 x 24 box, oldest on the left, scaled to the
/// row's own maximum. All zero is a flat line at the bottom.
let private trendPoints(values: int list) : string =
    let top = max 1 (List.max values)
    let any = values |> List.exists (fun v -> v > 0)
    let last = max 1 (values.Length - 1)

    values
    |> List.mapi (fun i v ->
        let x = (i * 76 + last / 2) / last + 2
        let y = if any then 22 - (v * 20 + top / 2) / top else 22
        $"{x},{y}")
    |> String.concat " "

let private trendSvg (t: Texts) (values: int list) : HtmlElement =
    // The only text is the localized label, HTML-encoded; every other part is an integer
    let label = WebUtility.HtmlEncode(t.ChartsTrendLabel values)
    let data = values |> List.map string |> String.concat ","

    span(class' = "block w-16 shrink-0 text-primary sm:w-24") {
        raw
            $"""<svg viewBox="0 0 80 24" class="block h-6 w-full" fill="none" role="img" aria-label="{label}" data-trend="{data}"><polyline points="{trendPoints values}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>"""
    }

let private movement (t: Texts) (move: Move) : HtmlElement =
    let line (kind: string) (by: int option) (tone: string) (i: Theme.Icon) (text: string) =
        let el =
            span(class' = $"inline-flex items-center gap-1 text-label-md font-semibold {tone}").data("move", kind)

        let el =
            match by with
            | Some n -> el.data("move-by", string n)
            | None -> el

        el {
            icon i
            text
        }

    match move with
    | Up n -> line "up" (Some n) "text-primary" Theme.TrendingUp (t.ChartsMoveUp n)
    | Down n -> line "down" (Some n) "text-on-surface-variant" Theme.TrendingDown (t.ChartsMoveDown n)
    | Same -> line "same" None "text-on-surface-variant" Theme.TrendingFlat t.ChartsMoveSame
    | New -> (pill TertiaryTone t.ChartsMoveNew).data("move", "new")

let private poster (p: Poster option) =
    span(class' = "flex w-12 shrink-0 justify-center sm:w-24") {
        match p with
        | Some(Portrait url) ->
            img(src = url, alt = "", class' = "aspect-[2/3] w-10 rounded-md object-cover sm:w-12")
                .attr("loading", "lazy")
                .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
        | Some(Thumbnail url) ->
            img(src = url, alt = "", class' = "aspect-video w-12 rounded-md object-cover sm:w-24")
                .attr("loading", "lazy")
                .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
        | None -> Fragment()
    }

let private row (t: Texts) (username: string option) (top: int) (rank: int) (r: ChartRow) =
    let share = if top = 0 then 0m else 100m * decimal r.People / decimal top
    let titleClass = "min-w-0 text-body-lg font-semibold text-on-surface [overflow-wrap:anywhere]"
    let rankTone = if rank <= 3 then "text-primary" else "text-on-surface-variant"

    li(class' = "flex items-center gap-3 py-4 sm:gap-4")
        .data("chart-row", r.SeriesId)
        .data("rank", string rank)
        .data("people", string r.People)
        .data("episodes", string r.Episodes) {
        span(class' = $"w-8 shrink-0 text-right font-mono text-headline-md {rankTone}") { string rank }

        poster r.Poster

        div(class' = "min-w-0 flex-1 space-y-1.5") {
            div(class' = "flex flex-wrap items-center gap-x-3 gap-y-1") {
                match username with
                | Some name when r.Mine ->
                    a(href = $"/u/{name}/anime/{r.SeriesId}", class' = titleClass + " hover:underline").data("chart-mine", "") { r.Title }
                | _ -> span(class' = titleClass) { r.Title }

                match r.Move with
                | Some move -> movement t move
                | None -> Fragment()
            }

            p(class' = "text-label-md font-normal text-on-surface-variant") { t.ChartsPeopleEpisodes r.People r.Episodes }

            toneBar PrimaryBar share

            match r.Slug with
            | Some slug ->
                a(href = $"https://www.crunchyroll.com/series/{r.SeriesId}/{slug}", target = "_blank", class' = "fr-link inline-flex min-h-6 items-center gap-1 text-label-md")
                    .attr("rel", "noopener")
                    .data("cr-series-link", "") {
                    t.EpisodeWatchOnCrunchyroll
                    icon Theme.OpenInNew
                }
            | None -> Fragment()
        }

        if r.Move.IsSome then
            trendSvg t r.Trend
    }

let private view (ctx: HttpContext) (t: Texts) (c: Chart) =
    let username = WebAuth.currentUsername ctx
    let top = c.Rows |> List.tryHead |> Option.map _.People |> Option.defaultValue 0

    div(class' = "space-y-8").data("charts", windowName c.Window).data("min-people", string c.MinPeople) {
        div(class' = "space-y-4") {
            h1(class' = "text-display-hero-mobile md:text-display-hero text-on-surface text-balance") { t.ChartsTitle }
            tabs t c.Window

            match c.Season with
            | Some(name, year) -> h2(class' = "text-headline-md text-on-surface").data("season", $"{name}-{year}") { t.DnaSeason name year }
            | None -> Fragment()

            div(class' = "space-y-1 text-label-md font-normal text-on-surface-variant") {
                p(class' = "flex items-start gap-1.5") {
                    icon Theme.Info
                    span() { t.ChartsLatestNote }
                }

                p(class' = "pl-6") { t.ChartsMinNote c.MinPeople }
            }
        }

        if c.Rows.IsEmpty then
            p(class' = "fr-card text-body-lg text-on-surface-variant").data("chart-empty", "") { t.ChartsEmpty }
        else
            ol(class' = "divide-y divide-outline-variant/40") {
                for i, r in List.indexed c.Rows do
                    row t username top (i + 1) r
            }
    }
    |> Layout.page ctx t t.ChartsTitle

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let private chartHandler (window: Window) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()
            let minimum = minPeople (ctx.GetService<IConfiguration>())
            let now = ctx.GetService<TimeProvider>().GetUtcNow()
            let! c = chart (ctx.GetService<DbConfig>()) window minimum now (WebAuth.currentUserId ctx)
            return! ctx.WriteHtmlView(view ctx t c)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/charts" (chartHandler Week) ]
      GET [ route "/charts/season" (chartHandler Season) ]
      GET [ route "/charts/all" (chartHandler AllTime) ] ]
