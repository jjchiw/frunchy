module FrunchyBackend.Handlers.Users.Progress

open System
open System.Globalization
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.Progress
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Model (specs/031-progress/contracts/hooks.md)
// ---------------------------------------------------------------------------

/// An open group of Unfinished shows lists this many rows, the rest behind "Show all"; this many or fewer
/// unfinished shows in all are not grouped at all
let groupRows = 10

/// What only the owner gets: the lists. Visitors' models have `None`, so their view can't name a show.
type OwnerLists =
    { Watching: Entry list
      Unfinished: Entry list
      /// The `CompletedListed` most recently completed
      Completed: Entry list }

type ProgressModel =
    { Username: string
      DisplayName: string
      /// What the page says about who can see it (feature 017)
      Access: Profiles.PageAccess
      CompletedCount: int
      WatchingCount: int
      UnfinishedCount: int
      /// Started Crunchyroll seasons without MyAnimeList details: in no count
      Unmatched: int
      Owner: OwnerLists option }

/// Unfinished shows grouped by the UTC year of their last watch, newest year first, each group newest watch first
let groupByYear (entries: Entry list) : (int * Entry list) list =
    entries
    |> List.groupBy (fun e -> e.LastWatch.UtcDateTime.Year)
    |> List.sortByDescending fst

// ---------------------------------------------------------------------------
// View (specs/031-progress/design-direction.md)
// ---------------------------------------------------------------------------

let private n0 (t: Texts) (n: int) = n.ToString("N0", t.Culture)

