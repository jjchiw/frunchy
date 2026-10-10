module FrunchyBackend.Handlers.Users.Dna

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
// Model (specs/004-anime-dna-design-system/data-model.md)
// ---------------------------------------------------------------------------

/// The page appears from this many fully watched episodes (FR-014)
let minimumEpisodes = 15L

/// A named share of a whole, in percent with 1 decimal
type ShareRow = { Name: string; Share: decimal }

type GenreRow =
    { Name: string
      Share: decimal
      /// (Crunchyroll series id, title) with the most minutes in the genre; None on "Others" and on a genre without one
      Top: (string * string) option
      /// The closing "Others" row, which stands for every genre outside the top five
      IsOthers: bool }

type Insignia =
    { SeriesId: string
      Title: string
      Poster: string option
      Format: string option
      Studio: string option
      Season: string option
      Year: int option
      FullyWatched: int64
      MalEpisodes: int option
      Hours: decimal }

type Pace =
    { Episodes: int64
      PerDay: decimal
      /// Weeks of the 13 with a fully watched episode
      ActiveWeeks: int
      /// Fully watched episodes per day, index 0 = the last 24 hours, 89 = 89 days ago
      PerDayCounts: int64 array }

type Figures =
    { Hours: decimal
      Days: decimal
      Episodes: int64
      /// Matched minutes ÷ all minutes, deduplicated (FR-004)
      Coverage: decimal
      /// Minutes with a known release year ÷ all minutes
      YearCoverage: decimal
      Studios: ShareRow list
      Decades: ShareRow list
      Genres: GenreRow list
      Formats: ShareRow list
      Insignia: Insignia option
      Pace: Pace }

type DnaModel =
    { Username: string
      DisplayName: string
      FullyWatched: int64
      /// None below minimumEpisodes (the page-level "not enough data yet")
      Figures: Figures option }

// ---------------------------------------------------------------------------
// View (contracts/dna-page.md; design/frunchy_anime_dna_profile_u_username_dna, per design/REVIEW.md)
// ---------------------------------------------------------------------------

