module FrunchyBackend.Handlers.Series.Series

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Model (specs/013-series-page/data-model.md)
// ---------------------------------------------------------------------------

type EpisodeLine =
    { Id: string
      Number: int option
      /// The special's own label ("OVA 1"), when the catalog has one
      Label: string option
      Title: string option
      DurationMs: int
      /// Public or unlisted, not deleted; hidden ones count (the thread's visibility)
      Comments: int }

type SeasonBlock =
    { /// None groups the episodes that have no season ("other")
      Id: string option
      Title: string option
      Number: int option
      Audio: string option
      Dub: bool
      Mal: MalInfo option
      /// An earlier season already showed this MyAnimeList entry in full
      ShownBefore: bool
      Episodes: EpisodeLine list }

type SeriesPage =
    { SeriesId: string
      Title: string
      Slug: string option
      Poster: Charts.Poster option
      People: int option
      PeopleWeek: int option
      Seasons: SeasonBlock list }

// ---------------------------------------------------------------------------
// View (contracts/series-page.md)
// ---------------------------------------------------------------------------

let private activityLine (hook: string) (text: string) (n: int) =
    p(class' = "text-label-md font-normal text-on-surface-variant").data(hook, string n) { text }

let private header (t: Texts) (page: SeriesPage) =
    div(class' = "space-y-6") {
        div(class' = "flex flex-col gap-6 sm:flex-row sm:items-end") {
            match page.Poster with
            | Some(Charts.Portrait url) ->
                img(src = url, alt = "", class' = "aspect-[2/3] w-32 shrink-0 rounded-md object-cover sm:w-40")
                    .attr("loading", "lazy")
                    .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
            | Some(Charts.Thumbnail url) ->
                img(src = url, alt = "", class' = "aspect-video w-full max-w-xs shrink-0 rounded-md object-cover sm:w-64")
                    .attr("loading", "lazy")
                    .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
            | None -> Fragment()

            div(class' = "min-w-0 space-y-4") {
                h1(class' = "text-display-hero-mobile md:text-display-hero text-on-surface text-balance [overflow-wrap:anywhere]")
                    .data("series-title", "") {
                    page.Title
                }

                match page.Slug with
                | Some slug ->
                    a(href = $"https://www.crunchyroll.com/series/{page.SeriesId}/{slug}", target = "_blank", class' = btn Secondary)
                        .attr("rel", "noopener")
                        .data("cr-series-link", "") {
                        t.AnimeWatchOnCrunchyroll
                        icon Theme.OpenInNew
                    }
                | None -> Fragment()

                if page.People.IsSome || page.PeopleWeek.IsSome then
                    div(class' = "space-y-1") {
                        match page.People with
                        | Some n -> activityLine "people-count" (t.ChartsWatchedBy n) n
                        | None -> Fragment()

                        match page.PeopleWeek with
                        | Some n -> activityLine "people-week" (t.SeriesWatchedWeek n) n
                        | None -> Fragment()
                    }
            }
        }
    }

let private episodeRow (t: Texts) (seriesId: string) (e: EpisodeLine) =
    let label =
        match e.Number, e.Label with
        | Some n, _ -> t.EpisodeLabel n
        | None, Some label -> label
        | None, None -> t.SpecialLabel

    let href = $"/anime/{seriesId}/episode/{e.Id}"

    li(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-3")
        .data("episode", e.Id)
        .data("comments", string e.Comments) {
        div(class' = "min-w-0 flex-1 basis-60") {
            match e.Title with
            | Some title ->
                span(class' = "text-label-md text-on-surface-variant") { label }

                a(href = href, class' = "ml-2 text-body-md text-on-surface [overflow-wrap:anywhere] hover:underline")
                    .data("episode-link", e.Id) {
                    title
                }
            | None -> a(href = href, class' = "text-label-md text-on-surface-variant hover:underline").data("episode-link", e.Id) { label }
        }

        div(class' = "flex flex-wrap items-center gap-x-4 gap-y-1 text-label-md font-normal text-on-surface-variant") {
            span(class' = "tabular-nums") { t.Minutes(int (Math.Round(float e.DurationMs / 60000.0))) }

            // Nothing for an episode nobody has written on: a list of "0 comments" says nothing
            if e.Comments > 0 then
                span(class' = "tabular-nums") { t.SeriesCommentsLabel e.Comments }
        }
    }

/// Longer seasons start closed behind a native <details>: One Piece has over a thousand known episodes
let private collapseAbove = 30

let private seasonCard (t: Texts) (seriesId: string) (s: SeasonBlock) =
    let name =
        match s.Id, s.Title, s.Number with
        | None, _, _ -> t.AnimeOtherEpisodes
        | _, Some title, _ -> title
        | _, None, Some n -> t.AnimeSeasonNumber n
        | Some id, None, None -> id

    let known = s.Episodes.Length

    let el =
        section(class' = "fr-card flex flex-col gap-5")
            .attr("aria-label", name)
            .data("season", defaultArg s.Id "other")
            .data("known", string known)

    let el =
        match s.Audio with
        | Some audio -> el.data("audio", audio)
        | None -> el

    let list =
        ol(class' = "divide-y divide-outline-variant/40") {
            for e in s.Episodes do
                episodeRow t seriesId e
        }

    (if s.Dub then el.data("dub", "") else el) {
        div(class' = "space-y-3") {
            seasonNameLine t name s.Audio s.Dub

            if known <= collapseAbove then
                p(class' = "text-label-md font-normal text-on-surface-variant") { t.SeriesKnownEpisodes known }
        }

        if s.Id.IsSome then
            match s.Mal with
            | Some mal -> if s.ShownBefore then malCompact t mal.MalId mal.Url else malDetails t mal
            | None ->
                p(class' = "flex items-center gap-1.5 text-body-md text-on-surface-variant").data("mal-missing", "") {
                    icon Theme.LinkOff
                    span() { t.NotOnMyAnimeList }
                }

        if known > collapseAbove then
            details(class' = "group").data("season-collapsed", "") {
                summary(class' = "min-h-6 cursor-pointer py-1 text-label-md text-on-surface-variant hover:text-on-surface") {
                    t.SeriesKnownEpisodes known
                }

                list
            }
        else
            list
    }

let view (ctx: HttpContext) (t: Texts) (page: SeriesPage) =
    div(class' = "space-y-10").data("series-page", page.SeriesId) {
        header t page

        div(class' = "space-y-4") {
            sectionTitle t.AnimeSeasonsTitle

            p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant").data("known-note", "") {
                icon Theme.Info
                span() { t.SeriesKnownNote }
            }

            for s in page.Seasons do
                seasonCard t page.SeriesId s
        }
    }
    |> Layout.page ctx t (t.SeriesPageTitle page.Title)

// ---------------------------------------------------------------------------
// Data access (research R2). One statement for the body: a row per known episode that carries its season,
// the season's match, its MyAnimeList entry and its comment count. The genres and studio are read once per
// season (the `sm` CTE), not once per episode.
// ---------------------------------------------------------------------------

type private Row =
    { Episode: EpisodeLine
      SeriesTitle: string option
      SeasonId: string option
      SeasonTitle: string option
      SeasonNumber: int option
      Audio: string option
      Dub: bool
      Mal: MalInfo option
      StoredSeriesTitle: string option
      Slug: string option }

let private loadRows (connection: NpgsqlConnection) (seriesId: string) : Task<Row list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH sm AS (
                    SELECT sea.id AS season_id, sea.title AS season_title, sea.season_number, sea.audio_locale, m.method,
                           a.mal_id, a.title AS mal_title, a.url, a.type, a.season, a.year, a.score, a.episodes, a.status,
                           array(SELECT t.name FROM mal_anime_tags at
                                 JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                                 WHERE at.mal_id = a.mal_id AND at.kind = 'genre'
                                 ORDER BY at.position, t.name LIMIT 3) AS genres,
                           (SELECT t.name FROM mal_anime_tags at
                            JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                            WHERE at.mal_id = a.mal_id AND at.kind = 'studio'
                            ORDER BY at.position, t.name LIMIT 1) AS studio
                    FROM cr_seasons sea
                    LEFT JOIN cr_season_matches m ON m.season_id = sea.id AND m.status = 'matched'
                    LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                    WHERE sea.id IN (SELECT season_id FROM cr_episodes WHERE series_id = @series_id)
                )
                SELECT e.id, e.episode_number, e.episode_label, e.title, e.duration_ms, e.series_title, e.season_id,
                       sm.season_title, sm.season_number, sm.audio_locale, sm.method,
                       sm.mal_id, sm.mal_title, sm.url, sm.type, sm.season, sm.year, sm.score, sm.episodes, sm.status,
                       sm.genres, sm.studio,
                       s.title, s.slug,
                       (SELECT count(*) FROM comments c
                        WHERE c.cr_episode_id = e.id AND c.deleted_at IS NULL AND c.audience IN ('public', 'unlisted'))
                FROM cr_episodes e
                LEFT JOIN sm ON sm.season_id = e.season_id
                LEFT JOIN cr_series s ON s.id = e.series_id
                WHERE e.series_id = @series_id AND e.duration_ms IS NOT NULL
                ORDER BY sm.season_number NULLS LAST, e.season_id NULLS LAST,
                         e.episode_number NULLS LAST, e.title, e.id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
        let int' (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)
        let rows = ResizeArray()

        while r.Read() do
            let mal =
                if r.IsDBNull 11 then
                    None
                else
                    Some
                        { MalId = r.GetInt32 11
                          Title = r.GetString 12
                          Url = r.GetString 13
                          Format = text 14
                          Season = text 15
                          Year = int' 16
                          Score = (if r.IsDBNull 17 then None else Some(r.GetDecimal 17))
                          Episodes = int' 18
                          Status = text 19
                          Genres = r.GetFieldValue<string[]> 20 |> List.ofArray
                          Studio = text 21 }

            rows.Add
                { Episode =
                    { Id = r.GetString 0
                      Number = int' 1
                      Label = text 2
                      Title = text 3
                      DurationMs = r.GetInt32 4
                      Comments = int (r.GetInt64 24) }
                  SeriesTitle = text 5
                  SeasonId = text 6
                  SeasonTitle = text 7
                  SeasonNumber = int' 8
                  Audio = text 9
                  Dub = (text 10 = Some "dub")
                  Mal = mal
                  StoredSeriesTitle = text 22
                  Slug = text 23 }

        return List.ofSeq rows
    }

/// The poster (research R2): the MyAnimeList image of the matched season that most distinct people watched
/// (ties: the smaller mal_id), else the thumbnail of the most recently watched episode that has one
let private loadPoster (connection: NpgsqlConnection) (seriesId: string) : Task<Charts.Poster option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT (SELECT a.image_url
                        FROM cr_episodes e
                        JOIN watch_events w ON w.cr_episode_id = e.id
                        JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                        JOIN mal_anime a ON a.mal_id = m.mal_id AND a.image_url IS NOT NULL
                        WHERE e.series_id = @series_id AND e.duration_ms IS NOT NULL
                        GROUP BY a.mal_id, a.image_url
                        ORDER BY count(DISTINCT w.user_id) DESC, a.mal_id
                        LIMIT 1),
                       (SELECT e.thumbnail_url
                        FROM cr_episodes e
                        JOIN watch_events w ON w.cr_episode_id = e.id
                        WHERE e.series_id = @series_id AND e.duration_ms IS NOT NULL AND e.thumbnail_url IS NOT NULL
                        ORDER BY w.watched_at DESC, e.id
                        LIMIT 1)
                """,
                connection
            )

        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)

            match text 0, text 1 with
            | Some image, _ -> return Some(Charts.Portrait image)
            | None, Some thumbnail -> return Some(Charts.Thumbnail thumbnail)
            | None, None -> return None
        else
            return None
    }

/// Seasons in the order the rows come in (number, then id), the seasonless episodes last whatever the database
/// does with nulls; a season whose entry an earlier one shows in full is marked
let private toSeasons (rows: Row list) : SeasonBlock list =
    let seasonOf (id: string option, rs: Row list) =
        let head = List.head rs

        { Id = id
          Title = head.SeasonTitle
          Number = head.SeasonNumber
          Audio = head.Audio
          Dub = head.Dub
          Mal = head.Mal
          ShownBefore = false
          Episodes = rs |> List.map _.Episode }

    let named, other =
        rows
        |> List.groupBy _.SeasonId
        |> List.partition (fun (id, _) -> id.IsSome)

    (named @ other)
    |> List.map seasonOf
    |> List.fold
        (fun (shown: Set<int>, acc: SeasonBlock list) s ->
            match s.Id, s.Mal with
            | Some _, Some mal -> shown.Add mal.MalId, { s with ShownBefore = shown.Contains mal.MalId } :: acc
            | _ -> shown, s :: acc)
        (Set.empty, [])
    |> snd
    |> List.rev

let private titleOf (seriesId: string) (rows: Row list) =
    (List.head rows).StoredSeriesTitle
    |> Option.orElse (rows |> List.choose _.SeriesTitle |> List.sort |> List.tryHead)
    |> Option.defaultValue seriesId

// ---------------------------------------------------------------------------
// Handler (research R1)
// ---------------------------------------------------------------------------

/// Crunchyroll's id alphabet, checked before the database is touched
let private seriesIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

let private notFound (ctx: HttpContext) (t: Texts) =
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// /anime/{seriesId}
let handler (rawSeriesId: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            if not (seriesIdPattern.IsMatch rawSeriesId) then
                return! notFound ctx t
            else
                let db = ctx.GetService<DbConfig>()
                use connection = getConnection db
                do! connection.OpenAsync()
                let! rows = loadRows connection rawSeriesId

                if rows.IsEmpty then
                    return! notFound ctx t
                else
                    let! poster = loadPoster connection rawSeriesId
                    let minimum = Charts.minPeople (ctx.GetService<IConfiguration>())
                    let now = ctx.GetService<TimeProvider>().GetUtcNow()
                    let! people = Charts.seriesPeople db rawSeriesId minimum
                    let! week = Charts.seriesPeopleSince db rawSeriesId (now.AddDays -7.0) minimum

                    let page =
                        { SeriesId = rawSeriesId
                          Title = titleOf rawSeriesId rows
                          Slug = (List.head rows).Slug
                          Poster = poster
                          People = people
                          PeopleWeek = week
                          Seasons = toSeasons rows }

                    return! ctx.WriteHtmlView(view ctx t page)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/anime/{%s}" handler ]