let private header (ctx: HttpContext) (t: Texts) (m: ProgressModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}/progress"

    Fragment() {
        profileTop t ProfileTab.Progress m.Username m.DisplayName (Some(m.Access, address))

        div(class' = "space-y-2") {
            h1(class' = "text-headline-lg text-on-surface text-balance") { t.ProgressTitle }
            p(class' = "max-w-prose text-body-lg text-on-surface-variant") { t.ProgressIntro }
        }
    }

let private segment (hook: string) (colour: string) (count: int) (whole: int) : HtmlElement =
    if count = 0 then
        Fragment()
    else
        let width = (100m * decimal count / decimal whole).ToString("0.###", CultureInfo.InvariantCulture)
        div(class' = $"h-full {colour}").data("progress-bar-segment", hook).attr("style", $"width: {width}%%")

let private countCell (t: Texts) (hook: string) (colour: string) (label: string) (count: int) =
    div(class' = "min-w-0") {
        p(class' = $"font-mono text-stat-counter {colour}").data(hook, string count) { n0 t count }
        p(class' = "text-label-md font-normal text-on-surface-variant") { label }
    }

/// One card: the three figures in lifecycle order over one bar. The bar's track is Unfinished: only Completed and
/// Watching are drawn, as shares of every started show, so a caught-up person fills it. A visitor gets the line under
/// the bar saying the lists are the owner's.
let private countsCard (t: Texts) (m: ProgressModel) =
    let whole = m.CompletedCount + m.WatchingCount + m.UnfinishedCount

    section(class' = "fr-card flex flex-col gap-5").attr("aria-label", t.ProgressTitle) {
        div(class' = "grid grid-cols-3 gap-4") {
            countCell t "progress-completed" "text-primary" t.ProgressCompleted m.CompletedCount
            countCell t "progress-watching" "text-tertiary" t.ProgressWatching m.WatchingCount
            countCell t "progress-unfinished" "text-on-surface-variant" t.ProgressUnfinished m.UnfinishedCount
        }

        div(class' = "flex h-2 overflow-hidden rounded-full bg-surface-container-highest").attr("aria-hidden", "true") {
            segment "completed" "bg-primary" m.CompletedCount whole
            segment "watching" "bg-tertiary" m.WatchingCount whole
        }

        match m.Owner with
        | Some _ -> Fragment()
        | None ->
            p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant") {
                icon Theme.Lock
                span() { t.ProgressVisitorLine m.DisplayName }
            }
    }

let private unmatchedNote (t: Texts) (m: ProgressModel) : HtmlElement =
    if m.Unmatched = 0 then
        Fragment()
    else
        (coverageNote (t.ProgressUnmatched m.Unmatched)).data("progress-unmatched", string m.Unmatched)

let private crunchyrollSeries (e: Entry) =
    match e.Slug with
    | Some slug -> $"https://www.crunchyroll.com/series/{e.SeriesId}/{slug}"
    | None -> $"https://www.crunchyroll.com/series/{e.SeriesId}"

/// "Next: episode 5": the episode's Frunchy page when the catalog has it, else the series on Crunchyroll (a new tab)
let private nextButton (t: Texts) (e: Entry) : HtmlElement =
    match e.NextNumber with
    | None -> Fragment()
    | Some number ->
        let label () =
            Fragment() {
                span() { t.ProgressNext number }
                span(class' = "sr-only") { t.ProgressNextFor e.Title }
            }

        let cls = $"{btnSm Secondary} shrink-0 self-start ml-[3.25rem] sm:ml-0 sm:self-center"

        match e.NextEpisode with
        | Some(seriesId, episodeId) ->
            a(href = $"/anime/{seriesId}/episode/{episodeId}", class' = cls).data("next-episode", string number) {
                label ()
                icon Theme.ArrowForward
            }
        | None ->
            a(href = crunchyrollSeries e, target = "_blank", class' = cls)
                .attr("rel", "noopener")
                .data("next-episode", string number) {
                label ()
                icon Theme.OpenInNew
            }

/// The attributes every listed entry carries, on its `li`
let private entryHooks (e: Entry) (row: li) : li =
    let row =
        row.data("progress-entry", string e.MalId).data("entry-watched", string e.Watched)

    let row =
        match e.Total with
        | Some total -> row.data("entry-total", string total)
        | None -> row

    if e.LongRunner then row.data("entry-long-runner", "") else row

let private lastWatched (t: Texts) (e: Entry) =
    (Layout.timeElement t "relative-only" e.LastWatch.UtcDateTime).data("entry-last", "")

/// The title: the public series page, never into anybody's history
let private titleLink (e: Entry) =
    a(href = $"/anime/{e.SeriesId}", class' = "fr-link").data("series-page-link", e.SeriesId) { e.Title }

let private quietPoster (url: string option) (size: string) =
    // the micro border is always there, so the slot looks the same whether there is no poster or the image failed to load
    posterOn "bg-surface-container-lowest [border:var(--border-micro)]" url size

/// A Watching or Unfinished row: poster, title, "4 of 12 episodes · last watched …", a track when there is a total
/// to compare with, and the next-episode button
let private row (t: Texts) (e: Entry) =
    let progress =
        match e.Total with
        | Some total when not e.LongRunner -> Some(min 1m (decimal e.Watched / decimal total))
        | _ -> None

    entryHooks e (li(class' = "py-4")) {
        div(class' = "flex flex-col gap-3 sm:flex-row sm:items-center sm:gap-4") {
            div(class' = "flex min-w-0 flex-1 items-start gap-3") {
                quietPoster e.Poster "aspect-[2/3] w-10 shrink-0 rounded-sm"

                div(class' = "min-w-0 space-y-1") {
                    p(class' = "line-clamp-2 text-body-md font-semibold [overflow-wrap:anywhere]") { titleLink e }

                    // two fixed parts: the figures with the Airing or Long run pill, then "last watched …"; the
                    // middot between them shows only on one line (sm and up), never when they stack on a phone
                    div(class' = "flex flex-col gap-y-0.5 text-label-md font-normal text-on-surface-variant sm:flex-row sm:flex-wrap sm:items-center sm:gap-x-2") {
                        span(class' = "flex flex-wrap items-center gap-x-2 gap-y-1") {
                            span() {
                                match e.Total with
                                | Some total when not e.LongRunner -> t.ProgressMetaOf e.Watched total
                                | _ -> t.ProgressMetaEpisodes e.Watched
                            }

                            if e.LongRunner then
                                pill TertiaryTone t.ProgressLongRun
                            elif e.Total.IsNone && e.Airing then
                                pill Muted t.ProgressAiring
                        }

                        span(class' = "hidden sm:inline").attr("aria-hidden", "true") { "·" }

                        span() {
                            t.ProgressLastWatched
                            " "
                            lastWatched t e
                        }
                    }

                    match progress with
                    | Some share ->
                        div(class' = "fr-track mt-1.5 h-1 max-w-48").attr("aria-hidden", "true") {
                            div(class' = "fr-fill")
                                .attr("style", $"""transform: scaleX({share.ToString("0.###", CultureInfo.InvariantCulture)})""")
                        }
                    | None -> Fragment()
                }
            }

            nextButton t e
        }
    }

let private rows (t: Texts) (entries: Entry list) =
    ul(class' = "divide-y divide-outline-variant/40") {
        for e in entries do
            row t e
    }

/// A section heading with its count beside it
let private titled (title: string) (count: int) (t: Texts) =
    div(class' = "flex items-baseline gap-2") {
        h2(class' = "text-headline-md text-on-surface") { title }
        span(class' = "text-label-md font-normal text-on-surface-variant") { n0 t count }
    }

let private watchingSection (t: Texts) (shows: Entry list) =
    section(class' = "space-y-3").attr("aria-label", t.ProgressWatching) {
        div(class' = "space-y-1") {
            titled t.ProgressWatching shows.Length t
            p(class' = "text-body-md text-on-surface-variant") { t.ProgressWatchingHint }
        }

        match shows with
        | [] -> p(class' = "text-body-md text-on-surface-variant") { t.ProgressWatchingEmpty }
        | _ -> div().data("progress-list", "watching") { rows t shows }
    }

/// An open year shows its first rows, the rest behind a nested "Show all N" (every show stays in the page)
let private cappedRows (t: Texts) (shows: Entry list) =
    let first, rest = shows |> List.splitAt (min groupRows shows.Length)

    Fragment() {
        rows t first

        if not rest.IsEmpty then
            details() {
                summary(class' = $"{btnSm Ghost} -ml-3.5 cursor-pointer list-none [&::-webkit-details-marker]:hidden") {
                    t.ProgressShowAll shows.Length
                }

                rows t rest
            }
    }

/// The newest year starts open and capped, the others closed; a native <details>, no script
let private yearGroup (t: Texts) (isOpen: bool) (year: int, shows: Entry list) =
    let group =
        details(class' = "border-t border-outline-variant/50")
            .data("progress-year", string year)
            .data("progress-year-shows", string shows.Length)

    (if isOpen then group.attr("open", "") else group) {
        summary(class' = "flex cursor-pointer list-none flex-col items-start gap-0.5 py-3 [&::-webkit-details-marker]:hidden") {
            h3(class' = "text-headline-md text-on-surface") { string year }

            span(class' = "flex items-center gap-1 text-label-md font-normal text-on-surface-variant") {
                t.ProgressGroupCount shows.Length
                iconWith "fr-expand" Theme.ExpandMore
            }
        }

        div(class' = "pb-2") { if isOpen then cappedRows t shows else rows t shows }
    }

let private unfinishedSection (t: Texts) (shows: Entry list) =
    section(class' = "space-y-3").attr("aria-label", t.ProgressUnfinished) {
        div(class' = "space-y-1") {
            titled t.ProgressUnfinished shows.Length t
            p(class' = "text-body-md text-on-surface-variant") { t.ProgressUnfinishedHint }
        }

        match shows with
        | [] ->
            p(class' = "flex items-start gap-1.5 text-body-md text-on-surface-variant") {
                iconWith "text-primary" Theme.CheckCircle
                span() { t.ProgressCaughtUp }
            }
        | _ when shows.Length <= groupRows -> div().data("progress-list", "unfinished") { rows t shows }
        | _ ->
            div().data("progress-list", "unfinished") {
                for i, group in List.indexed (groupByYear shows) do
                    yearGroup t (i = 0) group
            }
    }

/// A completed show as a tile, like the DNA page's seasons: poster, title, "12 ep." and when it was last watched
let private completedTile (t: Texts) (e: Entry) =
    entryHooks e (li(class' = "flex min-w-0 flex-col gap-1.5")) {
        quietPoster e.Poster "aspect-[2/3] w-full rounded-md"

        p(class' = "line-clamp-2 text-label-md [overflow-wrap:anywhere]") { titleLink e }
        p(class' = "text-label-md font-normal text-on-surface-variant") { t.DnaShowEpisodes(int64 e.Watched) }
        p(class' = "text-label-md font-normal text-on-surface-variant") { lastWatched t e }
    }

let private completedSection (t: Texts) (m: ProgressModel) (shows: Entry list) =
    section(class' = "space-y-3").attr("aria-label", t.ProgressCompletedHeading) {
        div(class' = "space-y-1") {
            titled t.ProgressCompletedHeading m.CompletedCount t

            if m.CompletedCount > shows.Length then
                p(class' = "text-body-md text-on-surface-variant") { t.ProgressCompletedMore shows.Length m.CompletedCount }
        }

        ul(class' = "grid grid-cols-3 gap-3 sm:grid-cols-[repeat(auto-fill,minmax(6.5rem,1fr))]")
            .data("progress-list", "completed") {
            for e in shows do
                completedTile t e
        }
    }

let private empty (t: Texts) (m: ProgressModel) =
    div(class' = "fr-card").data("progress-empty", "") {
        p(class' = "text-body-md text-on-surface-variant") {
            match m.Owner with
            | Some _ -> t.ProgressEmptyOwner
            | None -> t.ProgressEmptyVisitor m.DisplayName
        }
    }

let view (ctx: HttpContext) (t: Texts) (m: ProgressModel) =
    let who = if m.Owner.IsSome then "owner" else "visitor"
    let started = m.CompletedCount + m.WatchingCount + m.UnfinishedCount

    div(class' = "space-y-8").data("progress", who) {
        header ctx t m

        if started = 0 then
            empty t m
            unmatchedNote t m
        else
            div(class' = "space-y-3") {
                countsCard t m
                unmatchedNote t m
            }

            match m.Owner with
            | Some lists ->
                watchingSection t lists.Watching
                unfinishedSection t lists.Unfinished

                if m.CompletedCount > 0 then
                    completedSection t m lists.Completed
            | None -> Fragment()
    }
    |> Layout.page ctx t $"{t.ProgressTitle} · {m.DisplayName}"

// ---------------------------------------------------------------------------
// Data access: the user, the visibility check, then `Progress.load` (one statement) and an F# fold
// ---------------------------------------------------------------------------

let private findUser (connection: NpgsqlConnection) (username: string) =
    task {
        use cmd = new NpgsqlCommand("SELECT id, display_name FROM users WHERE username = @username", connection)
        cmd.Parameters.AddWithValue("username", username) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        return if r.Read() then Some(r.GetGuid 0, r.GetString 1) else None
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (rawUsername: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()
            let now = ctx.GetService<TimeProvider>().GetUtcNow()

            let notFound () =
                ctx.SetStatusCode 404
                ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)

            match Username.tryCreate rawUsername with
            | None -> return! notFound ()
            | Some username ->
                let db = ctx.GetService<DbConfig>()
                use connection = getConnection db
                do! connection.OpenAsync()

                match! findUser connection username with
                | None -> return! notFound ()
                | Some(userId, displayName) ->
                    // The visibility check, right after the lookup and before any history query. A hidden profile
                    // answers exactly what an unknown user does.
                    match! Profiles.gate ctx db userId with
                    | Profiles.SignInFirst -> return! WebAuth.redirectToSignInHere ctx
                    | Profiles.NotFound -> return! notFound ()
                    | Profiles.Show access ->
                        let! loaded = Progress.load connection userId
                        let classified = Progress.classify now loaded.Entries

                        // Only the signed-in owner gets the lists. The page access can't tell: a public profile
                        // reads the same to its owner and to everyone else.
                        let isOwner = (WebAuth.currentUserId ctx = Some userId)

                        let model =
                            { Username = username
                              DisplayName = displayName
                              Access = access
                              CompletedCount = classified.CompletedCount
                              WatchingCount = classified.Watching.Length
                              UnfinishedCount = classified.Unfinished.Length
                              Unmatched = loaded.Unmatched
                              Owner =
                                if isOwner then
                                    Some
                                        { Watching = classified.Watching
                                          Unfinished = classified.Unfinished
                                          Completed = classified.Completed }
                                else
                                    None }

                        return! ctx.WriteHtmlView(view ctx t model)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}/progress" handler ]
