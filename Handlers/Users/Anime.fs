module FrunchyBackend.Handlers.Users.Anime

open System
open System.Globalization
open System.Text.RegularExpressions
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
// Model (specs/006-anime-page/data-model.md)
// ---------------------------------------------------------------------------

/// The MyAnimeList entry a season matched (research R5)
type MalInfo =
    { MalId: int
      Title: string
      Url: string
      Format: string option
      Season: string option
      Year: int option
      Score: decimal option
      Episodes: int option
      Status: string option
      /// Up to 3, by position
      Genres: string list
      Studio: string option }

type WatchedEpisode =
    { Id: string
      Number: int option
      Title: string option
      DurationMs: int
      Fully: bool
      /// 0-100; 100 when fully watched
      Percent: int
      WatchedAt: DateTime }

type SeasonRow =
    { /// None groups the watched episodes that have no season ("other")
      Id: string option
      Title: string option
      Number: int option
      Audio: string option
      Dub: bool
      Watched: int64
      Known: int64
      Mal: MalInfo option
      Episodes: WatchedEpisode list }

type Poster =
    /// A MyAnimeList image, portrait
    | Portrait of string
    /// A Crunchyroll episode thumbnail, landscape
    | Thumbnail of string

type AnimeModel =
    { Username: string
      SeriesId: string
      Title: string
      Slug: string option
      Poster: Poster option
      Episodes: int64
      Hours: decimal
      FirstWatch: DateTime
      LatestWatch: DateTime
      Seasons: SeasonRow list }

// ---------------------------------------------------------------------------
// View (contracts/anime-page.md)
// ---------------------------------------------------------------------------

