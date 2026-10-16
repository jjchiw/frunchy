module FrunchyBackend.Handlers.Users.Dashboard

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/dashboard-ui.md)
// ---------------------------------------------------------------------------

type Figures =
    { Series: int64
      Episodes: int64
      Hours: decimal
      Days: decimal }

/// MyAnimeList details of a matched season (feature 003, contracts/dashboard.md)
type MalDetails =
    { Url: string
      Score: decimal option
      /// Up to 3 genre names in MyAnimeList's order, already joined with " / "
      Genres: string option
      /// MyAnimeList's first studio
      Studio: string option
      /// MyAnimeList's media type (TV, Movie, OVA, …), shown as MyAnimeList names it
      Format: string option
      /// Release year (MyAnimeList's season year, else the year it started airing)
      Year: int option }

type RecentEpisode =
    { SeriesId: string
      SeriesTitle: string
      EpisodeNumber: int option
      DurationMs: int
      ThumbnailUrl: string option
      WatchedAt: DateTime
      FullyWatched: bool
      PlayheadS: int
      Mal: MalDetails option }

/// "Top genres": fully watched episodes in matched seasons, per MyAnimeList genre (FR-018a)
type TopGenres =
    { Genres: (string * int64) list
      MatchedEpisodes: int64
      AllEpisodes: int64 }

type DashboardModel =
    { Username: string
      DisplayName: string
      LastSyncAt: DateTime option
      SyncInProgress: bool
      Figures: Figures option // None when nothing resolved has been watched yet
      TopGenres: TopGenres option // None when no fully watched episode is in a matched season
      Recent: RecentEpisode list }

let private timeElement = Layout.timeElement

let private stats (t: Texts) (f: Figures) =
    let inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)

    div(class' = "grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4") {
        metricTile t.StatSeries "series" (string f.Series) (f.Series.ToString("N0", t.Culture)) None
        metricTile t.StatEpisodes "episodes" (string f.Episodes) (f.Episodes.ToString("N0", t.Culture)) None
        metricTile t.StatHours "hours" (inv f.Hours) (f.Hours.ToString("N1", t.Culture)) None
        metricTile t.StatDays "days" (inv f.Days) (f.Days.ToString("N1", t.Culture)) None
    }

/// How far a partly watched episode got, 0–100
let private watchedPercent (e: RecentEpisode) =
    if e.DurationMs <= 0 then
        0
    else
        int (Math.Round(min 100m (100m * decimal e.PlayheadS * 1000m / decimal e.DurationMs), MidpointRounding.AwayFromZero))

