module FrunchyBackend.Handlers.Users.Dashboard

open System
open System.Globalization
open System.Threading.Tasks
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/dashboard-ui.md)
// ---------------------------------------------------------------------------

type Figures =
    { Series: int64
      Episodes: int64
      Hours: decimal
      Days: decimal }

type RecentEpisode =
    { SeriesTitle: string
      EpisodeNumber: int option
      DurationMs: int
      ThumbnailUrl: string option
      WatchedAt: DateTime
      FullyWatched: bool }

type DashboardModel =
    { DisplayName: string
      LastSyncAt: DateTime option
      SyncInProgress: bool
      Figures: Figures option // None when nothing resolved has been watched yet
      Recent: RecentEpisode list }

let private timeElement = Layout.timeElement

let private statCard (label: string) (name: string) (invariantValue: string) (display: string) =
    div(class' = "stat bg-base-300 rounded-lg shadow") {
        div(class' = "stat-title") { label }
        div(class' = "stat-value text-primary").data("stat", name).data("value", invariantValue) { display }
    }

let private stats (t: Texts) (f: Figures) =
    let inv (d: decimal) = d.ToString("0.0", CultureInfo.InvariantCulture)

    div(class' = "stats stats-vertical lg:stats-horizontal shadow-lg w-full") {
        statCard t.StatSeries "series" (string f.Series) (f.Series.ToString("N0", t.Culture))
        statCard t.StatEpisodes "episodes" (string f.Episodes) (f.Episodes.ToString("N0", t.Culture))
        statCard t.StatHours "hours" (inv f.Hours) (f.Hours.ToString("N1", t.Culture))
        statCard t.StatDays "days" (inv f.Days) (f.Days.ToString("N1", t.Culture))
    }

let private recentItem (t: Texts) (e: RecentEpisode) =
    div(class' = "card card-side bg-base-300 shadow-md").attr("data-recent-item", "") {
        match e.ThumbnailUrl with
        | Some url -> figure(class' = "w-32 shrink-0") { img(src = url, alt = e.SeriesTitle).attr("loading", "lazy") }
        | None -> Fragment()

        div(class' = "card-body p-4") {
            h3(class' = "card-title text-base") { e.SeriesTitle }

            p(class' = "text-sm text-gray-400") {
                (match e.EpisodeNumber with
                 | Some n -> t.EpisodeLabel n
                 | None -> t.SpecialLabel)
                + " · "
                + t.Minutes(int (Math.Round(float e.DurationMs / 60000.0)))
            }

            if not e.FullyWatched then
                span(class' = "badge badge-secondary") { t.InProgress }

            p(class' = "text-xs text-gray-500") { timeElement t "relative" e.WatchedAt }
        }
    }

let private header (t: Texts) (m: DashboardModel) =
    div(class' = "space-y-2") {
        h1(class' = "text-4xl font-bold") { m.DisplayName }
        p(class' = "text-sm text-gray-400") { t.PublicProfileNote }

        p(class' = "text-sm") {
            span(class' = "text-gray-400") { t.LastSync + ": " }

            match m.LastSyncAt with
            | Some at -> timeElement t "relative" at
            | None -> span() { t.NeverSynced }

            match m.Recent with
            | newest :: _ ->
                span(class' = "text-gray-400") { $" · {t.NewestEpisode}: " }
                span() { newest.SeriesTitle }
            | [] -> Fragment()
        }

        if m.SyncInProgress then
            div(class' = "alert alert-info").attr("data-sync-in-progress", "") { span() { t.SyncInProgress } }
    }

let view (ctx: Microsoft.AspNetCore.Http.HttpContext) (t: Texts) (m: DashboardModel) =
    div(class' = "space-y-8") {
        header t m

        match m.Figures with
        | Some figures ->
            stats t figures

            div(class' = "space-y-4") {
                h2(class' = "text-2xl font-bold") { t.RecentTitle }

                div(class' = "grid grid-cols-1 md:grid-cols-2 gap-4") {
                    for e in m.Recent do
                        recentItem t e
                }
            }
        | None ->
            div(class' = "card bg-base-300 shadow-md").attr("data-empty-state", "") {
                div(class' = "card-body") {
                    h2(class' = "card-title") { t.EmptyTitle }
                    p() { t.EmptyBody }
                }
            }
    }
    |> Layout.page ctx t m.DisplayName

let notFoundView (ctx: Microsoft.AspNetCore.Http.HttpContext) (t: Texts) =
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
                           e.thumbnail_url, w.watched_at, w.fully_watched
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    LEFT JOIN cr_series s ON s.id = e.series_id
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
                            { SeriesTitle = r.GetString 0
                              EpisodeNumber = if r.IsDBNull 1 then None else Some(r.GetInt32 1)
                              DurationMs = r.GetInt32 2
                              ThumbnailUrl = if r.IsDBNull 3 then None else Some(r.GetString 3)
                              WatchedAt = r.GetDateTime 4
                              FullyWatched = r.GetBoolean 5 }

                    return List.ofSeq items
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
                    { DisplayName = displayName
                      LastSyncAt = lastSync
                      SyncInProgress = inProgress
                      Figures = figures
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