let private header (ctx: HttpContext) (t: Texts) (m: DnaModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}/dna"

    div(class' = "flex flex-col gap-6 md:flex-row md:items-end md:justify-between") {
        div(class' = "space-y-3") {
            h1(class' = "text-display-hero-mobile md:text-display-hero text-on-surface text-balance").data("dna-user", m.DisplayName) {
                t.DnaHeading m.DisplayName
            }

            p(class' = "max-w-prose text-body-lg text-on-surface-variant") { t.DnaIntro }
        }

        div(class' = "flex flex-wrap items-start gap-2") {
            a(href = $"/u/{m.Username}", class' = btn Ghost).data("dashboard-link", "") {
                icon Theme.ArrowBack
                t.DashboardLink
            }

            a(href = $"/u/{m.Username}/year", class' = btn Ghost).data("year-link", "") {
                icon Theme.CalendarMonth
                t.YearLink
            }

            copyLink t address
        }
    }

let private totals (t: Texts) (f: Figures) =
    div(class' = "grid grid-cols-1 gap-4 sm:grid-cols-3").data("dna-totals", "") {
        div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6") {
            p(class' = "fr-overline") { t.DnaWatchTime }

            div(class' = "flex items-baseline gap-1.5") {
                span(class' = "text-stat-counter text-on-surface").data("stat", "hours").data("value", inv f.Hours) {
                    f.Hours.ToString("N1", t.Culture)
                }

                span(class' = "text-headline-md text-on-surface-variant").attr("aria-hidden", "true") { "h" }
            }

            p(class' = "text-label-md font-normal text-on-surface-variant").data("stat", "days").data("value", inv f.Days) {
                t.DnaDaysOfLife f.Days
            }
        }

        metricTile t.StatEpisodes "episodes" (string f.Episodes) (f.Episodes.ToString("N0", t.Culture)) None
        metricTile t.DnaCoverageTile "coverage" (inv f.Coverage) (pctText t f.Coverage) (Some t.DnaCoverageCaption)
    }

let private insignia (t: Texts) (username: string) (i: Insignia) =
    (figureSection "dna-insignia" t.DnaInsigniaTitle None "lg:col-span-7")
        .data("series", i.SeriesId)
        .data("episodes", string i.FullyWatched) {
        sectionHeading t.DnaInsigniaTitle

        div(class' = "flex flex-col gap-5 sm:flex-row") {
            match i.Poster with
            | Some url ->
                img(src = url, alt = i.Title, class' = "aspect-[2/3] w-32 shrink-0 rounded object-cover sm:w-40")
                    .attr("loading", "lazy")
                    .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")
            | None -> Fragment()

            div(class' = "flex min-w-0 flex-col gap-3") {
                p(class' = "text-headline-lg text-on-surface text-balance") { seriesLink username i.SeriesId i.Title }

                div(class' = "flex flex-wrap gap-2") {
                    match i.Format with
                    | Some f -> pill PrimaryTone f
                    | None -> Fragment()

                    match i.Studio with
                    | Some s -> pill SecondaryTone s
                    | None -> Fragment()

                    match i.Season, i.Year with
                    | Some s, Some y -> pill Muted (t.DnaSeason s y)
                    | None, Some y -> pill Muted (string y)
                    | _ -> Fragment()
                }

                p(class' = "text-body-lg text-on-surface") { t.DnaInsigniaEpisodes i.FullyWatched i.MalEpisodes }
                p(class' = "text-body-md text-on-surface-variant") { t.DnaInsigniaHours i.Hours }
            }
        }
    }

let private paceChart (t: Texts) (pc: Pace) =
    let peak = max 1L (Array.max pc.PerDayCounts)

    div(class' = "flex h-20 items-end gap-px")
        .attr("role", "img")
        .attr("aria-label", t.DnaPaceChart pc.Episodes) {
        // oldest first, today on the right
        for i in 89 .. -1 .. 0 do
            let n = pc.PerDayCounts[i]
            let height = if n = 0L then 4m else max 8m (100m * decimal n / decimal peak)

            div(class' = (if n = 0L then "flex-1 rounded-sm bg-surface-container-highest" else "flex-1 rounded-sm bg-primary"))
                .attr("style", $"""height: {height.ToString("0.#", CultureInfo.InvariantCulture)}%%""")
    }

let private pace (t: Texts) (pc: Pace) =
    let el = figureSection "dna-pace" t.DnaPaceTitle None "lg:col-span-5"

    if pc.Episodes < 10L then
        el.data("not-enough", "") {
            sectionHeading t.DnaPaceTitle
            p(class' = "text-body-md text-on-surface-variant") { t.DnaPaceNotEnough }
        }
    else
        let steady = pc.ActiveWeeks >= 9

        el
            .data("value", inv pc.PerDay)
            .data("label", (if steady then "steady" else "bursts"))
            .data("episodes", string pc.Episodes)
            .data("weeks", string pc.ActiveWeeks) {
            sectionHeading t.DnaPaceTitle

            div(class' = "flex flex-wrap items-baseline gap-x-2 gap-y-1") {
                span(class' = "text-stat-counter text-on-surface") { pc.PerDay.ToString("N1", t.Culture) }
                span(class' = "text-body-md text-on-surface-variant") { t.DnaPaceUnit }
                pill (if steady then PrimaryTone else TertiaryTone) (if steady then t.DnaSteady else t.DnaBursts)
            }

            paceChart t pc
            p(class' = "text-label-md font-normal text-on-surface-variant") { t.DnaPaceDetail pc.Episodes pc.ActiveWeeks }
        }

let private studios (t: Texts) (f: Figures) =
    let el = figureSection "dna-studio" t.DnaStudioTitle (Some f.Coverage) "lg:col-span-6"

    match f.Studios with
    | [] ->
        el {
            sectionHeading t.DnaStudioTitle
            notEnough t
        }
    | first :: rest ->
        el {
            sectionHeading t.DnaStudioTitle

            div(class' = "space-y-1").data("rank", "1").data("name", first.Name).data("share", inv first.Share) {
                p(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance") { first.Name }
                p(class' = "text-body-md text-primary") { pctText t first.Share }
            }

            for second in rest |> List.truncate 1 do
                div(class' = "flex items-baseline justify-between gap-4 border-t border-outline-variant/50 pt-4")
                    .data("rank", "2")
                    .data("name", second.Name)
                    .data("share", inv second.Share) {
                    div() {
                        p(class' = "fr-overline") { t.DnaRunnerUp }
                        p(class' = "text-headline-md text-on-surface") { second.Name }
                    }

                    span(class' = "text-body-md text-on-surface-variant") { pctText t second.Share }
                }

            coverageNote (t.DnaCoverage f.Coverage)
        }

let private decades (t: Texts) (f: Figures) =
    let el = figureSection "dna-decade" t.DnaDecadeTitle (Some f.YearCoverage) "lg:col-span-6"

    match f.Decades with
    | [] ->
        el {
            sectionHeading t.DnaDecadeTitle
            notEnough t
        }
    | top :: _ ->
        let name (d: ShareRow) = t.DnaDecade(int d.Name)

        el.data("name", top.Name).data("share", inv top.Share) {
            sectionHeading t.DnaDecadeTitle

            div(class' = "space-y-1") {
                p(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface") { name top }
                p(class' = "text-body-md text-primary") { pctText t top.Share }
            }

            div(class' = "space-y-3") {
                for d in f.Decades do
                    div().data("decade", d.Name).data("share", inv d.Share) { shareLine (name d) None d.Share t }
            }

            coverageNote (t.DnaDecadeCoverage f.YearCoverage)
        }

let private genres (t: Texts) (username: string) (f: Figures) =
    let el = figureSection "dna-genres" t.DnaGenresTitle (Some f.Coverage) "lg:col-span-7"

    if f.Genres.IsEmpty then
        el {
            sectionHeading t.DnaGenresTitle
            notEnough t
        }
    else
        el {
            div(class' = "space-y-1") {
                sectionHeading t.DnaGenresTitle
                p(class' = "text-body-md text-on-surface-variant") { t.DnaGenresNote }
            }

            div(class' = "space-y-4") {
                for g in f.Genres do
                    let row =
                        div().data("genre", (if g.IsOthers then "others" else g.Name)).data("share", inv g.Share)

                    if g.IsOthers then
                        row { shareLine t.DnaOthers None g.Share t }
                    else
                        match g.Top with
                        | Some(seriesId, title) ->
                            row.data("top-series", seriesId) { shareLine g.Name (Some(span() { t.TopAnimePrefix; " "; seriesLink username seriesId title })) g.Share t }
                        | None -> row { shareLine g.Name None g.Share t }
            }

            coverageNote (t.DnaCoverage f.Coverage)
        }

let private formats (t: Texts) (f: Figures) =
    let el = figureSection "dna-formats" t.DnaFormatsTitle (Some f.Coverage) "lg:col-span-5"

    if f.Formats.IsEmpty then
        el {
            sectionHeading t.DnaFormatsTitle
            notEnough t
        }
    else
        el {
            sectionHeading t.DnaFormatsTitle

            div(class' = "space-y-4") {
                for x in f.Formats do
                    div().data("format", x.Name).data("share", inv x.Share) { shareLine x.Name None x.Share t }
            }

            coverageNote (t.DnaCoverage f.Coverage)
        }

let private empty (t: Texts) (m: DnaModel) =
    div().data("dna-empty", "").data("value", string m.FullyWatched) {
        emptyState
            Theme.Fingerprint
            t.DnaEmptyTitle
            t.DnaEmptyBody
            (Some(
                div(class' = "flex flex-col items-center gap-4") {
                    p(class' = "text-label-md text-primary") { t.DnaEmptyCount m.FullyWatched }

                    a(href = "/#how-to-sync", class' = btn Primary) {
                        t.DnaEmptyAction
                        icon Theme.ArrowForward
                    }
                }
            ))
    }

let view (ctx: HttpContext) (t: Texts) (m: DnaModel) =
    div(class' = "space-y-10").data("dna", "") {
        header ctx t m

        match m.Figures with
        | None -> empty t m
        | Some f ->
            totals t f

            div(class' = "grid grid-cols-1 gap-4 lg:grid-cols-12") {
                match f.Insignia with
                | Some i -> insignia t m.Username i
                | None -> Fragment()

                pace t f.Pace
                studios t f
                decades t f
                genres t m.Username f
                formats t f
            }
    }
    |> Layout.page ctx t $"{t.DnaTitle} · {m.DisplayName}"

// ---------------------------------------------------------------------------
// Data access (research R4–R10). Minutes per episode: the whole duration when fully watched, else up
// to the playhead; episodes without a duration don't count. MyAnimeList figures use the deduplicated
// minutes: a dub and its original count once per episode number (keyed on the original season).
// ---------------------------------------------------------------------------

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private percent (part: int64) (whole: int64) =
    if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)

let private tryLoad (db: DbConfig) (username: string) (now: DateTimeOffset) : Task<DnaModel option> =
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
            // One statement, one row shape: (kind, name, ms, n, x1, x2, x3, t1, t2, t3, t4, t5)
            use cmd =
                new NpgsqlCommand(
                    """
                    WITH w AS (
                        SELECT e.id, e.series_id, e.series_title, e.thumbnail_url, e.episode_number, e.season_id,
                               w.fully_watched, w.watched_at,
                               CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                    ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END AS ms,
                               m.mal_id, coalesce(m.shared_from, e.season_id) AS original_season
                        FROM watch_events w
                        JOIN cr_episodes e ON e.id = w.cr_episode_id
                        LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                        WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                    ),
                    dna AS (
                        SELECT DISTINCT ON (dedup_key) *
                        FROM (SELECT w.*, CASE WHEN w.episode_number IS NULL OR w.original_season IS NULL THEN w.id
                                              ELSE w.original_season || '#' || w.episode_number END AS dedup_key
                              FROM w) k
                        ORDER BY dedup_key, ms DESC, id
                    ),
                    titles AS (
                        SELECT d.series_id, coalesce(max(s.title), min(d.series_title), d.series_id) AS title
                        FROM dna d LEFT JOIN cr_series s ON s.id = d.series_id
                        GROUP BY d.series_id
                    ),
                    genre_series AS (
                        SELECT tg.name, d.series_id, sum(d.ms)::bigint AS ms
                        FROM dna d
                        JOIN mal_anime_tags at ON at.mal_id = d.mal_id AND at.kind = 'genre'
                        JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                        GROUP BY tg.name, d.series_id
                    ),
                    genre_top AS (
                        SELECT DISTINCT ON (g.name) g.name, g.series_id, ti.title
                        FROM genre_series g JOIN titles ti ON ti.series_id = g.series_id
                        ORDER BY g.name, g.ms DESC, ti.title, g.series_id
                    ),
                    insignia AS (
                        SELECT d.series_id, ti.title, sum(d.ms)::bigint AS ms, count(*) FILTER (WHERE d.fully_watched) AS n
                        FROM dna d JOIN titles ti ON ti.series_id = d.series_id
                        GROUP BY d.series_id, ti.title
                        ORDER BY sum(d.ms) DESC, ti.title, d.series_id
                        LIMIT 1
                    ),
                    insignia_season AS (
                        SELECT d.mal_id FROM dna d JOIN insignia i ON i.series_id = d.series_id
                        WHERE d.mal_id IS NOT NULL
                        GROUP BY d.mal_id ORDER BY sum(d.ms) DESC, d.mal_id LIMIT 1
                    )
                    SELECT 'totals'::text, NULL::text, (SELECT coalesce(sum(ms), 0) FROM w)::bigint,
                           (SELECT count(*) FROM w WHERE fully_watched)::bigint,
                           coalesce(sum(d.ms), 0)::bigint,
                           coalesce(sum(d.ms) FILTER (WHERE d.mal_id IS NOT NULL), 0)::bigint,
                           coalesce(sum(d.ms) FILTER (WHERE coalesce(a.year, extract(year FROM a.aired_from)::int) IS NOT NULL), 0)::bigint,
                           NULL::text, NULL::text, NULL::text, NULL::text, NULL::text
                    FROM dna d LEFT JOIN mal_anime a ON a.mal_id = d.mal_id
                    UNION ALL
                    (SELECT 'studio', tg.name, sum(d.ms)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                     FROM dna d
                     JOIN mal_anime_tags at ON at.mal_id = d.mal_id AND at.kind = 'studio'
                     JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                     GROUP BY tg.name ORDER BY sum(d.ms) DESC, tg.name LIMIT 2)
                    UNION ALL
                    SELECT 'decade', (coalesce(a.year, extract(year FROM a.aired_from)::int) / 10 * 10)::text, sum(d.ms)::bigint,
                           NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    FROM dna d JOIN mal_anime a ON a.mal_id = d.mal_id
                    WHERE coalesce(a.year, extract(year FROM a.aired_from)::int) IS NOT NULL
                    GROUP BY 2
                    UNION ALL
                    SELECT 'genre', g.name, sum(g.ms)::bigint, NULL, NULL, NULL, NULL, gt.series_id, gt.title, NULL, NULL, NULL
                    FROM genre_series g JOIN genre_top gt ON gt.name = g.name
                    GROUP BY g.name, gt.series_id, gt.title
                    UNION ALL
                    SELECT 'format', a.type, sum(d.ms)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    FROM dna d JOIN mal_anime a ON a.mal_id = d.mal_id
                    WHERE a.type IS NOT NULL
                    GROUP BY a.type
                    UNION ALL
                    SELECT 'insignia', i.title, i.ms, i.n, a.episodes::bigint, a.year::bigint, NULL, i.series_id,
                           coalesce(a.image_url,
                                    (SELECT d.thumbnail_url FROM dna d
                                     WHERE d.series_id = i.series_id AND d.thumbnail_url IS NOT NULL
                                     ORDER BY d.watched_at DESC, d.id LIMIT 1)),
                           a.type,
                           (SELECT tg.name FROM mal_anime_tags at JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                            WHERE at.mal_id = a.mal_id AND at.kind = 'studio' ORDER BY at.position LIMIT 1),
                           a.season
                    FROM insignia i
                    LEFT JOIN insignia_season s ON true
                    LEFT JOIN mal_anime a ON a.mal_id = s.mal_id
                    UNION ALL
                    SELECT 'day', NULL, NULL, count(*)::bigint,
                           floor(extract(epoch FROM (@now - d.watched_at)) / 86400)::bigint, NULL, NULL,
                           NULL, NULL, NULL, NULL, NULL
                    FROM dna d
                    WHERE d.fully_watched AND d.watched_at > @now - interval '90 days' AND d.watched_at <= @now
                    GROUP BY 5
                    """,
                    connection
                )

            cmd.Parameters.AddWithValue("user_id", userId) |> ignore
            cmd.Parameters.AddWithValue("now", now) |> ignore

            let! rows =
                task {
                    use! r = cmd.ExecuteReaderAsync()
                    let rows = ResizeArray()

                    let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
                    let int64' (i: int) = if r.IsDBNull i then None else Some(r.GetInt64 i)

                    while r.Read() do
                        rows.Add(
                            r.GetString 0,
                            text 1,
                            int64' 2,
                            int64' 3,
                            (int64' 4, int64' 5, int64' 6),
                            (text 7, text 8, text 9, text 10, text 11)
                        )

                    return List.ofSeq rows
                }

            let ofKind kind =
                rows |> List.filter (fun (k, _, _, _, _, _) -> k = kind)

            let allMs, fullyWatched, dedupMs, matchedMs, yearMs =
                match ofKind "totals" with
                | (_, _, Some all, Some n, (Some dedup, Some matched, Some year), _) :: _ -> all, n, dedup, matched, year
                | _ -> 0L, 0L, 0L, 0L, 0L

            let shares kind whole =
                ofKind kind
                |> List.choose (fun (_, name, ms, _, _, _) ->
                    match name, ms with
                    | Some n, Some ms -> Some(n, ms)
                    | _ -> None)
                |> List.sortBy (fun (n, ms) -> -ms, n)
                |> List.map (fun (n, ms) -> { Name = n; Share = percent ms whole })

            // Genres: shares over the sum of genre minutes, top 5 + "Others" (research R6)
            let genreRows =
                ofKind "genre"
                |> List.choose (fun (_, name, ms, _, _, (top, title, _, _, _)) ->
                    match name, ms with
                    | Some n, Some ms -> Some(n, ms, Option.map2 (fun a b -> a, b) top title)
                    | _ -> None)
                |> List.sortBy (fun (n, ms, _) -> -ms, n)

            let genreTotal = genreRows |> List.sumBy (fun (_, ms, _) -> ms)
            let top5 = genreRows |> List.truncate 5
            let others = genreTotal - (top5 |> List.sumBy (fun (_, ms, _) -> ms))

            let genres =
                [ for n, ms, top in top5 ->
                      { Name = n
                        Share = percent ms genreTotal
                        Top = top
                        IsOthers = false }
                  if others > 0L then
                      yield { Name = "others"; Share = percent others genreTotal; Top = None; IsOthers = true } ]

            let insignia =
                match ofKind "insignia" with
                | (_, Some title, Some ms, Some n, (episodes, year, _), (Some seriesId, poster, format, studio, season)) :: _ ->
                    Some
                        { SeriesId = seriesId
                          Title = title
                          Poster = poster
                          Format = format
                          Studio = studio
                          Season = season
                          Year = year |> Option.map int
                          FullyWatched = n
                          MalEpisodes = episodes |> Option.map int |> Option.filter (fun e -> e > 0)
                          Hours = round1 (decimal ms / 3_600_000m) }
                | _ -> None

            let perDay = Array.zeroCreate<int64> 90

            for _, _, _, n, (index, _, _), _ in ofKind "day" do
                match n, index with
                | Some n, Some i when i >= 0L && i < 90L -> perDay[int i] <- perDay[int i] + n
                | _ -> ()

            let paceEpisodes = Array.sum perDay
            let activeWeeks = perDay |> Array.chunkBySize 7 |> Array.filter (Array.exists (fun n -> n > 0L)) |> Array.length
            let hours = decimal allMs / 3_600_000m

            return
                Some
                    { Username = username
                      DisplayName = displayName
                      FullyWatched = fullyWatched
                      Figures =
                        if fullyWatched < minimumEpisodes then
                            None
                        else
                            Some
                                { Hours = round1 hours
                                  Days = round1 (hours / 24m)
                                  Episodes = fullyWatched
                                  Coverage = percent matchedMs dedupMs
                                  YearCoverage = percent yearMs dedupMs
                                  Studios = shares "studio" matchedMs
                                  Decades = shares "decade" yearMs
                                  Genres = genres
                                  Formats = shares "format" matchedMs
                                  Insignia = insignia
                                  Pace =
                                    { Episodes = paceEpisodes
                                      PerDay = round1 (decimal paceEpisodes / 90m)
                                      ActiveWeeks = activeWeeks
                                      PerDayCounts = perDay } } }
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (rawUsername: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()
            let now = ctx.GetService<TimeProvider>().GetUtcNow()

            let! model =
                match Username.tryCreate rawUsername with
                | Some username -> tryLoad (ctx.GetService<DbConfig>()) username now
                | None -> Task.FromResult None

            match model with
            | Some m -> return! ctx.WriteHtmlView(view ctx t m)
            | None ->
                ctx.SetStatusCode 404
                return! ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}/dna" handler ]
