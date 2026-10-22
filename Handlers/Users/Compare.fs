module FrunchyBackend.Handlers.Users.Compare

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (specs/011-taste-compatibility/contracts/compatibility.md). No score and no percentage of the level
// is printed anywhere, not even in an attribute: only the level, its meter and the sections.
// ---------------------------------------------------------------------------

/// How many shared series the page lists before "and N more"
let private sharedShown = 10

let private shareText (t: Texts) (share: decimal) = share.ToString("0.0", t.Culture) + "%"

/// Matched share of a person's deduplicated watch time (the DNA page's coverage), one decimal
let private coverage (p: Compatibility.Person) =
    if p.DedupMs = 0L then
        0m
    else
        Math.Round(100m * decimal p.MatchedMs / decimal p.DedupMs, 1, MidpointRounding.AwayFromZero)

/// One of the two people: name (amber for the viewer, rose for the owner), username in mono, series count
let private personBlock (t: Texts) (role: string) (person: Compatibility.Person) =
    let viewer = role = "viewer"

    div(class' = "min-w-0 space-y-1.5").data("person", role) {
        div(class' = "flex flex-wrap items-center gap-2") {
            p(class' = (if viewer then "text-headline-md text-primary [overflow-wrap:anywhere]" else "text-headline-md text-secondary [overflow-wrap:anywhere]")) {
                person.DisplayName
            }

            if viewer then
                pill PrimaryTone t.CompatYou
        }

        p(class' = "font-mono text-label-md text-on-surface-variant [overflow-wrap:anywhere]") { "@" + person.Username }

        p(class' = "text-body-md text-on-surface-variant").data("series-count", string person.Series.Count) {
            t.CompatSeriesCount person.Series.Count
        }
    }

let private heading (t: Texts) (c: Compatibility.Comparison) (level: int) =
    let owner = c.Owner

    section(class' = "fr-card flex flex-col gap-6 p-5 md:p-6").attr("aria-labelledby", "compare-title") {
        h1(id = "compare-title", class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance [overflow-wrap:anywhere]")
            .data("compare-summary", "") {
            t.CompatSummary owner.DisplayName c.Shared.Length (List.tryHead c.SharedGenres)
        }

        div(class' = "space-y-3") {
            div(class' = "flex flex-wrap items-center gap-x-4 gap-y-2") {
                levelMeter t level
                span(class' = "text-headline-md text-primary").data("level-name", string level) { t.CompatLevel level }
            }

            p(class' = "max-w-prose text-body-md text-on-surface-variant") { t.CompatExplain }

            if not c.GenresCompared then
                coverageNote t.CompatNoGenres
        }

        div(class' = "grid grid-cols-1 gap-4 sm:grid-cols-2") {
            personBlock t "viewer" c.Viewer
            personBlock t "owner" owner
        }
    }

/// One person's bar and share in a genre row; the name is read out, the colour is not the only cue
let private barLine (t: Texts) (tone: BarTone) (name: string) (share: decimal) =
    div(class' = "flex items-center gap-3") {
        div(class' = "min-w-0 flex-1") { toneBar tone share }

        span(class' = "w-14 shrink-0 text-right text-label-md text-on-surface tabular-nums") {
            span(class' = "sr-only") { name + ": " }
            shareText t share
        }
    }

let private legendDot (colour: string) (label: string) =
    span(class' = "inline-flex min-w-0 items-center gap-2") {
        span(class' = $"block h-2.5 w-2.5 shrink-0 rounded-full {colour}").attr("aria-hidden", "true")
        span(class' = "[overflow-wrap:anywhere]") { label }
    }

let private genresSection (t: Texts) (c: Compatibility.Comparison) =
    let owner = c.Owner.DisplayName

    section(class' = "fr-card flex flex-col gap-5 p-5 md:p-6 lg:col-span-7")
        .data("compare-genres", "")
        .attr("aria-label", t.CompatTimeTitle) {
        sectionHeading t.CompatTimeTitle

        if not c.GenreRows.IsEmpty then
            div(class' = "flex flex-wrap gap-x-5 gap-y-1 text-label-md font-normal text-on-surface-variant") {
                legendDot "bg-primary" t.CompatYou
                legendDot "bg-secondary" owner
            }

            div(class' = "space-y-5") {
                for g in c.GenreRows do
                    div(class' = "space-y-2")
                        .data("genre-compare", g.Name)
                        .data("viewer-share", inv g.ViewerShare)
                        .data("owner-share", inv g.OwnerShare) {
                        p(class' = "text-body-md font-semibold text-on-surface [overflow-wrap:anywhere]") { g.Name }
                        barLine t PrimaryBar t.CompatYou g.ViewerShare
                        barLine t SecondaryBar owner g.OwnerShare
                    }
            }

        div(class' = "space-y-2") {
            (coverageNote (t.CompatCoverage c.Viewer.DisplayName (coverage c.Viewer))).data("viewer-coverage", inv (coverage c.Viewer))
            (coverageNote (t.CompatCoverage owner (coverage c.Owner))).data("owner-coverage", inv (coverage c.Owner))
        }
    }

let private sharedSection (t: Texts) (c: Compatibility.Comparison) =
    let owner = c.Owner
    let count = c.Shared.Length
    let more = count - sharedShown

    section(class' = "fr-card flex flex-col gap-4 p-5 md:p-6 lg:col-span-5")
        .data("compare-shared", "")
        .attr("aria-label", t.CompatSharedTitle) {
        div(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1") {
            sectionHeading t.CompatSharedTitle

            let total = span(class' = "text-label-md text-on-surface-variant").data("shared-count", string count)
            (if count = 0 then total.data("shared-none", "") else total) { t.CompatSharedCount count }
        }

        if count > 0 then
            // A long list scrolls in its own box, which a keyboard can reach
            div(class' = "max-h-[30rem] overflow-y-auto")
                .attr("tabindex", "0")
                .attr("role", "region")
                .attr("aria-label", t.CompatSharedTitle) {
                ul(class' = "divide-y divide-outline-variant/40") {
                    for s in c.Shared |> List.truncate sharedShown do
                        li(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-3")
                            .data("shared-series", s.SeriesId)
                            .data("viewer-episodes", string s.ViewerEpisodes)
                            .data("owner-episodes", string s.OwnerEpisodes) {
                            div(class' = "min-w-0 flex-1 basis-40 text-body-md font-semibold [overflow-wrap:anywhere]") {
                                seriesLink owner.Username s.SeriesId s.Title
                            }

                            div(class' = "flex flex-wrap gap-x-4 gap-y-1 text-label-md font-normal text-on-surface-variant") {
                                span() {
                                    span(class' = "text-primary") { t.CompatYou }
                                    " "
                                    t.TopGenresEpisodes s.ViewerEpisodes
                                }

                                span() {
                                    span(class' = "text-secondary [overflow-wrap:anywhere]") { owner.DisplayName }
                                    " "
                                    t.TopGenresEpisodes s.OwnerEpisodes
                                }
                            }
                        }
                }
            }

            if more > 0 then
                p(class' = "text-label-md text-on-surface-variant").data("shared-more", string more) { t.CompatMore more }
    }

let private theirsSection (t: Texts) (c: Compatibility.Comparison) =
    let owner = c.Owner

    section(class' = "fr-card flex flex-col gap-4 p-5 md:p-6 lg:col-span-7")
        .data("compare-theirs", "")
        .attr("aria-label", t.CompatTheirsTitle owner.DisplayName) {
        sectionHeading (t.CompatTheirsTitle owner.DisplayName)

        ul(class' = "divide-y divide-outline-variant/40") {
            for s in c.Theirs do
                let item = li(class' = "space-y-2 py-3").data("their-series", s.SeriesId)

                let item =
                    match s.MalScore with
                    | Some score -> item.data("mal-score", score.ToString("0.00", CultureInfo.InvariantCulture))
                    | None -> item

                item {
                    div(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1") {
                        div(class' = "min-w-0 flex-1 basis-40 text-body-md font-semibold [overflow-wrap:anywhere]") {
                            seriesLink owner.Username s.SeriesId s.Title
                        }

                        span(class' = "text-label-md font-normal text-on-surface-variant") { t.TopGenresEpisodes s.Episodes }
                    }

                    if s.MalScore.IsSome || not s.Genres.IsEmpty then
                        div(class' = "flex flex-wrap items-center gap-1.5") {
                            match s.MalScore with
                            | Some score ->
                                (pill Muted ("★ " + score.ToString("N2", t.Culture)))
                                    .attr("role", "group")
                                    .attr("aria-label", t.ScoreLabel)
                            | None -> Fragment()

                            for genre in s.Genres do
                                pill TertiaryTone genre
                        }
                }
        }
    }

let private studiosSection (t: Texts) (c: Compatibility.Comparison) (span': string) =
    section(class' = $"fr-card flex flex-col gap-4 p-5 md:p-6 {span'}")
        .data("compare-studios", "")
        .attr("aria-label", t.CompatStudiosTitle) {
        sectionHeading t.CompatStudiosTitle

        ul(class' = "divide-y divide-outline-variant/40") {
            for s in c.Studios do
                li(class' = "flex flex-wrap items-center justify-between gap-x-4 gap-y-1 py-3")
                    .data("shared-studio", s.Name)
                    .data("studio-series", string s.Series) {
                    pill TertiaryTone s.Name
                    span(class' = "text-label-md font-normal text-on-surface-variant") { t.CompatSeriesCount s.Series }
                }
        }
    }

let view (ctx: HttpContext) (t: Texts) (c: Compatibility.Comparison) =
    let owner = c.Owner

    let content =
        match c.Level with
        | Some level ->
            div(class' = "space-y-8").data("compare", owner.Username).data("level", string level) {
                profileTop t ProfileTab.NoTab owner.Username owner.DisplayName None
                heading t c level

                div(class' = "grid grid-cols-1 gap-6 lg:grid-cols-12") {
                    genresSection t c
                    sharedSection t c

                    if not c.Theirs.IsEmpty then
                        theirsSection t c

                    if not c.Studios.IsEmpty then
                        studiosSection t c (if c.Theirs.IsEmpty then "lg:col-span-12" else "lg:col-span-5")
                }
            }
        | None ->
            let who, text =
                match c.Enough with
                | Compatibility.ViewerShort -> "viewer", t.CompatNotEnoughViewer
                | Compatibility.OwnerShort -> "owner", t.CompatNotEnoughOwner owner.DisplayName
                | _ -> "both", t.CompatNotEnoughBoth owner.DisplayName

            div(class' = "space-y-8").data("compare", owner.Username).data("level", "") {
                profileTop t ProfileTab.NoTab owner.Username owner.DisplayName None
                (emptyPageState Theme.Info (t.CompatPageTitle owner.DisplayName) text None).data("compat-not-enough", who)
            }

    content |> Layout.page ctx t (t.CompatPageTitle owner.DisplayName)

// ---------------------------------------------------------------------------
// Data access: the owner's id (research R5, step 3); everything else is Compatibility.load
// ---------------------------------------------------------------------------

let private tryFindUser (db: DbConfig) (username: string) : Task<Guid option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd = new NpgsqlCommand("SELECT id FROM users WHERE username = @username", connection)
        cmd.Parameters.AddWithValue("username", username) |> ignore

        let! found = cmd.ExecuteScalarAsync()

        return
            match found with
            | :? Guid as id -> Some id
            | _ -> None
    }

// ---------------------------------------------------------------------------
// Handler (research R5): signed in, then the username, then the lookup, then self, then load
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) (t: Texts) =
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)

/// /u/{username}/compare
let handler (rawUsername: string) : EndpointHandler =
    WebAuth.requireSignedIn (fun viewerId ctx ->
        task {
            let t = Texts.current ()

            match Username.tryCreate rawUsername with
            | None -> return! notFound ctx t
            | Some username ->
                let db = ctx.GetService<DbConfig>()

                match! tryFindUser db username with
                | None -> return! notFound ctx t
                | Some ownerId ->
                    // Feature 017: a profile the viewer can't see is the unknown-user 404, before anything else
                    match! Profiles.visibleTo ctx db ownerId with
                    | false -> return! notFound ctx t
                    | true when ownerId = viewerId ->
                        // Comparing yourself with yourself has nothing to say: back to your own dashboard
                        ctx.Response.Redirect $"/u/{username}"
                        return! Task.CompletedTask
                    | true ->
                        let! comparison = Compatibility.load db viewerId ownerId
                        return! ctx.WriteHtmlView(view ctx t comparison)
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}/compare" handler ]