/// The matched anime: studio, format, year and score as pills; then the genres and the MyAnimeList link.
/// `cell` places both rows in the card's grid.
let private malDetails (t: Texts) (cell: string) (mal: MalDetails) =
    Fragment() {
        div(class' = $"flex flex-wrap items-center gap-1.5 {cell}") {
            match mal.Studio with
            | Some s -> (pill Muted s).data("mal-studio", "")
            | None -> Fragment()

            match mal.Format with
            | Some f -> (pill Muted f).data("mal-format", "")
            | None -> Fragment()

            match mal.Year with
            | Some y -> (pill Muted (string y)).data("mal-year", "")
            | None -> Fragment()

            match mal.Score with
            | Some score ->
                (pill TertiaryTone ("★ " + score.ToString("N2", t.Culture)))
                    .data("mal-score", score.ToString("0.00", CultureInfo.InvariantCulture))
                    .attr("role", "group")
                    .attr("aria-label", t.ScoreLabel)
            | None -> Fragment()
        }

        div(class' = $"flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 text-body-md {cell}") {
            match mal.Genres with
            | Some genres -> span(class' = "min-w-0 text-on-surface-variant").data("mal-genres", "") { genres }
            | None -> Fragment()

            a(href = mal.Url, target = "_blank", class' = "fr-link inline-flex min-h-6 items-center gap-1 text-label-md")
                .attr("rel", "noopener")
                .data("mal-link", "") {
                t.MyAnimeListLink
                icon Theme.OpenInNew
            }
        }
    }

/// A recent card. With a thumbnail it's a 2-column grid: on phones a small thumbnail sits beside the title
/// and the other rows span the card; from `sm` the thumbnail is a column of its own, spanning every row.
let private recentItem (t: Texts) (username: string) (e: RecentEpisode) =
    let rows = 3 + (if e.FullyWatched then 0 else 1) + (if e.Mal.IsSome then 1 else 0)

    let grid, cell =
        match e.ThumbnailUrl with
        | Some _ -> "grid-cols-[6rem_minmax(0,1fr)] sm:grid-cols-[11rem_minmax(0,1fr)]", "col-span-2 sm:col-span-1 sm:col-start-2"
        | None -> "grid-cols-1", ""

    // Literal class names, so Tailwind finds them
    let thumbRows =
        match rows with
        | 3 -> "sm:row-span-3"
        | 4 -> "sm:row-span-4"
        | _ -> "sm:row-span-5"

    // content-start: a card stretched to its neighbour's height keeps its rows together at the top
    article(class' = $"fr-card grid content-start items-start gap-x-4 gap-y-3 p-4 md:p-4 {grid}").attr("data-recent-item", "") {
        match e.ThumbnailUrl with
        | Some url ->
            // Decorative: the title is right next to it
            img(src = url, alt = "", class' = $"aspect-video w-full rounded-md bg-surface-container object-cover {thumbRows}")
                .attr("loading", "lazy")
        | None -> Fragment()

        div(class' = "min-w-0 space-y-1") {
            h3(class' = "text-headline-md text-on-surface [overflow-wrap:anywhere] [&_a]:no-underline [&_a:hover]:underline") { seriesLink username e.SeriesId e.SeriesTitle }

            p(class' = "text-body-md text-on-surface-variant") {
                (match e.EpisodeNumber with
                 | Some n -> t.EpisodeLabel n
                 | None -> t.SpecialLabel)
                + " · "
                + t.Minutes(int (Math.Round(float e.DurationMs / 60000.0)))
            }
        }

        if not e.FullyWatched then
            let watched = watchedPercent e

            div(class' = $"flex flex-wrap items-center gap-x-3 gap-y-2 {cell}") {
                pill PrimaryTone t.InProgress
                div(class' = "min-w-16 flex-1") { shareBar (decimal watched) }
                span(class' = "shrink-0 text-label-md font-normal text-on-surface-variant") { t.WatchedPercent watched }
            }

        match e.Mal with
        | Some mal -> malDetails t cell mal
        | None ->
            p(class' = $"flex items-center gap-1.5 text-body-md text-on-surface-variant {cell}") {
                icon Theme.LinkOff
                span() { t.NotOnMyAnimeList }
            }

        p(class' = $"flex items-center gap-1.5 text-label-md font-normal text-on-surface-variant {cell}") {
            icon Theme.Schedule
            timeElement t "relative" e.WatchedAt
        }
    }

let private percent (part: int64) (whole: int64) =
    if whole = 0L then 0 else int (Math.Round(100m * decimal part / decimal whole, MidpointRounding.AwayFromZero))

let private topGenres (t: Texts) (g: TopGenres) =
    let coverage = percent g.MatchedEpisodes g.AllEpisodes

    section(class' = "fr-card flex flex-col gap-5 p-5 md:p-6").data("top-genres", "").attr("aria-labelledby", "top-genres-title") {
        h2(id = "top-genres-title", class' = "text-headline-md text-on-surface") { t.TopGenresTitle }

        div(class' = "space-y-4") {
            for name, count in g.Genres do
                let share = percent count g.MatchedEpisodes

                div(class' = "space-y-2").data("genre", name) {
                    div(class' = "flex items-baseline justify-between gap-3") {
                        span(class' = "min-w-0 text-body-md font-semibold text-on-surface [overflow-wrap:anywhere]") { name }

                        span(class' = "shrink-0 text-label-md font-normal text-on-surface-variant") {
                            span(class' = "font-semibold text-on-surface").data("genre-share", string share) { $"{share}%%" }
                            " · "
                            span().data("genre-episodes", string count) { t.TopGenresEpisodes count }
                        }
                    }

                    shareBar (decimal share)
                }
        }

        p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant").data("genres-coverage", string coverage) {
            icon Theme.Info
            span() { t.TopGenresCoverage coverage }
        }
    }

let private header (ctx: HttpContext) (t: Texts) (m: DashboardModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}"

    div(class' = "flex flex-col gap-6 md:flex-row md:items-end md:justify-between") {
        div(class' = "min-w-0 space-y-3 [overflow-wrap:anywhere]") {
            pageHeading m.DisplayName (Some t.PublicProfileNote)

            p(class' = "flex flex-wrap items-center gap-x-1.5 gap-y-1 text-body-md text-on-surface-variant") {
                icon Theme.Sync
                span() { t.LastSync + ":" }

                match m.LastSyncAt with
                | Some at -> span(class' = "text-on-surface") { timeElement t "relative" at }
                | None -> span(class' = "text-on-surface") { t.NeverSynced }

                match m.Recent with
                | newest :: _ ->
                    // One group, so the separator wraps with what it introduces
                    span() {
                        span().attr("aria-hidden", "true") { "· " }
                        t.NewestEpisode + ": "
                        span(class' = "text-on-surface") { seriesLink m.Username newest.SeriesId newest.SeriesTitle }
                    }
                | [] -> Fragment()
            }
        }

        div(class' = "flex shrink-0 flex-wrap items-start gap-2") {
            a(href = $"/u/{m.Username}/dna", class' = btn Primary).data("dna-link", "") {
                icon Theme.Fingerprint
                t.DnaLink
            }

            a(href = $"/u/{m.Username}/year", class' = btn Secondary).data("year-link", "") {
                icon Theme.CalendarMonth
                t.YearLink
            }

            copyLink t address
        }
    }

let private recent (t: Texts) (m: DashboardModel) =
    let wide = m.TopGenres.IsNone

    section(class' = (if wide then "space-y-4 lg:col-span-12" else "space-y-4 lg:col-span-8"))
        .attr("aria-labelledby", "recent-title") {
        h2(id = "recent-title", class' = "text-headline-md text-on-surface") { t.RecentTitle }

        div(class' = (if wide then "grid grid-cols-1 gap-3 lg:grid-cols-2" else "grid grid-cols-1 gap-3")) {
            for e in m.Recent do
                recentItem t m.Username e
        }
    }

let view (ctx: HttpContext) (t: Texts) (m: DashboardModel) =
    div(class' = "space-y-8 md:space-y-10") {
        header ctx t m

        if m.SyncInProgress then
            (processing t.SyncInProgress).attr("data-sync-in-progress", "")

        match m.Figures with
        | Some figures ->
            stats t figures

            div(class' = "grid grid-cols-1 gap-8 lg:grid-cols-12 lg:items-start lg:gap-6") {
                recent t m

                match m.TopGenres with
                | Some genres -> div(class' = "lg:col-span-4 lg:sticky lg:top-24") { topGenres t genres }
                | None -> Fragment()
            }
        | None ->
            (emptyState
                Theme.History
                t.EmptyTitle
                t.EmptyBody
                (Some(
                    a(href = "/#how-to-sync", class' = btn Primary) {
                        t.HomeHowToSyncTitle
                        icon Theme.ArrowForward
                    }
                )))
                .data("empty-state", "")
    }
    |> Layout.page ctx t m.DisplayName

let notFoundView (ctx: HttpContext) (t: Texts) =
    Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody

// ---------------------------------------------------------------------------
// Data access (data-model.md, "Dashboard figures"); only resolved episodes count
// ---------------------------------------------------------------------------

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private tryLoad (db: DbConfig) (username: string) : Task<DashboardModel option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use userCmd = new NpgsqlCommand("SELECT id, display_name FROM users WHERE username = @username", connection)
        userCmd.Parameters.AddWithValue("username", username) |> ignore

        let! user =
            task {
                use! r = userCmd.ExecuteReaderAsync()
                return if r.Read() then Some(r.GetGuid 0, r.GetString 1) else None
            }

        match user with
        | None -> return None
        | Some(userId, displayName) ->
            use figuresCmd =
                new NpgsqlCommand(
                    """
                    SELECT count(DISTINCT e.series_id) FILTER (WHERE w.fully_watched),
                           count(*) FILTER (WHERE w.fully_watched),
                           coalesce(sum(CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                             ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END), 0)::bigint,
                           count(*)
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                    """,
                    connection
                )

            figuresCmd.Parameters.AddWithValue("user_id", userId) |> ignore

            let! figures =
                task {
                    use! r = figuresCmd.ExecuteReaderAsync()
                    r.Read() |> ignore
                    let hours = decimal (r.GetInt64 2) / 3_600_000m

                    return
                        if r.GetInt64 3 = 0L then
                            None
                        else
                            Some
                                { Series = r.GetInt64 0
                                  Episodes = r.GetInt64 1
                                  Hours = round1 hours
                                  Days = round1 (hours / 24m) }
                }

            use recentCmd =
                new NpgsqlCommand(
                    """
                    SELECT coalesce(e.series_title, s.title, e.series_id), e.episode_number, e.duration_ms,
                           e.thumbnail_url, w.watched_at, w.fully_watched,
                           a.url, a.score,
                           (SELECT string_agg(g.name, ' / ' ORDER BY g.position)
                            FROM (SELECT t.name, at.position FROM mal_anime_tags at
                                  JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                                  WHERE at.mal_id = a.mal_id AND at.kind = 'genre'
                                  ORDER BY at.position LIMIT 3) g),
                           (SELECT t.name FROM mal_anime_tags at
                            JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                            WHERE at.mal_id = a.mal_id AND at.kind = 'studio'
                            ORDER BY at.position LIMIT 1),
                           a.type, coalesce(a.year, extract(year FROM a.aired_from)::int),
                           w.playhead_s, e.series_id
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    LEFT JOIN cr_series s ON s.id = e.series_id
                    LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                    WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                    ORDER BY w.watched_at DESC, e.id
                    LIMIT 6
                    """,
                    connection
                )

            recentCmd.Parameters.AddWithValue("user_id", userId) |> ignore

            let! recent =
                task {
                    use! r = recentCmd.ExecuteReaderAsync()
                    let items = ResizeArray<RecentEpisode>()

                    while r.Read() do
                        items.Add
                            { SeriesId = r.GetString 13
                              SeriesTitle = r.GetString 0
                              EpisodeNumber = if r.IsDBNull 1 then None else Some(r.GetInt32 1)
                              DurationMs = r.GetInt32 2
                              ThumbnailUrl = if r.IsDBNull 3 then None else Some(r.GetString 3)
                              WatchedAt = r.GetDateTime 4
                              FullyWatched = r.GetBoolean 5
                              PlayheadS = r.GetInt32 12
                              Mal =
                                if r.IsDBNull 6 then
                                    None
                                else
                                    let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)

                                    Some
                                        { Url = r.GetString 6
                                          Score = if r.IsDBNull 7 then None else Some(r.GetDecimal 7)
                                          Genres = text 8
                                          Studio = text 9
                                          Format = text 10
                                          Year = if r.IsDBNull 11 then None else Some(r.GetInt32 11) } }

                    return List.ofSeq items
                }

            // "Top genres": the "Episodes watched" rule (fully watched, known duration), matched seasons only
            use genresCmd =
                new NpgsqlCommand(
                    """
                    WITH fw AS (
                        SELECT e.season_id FROM watch_events w
                        JOIN cr_episodes e ON e.id = w.cr_episode_id
                        WHERE w.user_id = @user_id AND w.fully_watched AND e.duration_ms IS NOT NULL
                    ),
                    matched AS (
                        SELECT m.mal_id FROM fw JOIN cr_season_matches m ON m.season_id = fw.season_id AND m.status = 'matched'
                    )
                    SELECT NULL::text, (SELECT count(*) FROM fw), (SELECT count(*) FROM matched)
                    UNION ALL
                    (SELECT t.name, count(*), NULL FROM matched x
                     JOIN mal_anime_tags at ON at.mal_id = x.mal_id AND at.kind = 'genre'
                     JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                     GROUP BY t.tag_id, t.name
                     ORDER BY count(*) DESC, t.name
                     LIMIT 5)
                    """,
                    connection
                )

            genresCmd.Parameters.AddWithValue("user_id", userId) |> ignore

            let! topGenres =
                task {
                    use! r = genresCmd.ExecuteReaderAsync()
                    let mutable all, matched = 0L, 0L
                    let genres = ResizeArray()

                    while r.Read() do
                        if r.IsDBNull 0 then
                            all <- r.GetInt64 1
                            matched <- r.GetInt64 2
                        else
                            genres.Add(r.GetString 0, r.GetInt64 1)

                    return
                        if matched = 0L then
                            None
                        else
                            Some
                                { Genres = List.ofSeq genres
                                  MatchedEpisodes = matched
                                  AllEpisodes = all }
                }

            use syncCmd =
                new NpgsqlCommand(
                    "SELECT max(received_at), coalesce(bool_or(status IN ('pending', 'processing')), false) FROM cr_imports WHERE user_id = @user_id",
                    connection
                )

            syncCmd.Parameters.AddWithValue("user_id", userId) |> ignore

            let! lastSync, inProgress =
                task {
                    use! r = syncCmd.ExecuteReaderAsync()
                    r.Read() |> ignore
                    return (if r.IsDBNull 0 then None else Some(r.GetDateTime 0)), r.GetBoolean 1
                }

            return
                Some
                    { Username = username
                      DisplayName = displayName
                      LastSyncAt = lastSync
                      SyncInProgress = inProgress
                      Figures = figures
                      TopGenres = topGenres
                      Recent = recent }
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (rawUsername: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            let! model =
                match Username.tryCreate rawUsername with
                | Some username -> tryLoad (ctx.GetService<DbConfig>()) username
                | None -> Task.FromResult None

            match model with
            | Some m -> return! ctx.WriteHtmlView(view ctx t m)
            | None ->
                ctx.SetStatusCode 404
                return! ctx.WriteHtmlView(notFoundView ctx t)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}" handler ]