let private header (ctx: HttpContext) (t: Texts) (m: AnimeModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}/anime/{m.SeriesId}"

    div(class' = "space-y-6") {
        div(class' = "flex flex-col gap-6 sm:flex-row sm:items-end") {
            match m.Poster with
            | Some(Portrait url) ->
                img(src = url, alt = "", class' = "aspect-[2/3] w-32 shrink-0 rounded object-cover sm:w-40")
                    .attr("loading", "lazy")
                    .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
            | Some(Thumbnail url) ->
                img(src = url, alt = "", class' = "aspect-video w-full max-w-xs shrink-0 rounded object-cover sm:w-64")
                    .attr("loading", "lazy")
                    .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
            | None -> Fragment()

            div(class' = "min-w-0 space-y-4") {
                h1(class' = "text-display-hero-mobile md:text-display-hero text-on-surface text-balance [overflow-wrap:anywhere]").data("anime-title", "") {
                    m.Title
                }

                div(class' = "flex flex-wrap items-start gap-2") {
                    a(href = $"/u/{m.Username}", class' = btn Ghost).data("dashboard-link", "") {
                        icon Theme.ArrowBack
                        t.DashboardLink
                    }

                    match m.Slug with
                    | Some slug ->
                        a(href = $"https://www.crunchyroll.com/series/{m.SeriesId}/{slug}", target = "_blank", class' = btn Secondary)
                            .attr("rel", "noopener")
                            .data("crunchyroll-link", "") {
                            t.AnimeWatchOnCrunchyroll
                            icon Theme.OpenInNew
                        }
                    | None -> Fragment()

                    copyLink t address
                }
            }
        }

        p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant").data("rewatch-note", "") {
            icon Theme.Info
            span() { t.AnimeRewatchNote }
        }
    }

let private watchTile (t: Texts) (label: string) (hook: string) (instant: DateTime) =
    div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6").data(hook, "") {
        p(class' = "fr-overline") { label }
        p(class' = "text-body-lg text-on-surface") { Layout.timeElement t "absolute" instant }
    }

let private totals (t: Texts) (m: AnimeModel) =
    div(class' = "grid grid-cols-2 gap-4 lg:grid-cols-4").data("anime-totals", "") {
        metricTile t.StatEpisodes "episodes" (string m.Episodes) (m.Episodes.ToString("N0", t.Culture)) None
        metricTile t.StatHours "hours" (inv m.Hours) (m.Hours.ToString("N1", t.Culture)) None
        watchTile t t.AnimeFirstWatch "first-watch" m.FirstWatch
        watchTile t t.AnimeLatestWatch "latest-watch" m.LatestWatch
    }

let private malDetails (t: Texts) (mal: MalInfo) =
    div(class' = "space-y-3").data("mal", string mal.MalId) {
        div(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1") {
            p(class' = "min-w-0 text-body-lg font-semibold text-on-surface [overflow-wrap:anywhere]") { mal.Title }

            a(href = mal.Url, target = "_blank", class' = "fr-link inline-flex min-h-6 items-center gap-1 text-label-md")
                .attr("rel", "noopener")
                .data("mal-link", "") {
                t.MyAnimeListLink
                icon Theme.OpenInNew
            }
        }

        div(class' = "flex flex-wrap items-center gap-1.5") {
            match mal.Format with
            | Some f -> pill Muted f
            | None -> Fragment()

            match mal.Year with
            | Some y -> pill Muted (t.DnaSeason (defaultArg mal.Season "") y)
            | None -> Fragment()

            match mal.Score with
            | Some score ->
                (pill TertiaryTone ("★ " + score.ToString("N2", t.Culture)))
                    .data("mal-score", score.ToString("0.00", CultureInfo.InvariantCulture))
                    .attr("role", "group")
                    .attr("aria-label", t.ScoreLabel)
            | None -> Fragment()

            match mal.Episodes with
            | Some n when n > 0 -> pill Muted (t.AnimeMalEpisodes n)
            | _ -> Fragment()

            match mal.Status with
            | Some s -> pill Muted (t.AnimeStatus s)
            | None -> Fragment()
        }

        if not mal.Genres.IsEmpty || mal.Studio.IsSome then
            p(class' = "text-body-md text-on-surface-variant") {
                String.Join(" / ", mal.Genres)

                match mal.Studio with
                | Some studio -> (if mal.Genres.IsEmpty then "" else " · ") + t.AnimeStudio studio
                | None -> ""
            }
    }

let private episodeRow (t: Texts) (e: WatchedEpisode) =
    // Wide screens line the columns up across rows (the right-hand group dissolves into the grid)
    li(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-3 lg:grid lg:grid-cols-[minmax(0,1fr)_4.5rem_10rem_15rem]")
        .data("episode", e.Id)
        .data("fully", (if e.Fully then "true" else "false"))
        .data("percent", string e.Percent) {
        div(class' = "min-w-0 flex-1 basis-60") {
            span(class' = "text-label-md text-on-surface-variant") {
                match e.Number with
                | Some n -> t.EpisodeLabel n
                | None -> t.SpecialLabel
            }

            match e.Title with
            | Some title -> span(class' = "ml-2 text-body-md text-on-surface [overflow-wrap:anywhere]") { title }
            | None -> Fragment()
        }

        div(class' = "flex flex-wrap items-center gap-x-4 gap-y-1 text-label-md font-normal text-on-surface-variant lg:contents") {
            span(class' = "tabular-nums lg:text-right") { t.Minutes(int (Math.Round(float e.DurationMs / 60000.0))) }

            if e.Fully then
                span(class' = "inline-flex items-center gap-1 text-primary") {
                    icon Theme.CheckCircle
                    t.AnimeEpisodeWatched
                }
            else
                span(class' = "inline-flex items-center gap-2") {
                    div(class' = "w-16") { shareBar (decimal e.Percent) }
                    span() { t.WatchedPercent e.Percent }
                }

            span(class' = "lg:text-right") { Layout.timeElement t "relative" e.WatchedAt }
        }
    }

let private seasonCard (t: Texts) (shownBefore: bool) (s: SeasonRow) =
    let name =
        match s.Id, s.Title, s.Number with
        | None, _, _ -> t.AnimeOtherEpisodes
        | _, Some title, _ -> title
        | _, None, Some n -> t.AnimeSeasonNumber n
        | Some id, None, None -> id

    let el =
        section(class' = "fr-card flex flex-col gap-5")
            .attr("aria-label", name)
            .data("season", defaultArg s.Id "other")
            .data("watched", string s.Watched)
            .data("known", string s.Known)

    let el =
        match s.Audio with
        | Some audio -> el.data("audio", audio)
        | None -> el

    (if s.Dub then el.data("dub", "") else el) {
        div(class' = "space-y-3") {
            seasonNameLine t name s.Audio s.Dub

            // "Other episodes" has no known total to measure against
            if s.Id.IsSome then
                let share = if s.Known = 0L then 0m else 100m * decimal s.Watched / decimal s.Known

                div(class' = "space-y-2") {
                    p(class' = "text-label-md font-normal text-on-surface-variant") { t.AnimeKnownEpisodes s.Watched s.Known }
                    shareBar share
                }
        }

        if s.Id.IsSome then
            match s.Mal with
            | Some mal -> if shownBefore then malCompact t mal.MalId mal.Url else malDetails t mal
            | None ->
                p(class' = "flex items-center gap-1.5 text-body-md text-on-surface-variant").data("mal-missing", "") {
                    icon Theme.LinkOff
                    span() { t.NotOnMyAnimeList }
                }

        ol(class' = "divide-y divide-outline-variant/40") {
            for e in s.Episodes do
                episodeRow t e
        }
    }

let view (ctx: HttpContext) (t: Texts) (m: AnimeModel) =
    div(class' = "space-y-10").data("anime", m.SeriesId) {
        header ctx t m
        totals t m

        div(class' = "space-y-4") {
            sectionTitle t.AnimeSeasonsTitle

            // Seasons often share one entry (a long series, a dub): its details show once, on the first
            let firsts =
                m.Seasons
                |> List.mapi (fun i s -> i, s.Mal |> Option.map _.MalId)
                |> List.choose (fun (i, id) -> id |> Option.map (fun id -> id, i))
                |> List.distinctBy fst
                |> List.map snd
                |> Set.ofList

            for i, s in List.indexed m.Seasons do
                seasonCard t (s.Mal.IsSome && not (firsts.Contains i)) s
        }
    }
    |> Layout.page ctx t m.Title

// ---------------------------------------------------------------------------
// Data access (research R2–R5). One statement: a row per watched episode that carries its season,
// its match and its MyAnimeList entry, so the figures are folded in F# from the same rows.
// ---------------------------------------------------------------------------

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private loadUser (connection: NpgsqlConnection) (username: string) : Task<(Guid * string) option> =
    task {
        use cmd = new NpgsqlCommand("SELECT id, display_name FROM users WHERE username = @username", connection)
        cmd.Parameters.AddWithValue("username", username) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            return Some(r.GetGuid 0, r.GetString 1)
        else
            return None
    }

/// One watched episode with everything its season adds
type private Row =
    { Episode: WatchedEpisode
      /// Minutes watched, a partial episode counting up to its playhead (the dashboard's rule)
      Ms: int64
      Thumbnail: string option
      SeriesTitle: string option
      SeasonId: string option
      SeasonTitle: string option
      SeasonNumber: int option
      Audio: string option
      Known: int64
      Dub: bool
      Mal: MalInfo option
      MalImage: string option
      StoredSeriesTitle: string option
      Slug: string option }

let private loadRows (connection: NpgsqlConnection) (userId: Guid) (seriesId: string) : Task<Row list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT e.id, e.episode_number, e.title, e.duration_ms, w.fully_watched, w.playhead_s, w.watched_at,
                       e.thumbnail_url, e.series_title,
                       e.season_id, sea.title, sea.season_number, sea.audio_locale,
                       (SELECT count(*) FROM cr_episodes k WHERE k.season_id = e.season_id AND k.duration_ms IS NOT NULL),
                       m.method,
                       a.mal_id, a.title, a.url, a.type, a.season, a.year, a.score, a.episodes, a.status, a.image_url,
                       array(SELECT t.name FROM mal_anime_tags at
                             JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                             WHERE at.mal_id = a.mal_id AND at.kind = 'genre'
                             ORDER BY at.position, t.name LIMIT 3),
                       (SELECT t.name FROM mal_anime_tags at
                        JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                        WHERE at.mal_id = a.mal_id AND at.kind = 'studio'
                        ORDER BY at.position, t.name LIMIT 1),
                       s.title, s.slug
                FROM watch_events w
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_seasons sea ON sea.id = e.season_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                LEFT JOIN cr_series s ON s.id = e.series_id
                WHERE w.user_id = @user_id AND e.series_id = @series_id AND e.duration_ms IS NOT NULL
                ORDER BY sea.season_number NULLS LAST, e.season_id NULLS LAST,
                         e.episode_number NULLS LAST, e.title, e.id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
        let int' (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)
        let rows = ResizeArray()

        while r.Read() do
            let duration = r.GetInt32 3
            let fully = r.GetBoolean 4
            let playhead = int64 (r.GetInt32 5)
            let ms = if fully then int64 duration else min (playhead * 1000L) (int64 duration)

            let percent =
                if fully then 100
                else int (Math.Round(100m * decimal ms / decimal duration, MidpointRounding.AwayFromZero))

            let mal =
                if r.IsDBNull 15 then
                    None
                else
                    Some
                        { MalId = r.GetInt32 15
                          Title = r.GetString 16
                          Url = r.GetString 17
                          Format = text 18
                          Season = text 19
                          Year = int' 20
                          Score = (if r.IsDBNull 21 then None else Some(r.GetDecimal 21))
                          Episodes = int' 22
                          Status = text 23
                          Genres = r.GetFieldValue<string[]> 25 |> List.ofArray
                          Studio = text 26 }

            rows.Add
                { Episode =
                    { Id = r.GetString 0
                      Number = int' 1
                      Title = text 2
                      DurationMs = duration
                      Fully = fully
                      Percent = percent
                      WatchedAt = r.GetDateTime 6 }
                  Ms = ms
                  Thumbnail = text 7
                  SeriesTitle = text 8
                  SeasonId = text 9
                  SeasonTitle = text 10
                  SeasonNumber = int' 11
                  Audio = text 12
                  Known = r.GetInt64 13
                  Dub = (text 14 = Some "dub")
                  Mal = mal
                  MalImage = text 24
                  StoredSeriesTitle = text 27
                  Slug = text 28 }

        return List.ofSeq rows
    }

/// The page's figures from the rows: the dashboard's rule restricted to the series (R3), seasons in the
/// order the rows come in (R4), the poster by watch time (R2)
let private toModel (username: string) (seriesId: string) (rows: Row list) : AnimeModel option =
    match rows with
    | [] -> None
    | first :: _ ->
        let seasons =
            rows
            |> List.fold
                (fun (acc: (string option * Row list) list) row ->
                    match acc |> List.tryFindIndex (fun (id, _) -> id = row.SeasonId) with
                    | Some i -> acc |> List.mapi (fun j (id, rs) -> if i = j then id, row :: rs else id, rs)
                    | None -> acc @ [ row.SeasonId, [ row ] ])
                []
            |> List.map (fun (id, rs) -> id, List.rev rs)

        let seasonRow (id: string option, rs: Row list) =
            let head = List.head rs

            { Id = id
              Title = head.SeasonTitle
              Number = head.SeasonNumber
              Audio = head.Audio
              Dub = head.Dub
              Watched = rs |> List.filter _.Episode.Fully |> List.length |> int64
              Known = head.Known
              Mal = head.Mal
              Episodes = rs |> List.map _.Episode }

        // Episodes without a season come last, whatever the database's ordering of nulls
        let named, other = seasons |> List.partition (fun (id, _) -> id.IsSome)

        let poster =
            rows
            |> List.choose (fun r -> r.Mal |> Option.bind (fun mal -> r.MalImage |> Option.map (fun image -> mal.MalId, image, r.Ms)))
            |> List.groupBy (fun (id, image, _) -> id, image)
            |> List.map (fun ((id, image), xs) -> id, image, xs |> List.sumBy (fun (_, _, ms) -> ms))
            |> List.sortBy (fun (id, _, ms) -> -ms, id)
            |> List.tryHead
            |> Option.map (fun (_, image, _) -> Portrait image)
            |> Option.orElse (
                rows
                |> List.filter (fun r -> r.Thumbnail.IsSome)
                |> List.sortBy (fun r -> -r.Episode.WatchedAt.Ticks, r.Episode.Id)
                |> List.tryHead
                |> Option.map (fun r -> Thumbnail r.Thumbnail.Value)
            )

        let title =
            first.StoredSeriesTitle
            |> Option.orElse (rows |> List.choose _.SeriesTitle |> List.sort |> List.tryHead)
            |> Option.defaultValue seriesId

        let fullyWatched = rows |> List.filter _.Episode.Fully |> List.length |> int64

        Some
            { Username = username
              SeriesId = seriesId
              Title = title
              Slug = first.Slug
              Poster = poster
              Episodes = fullyWatched
              Hours = round1 (decimal (rows |> List.sumBy _.Ms) / 3_600_000m)
              FirstWatch = rows |> List.map _.Episode.WatchedAt |> List.min
              LatestWatch = rows |> List.map _.Episode.WatchedAt |> List.max
              Seasons = (named @ other) |> List.map seasonRow }

// ---------------------------------------------------------------------------
// Handler (research R1)
// ---------------------------------------------------------------------------

/// Crunchyroll's id alphabet, checked before the database is touched
let private seriesIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

let private notFound (ctx: HttpContext) (t: Texts) (userMissing: bool) =
    ctx.SetStatusCode 404

    if userMissing then
        ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)
    else
        ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// /u/{username}/anime/{seriesId}
let handler (rawUsername: string) (rawSeriesId: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            match Username.tryCreate rawUsername with
            | None -> return! notFound ctx t true
            | Some _ when not (seriesIdPattern.IsMatch rawSeriesId) -> return! notFound ctx t false
            | Some username ->
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadUser connection username with
                | None -> return! notFound ctx t true
                | Some(userId, _) ->
                    let! rows = loadRows connection userId rawSeriesId

                    match toModel username rawSeriesId rows with
                    | None -> return! notFound ctx t false
                    | Some model -> return! ctx.WriteHtmlView(view ctx t model)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}/anime/{%s}" handler ]
