module FrunchyBackend.Handlers.Users.Year

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
// Model (specs/005-year-in-review/data-model.md)
// ---------------------------------------------------------------------------

/// A year's figures appear from this many fully watched episodes in it (FR-011)
let minimumEpisodes = 15L

/// Years before this are never valid (FR-003)
let firstYear = 2000

/// A named share of the year's watched seasons (feature 027), in percent with 1 decimal
type ShareRow = { Name: string; Share: decimal; Seasons: int64 }

type GenreRow =
    { Name: string
      Share: decimal
      Seasons: int64
      /// (Crunchyroll series id, title) with the most watched seasons in the genre; None on "Others" and on a genre without one
      Top: (string * string) option
      /// The closing "Others" row, which stands for every genre outside the top five
      IsOthers: bool }

type TopSeries =
    { SeriesId: string
      Title: string
      Poster: string option
      FullyWatched: int64 }

type Streak = { Days: int; From: DateOnly; To: DateOnly }

type Figures =
    { Hours: decimal
      Days: decimal
      Episodes: int64
      Series: int64
      /// Watched seasons of the year, with and without a MyAnimeList entry (what the cards' notes are based on)
      SeasonsTotal: int64
      /// Watched seasons with a MyAnimeList entry: the whole of the studio and format shares
      SeasonsMatched: int64
      /// Minutes per month, index 0 = January
      Months: int64 array
      TopSeries: TopSeries option
      NewSeries: int64
      ContinuedSeries: int64
      Streak: Streak option
      Genres: GenreRow list
      Studios: ShareRow list
      Formats: ShareRow list }

type YearModel =
    { Username: string
      DisplayName: string
      Year: int
      /// Years with history, newest first
      Years: int list
      /// The first month that hasn't happened yet, when the year is the current one (2..12)
      FirstFutureMonth: int option
      FullyWatched: int64
      /// None below minimumEpisodes (the page-level "not enough data yet")
      Figures: Figures option
      /// What the page says about who can see it (feature 017)
      Access: Profiles.PageAccess }

// ---------------------------------------------------------------------------
// View (contracts/year-page.md)
// ---------------------------------------------------------------------------

/// Month name in the page's language, capitalised (Spanish month names are lowercase)
let private monthName (t: Texts) (month: int) =
    let name = t.Culture.DateTimeFormat.GetMonthName month
    t.Culture.TextInfo.ToUpper(name[0]).ToString() + name.Substring 1

/// Abbreviated month name without the trailing dot some cultures add ("sept.")
let private shortMonth (t: Texts) (month: int) =
    let name = t.Culture.DateTimeFormat.GetAbbreviatedMonthName(month).TrimEnd('.')
    t.Culture.TextInfo.ToUpper(name[0]).ToString() + name.Substring 1

/// A UTC date as a plain day and month (not through the client-side time script, which would shift it)
let private day (t: Texts) (d: DateOnly) = d.ToString("d MMM", t.Culture)

let private header (ctx: HttpContext) (t: Texts) (m: YearModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}/year/{m.Year}"

    Fragment() {
        profileTop t ProfileTab.Year m.Username m.DisplayName (Some(m.Access, address))

        div(class' = "space-y-4") {
            div(class' = "space-y-2") {
                h1(class' = "text-headline-lg text-on-surface text-balance").data("year-user", m.DisplayName) { t.YearTitle m.Year }

                p(class' = "max-w-prose text-body-lg text-on-surface-variant") { t.YearIntro }
            }

            if not m.Years.IsEmpty then
                yearPicker t.YearPickerLabel m.Username m.Years m.Year

            p(class' = "flex items-start gap-1.5 text-label-md font-normal text-on-surface-variant").data("rewatch-note", "") {
                icon Theme.Info
                span() { t.YearRewatchNote }
            }
        }
    }

let private totals (t: Texts) (f: Figures) =
    div(class' = "grid grid-cols-2 gap-4 lg:grid-cols-4").data("year-totals", "") {
        div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6") {
            p(class' = "fr-overline") { t.StatHours }

            div(class' = "text-stat-counter text-primary").data("stat", "hours").data("value", inv f.Hours) {
                f.Hours.ToString("N1", t.Culture)
            }

            p(class' = "text-label-md font-normal text-on-surface-variant").data("stat", "days").data("value", inv f.Days) {
                t.YearHoursCaption f.Days
            }
        }

        metricTile t.StatEpisodes "episodes" (string f.Episodes) (f.Episodes.ToString("N0", t.Culture)) None
        metricTile t.YearStatSeries "series" (string f.Series) (f.Series.ToString("N0", t.Culture)) None

        div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6").data("year-series-split", "").data("new", string f.NewSeries).data("continued", string f.ContinuedSeries) {
            p(class' = "fr-overline") { t.YearSplitTitle }
            p(class' = "text-body-lg text-on-surface") { t.YearNewSeries f.NewSeries }
            p(class' = "text-body-md text-on-surface-variant") { t.YearContinuedSeries f.ContinuedSeries }
        }
    }

let private topSeries (t: Texts) (username: string) (s: TopSeries) =
    (figureSection "year-top-series" t.YearTopSeriesTitle None "")
        .data("series", s.SeriesId)
        .data("episodes", string s.FullyWatched) {
        sectionHeading t.YearTopSeriesTitle

        div(class' = "flex flex-col gap-5 sm:flex-row lg:flex-col xl:flex-row") {
            match s.Poster with
            | Some url ->
                poster (Some url) "aspect-[2/3] w-32 shrink-0 rounded-md sm:w-40"
            | None -> Fragment()

            div(class' = "flex min-w-0 flex-col gap-2") {
                p(class' = "text-headline-lg text-on-surface text-balance") { seriesLink username s.SeriesId s.Title }
                p(class' = "text-body-md text-on-surface-variant") { t.YearTopSeriesEpisodes s.FullyWatched }
            }
        }
    }

let private months (t: Texts) (year: int) (firstFuture: int option) (f: Figures) =
    let peak = max 1L (Array.max f.Months)
    let busiest = f.Months |> Array.mapi (fun i ms -> i + 1, ms) |> Array.maxBy (fun (i, ms) -> ms, -i) |> fst
    let el = figureSection "year-months" t.YearMonthsTitle None ""

    (if Array.max f.Months > 0L then el.data("busiest", string busiest) else el) {
        sectionHeading t.YearMonthsTitle

        if Array.max f.Months > 0L then
            p(class' = "text-body-md text-primary") { t.YearBusiestMonth (monthName t busiest) (Math.Round(decimal f.Months[busiest - 1] / 3_600_000m, 1)) }

        div(class' = "flex h-40 items-end gap-1.5 sm:gap-2").attr("role", "img").attr("aria-label", t.YearMonthsChart year) {
            for m in 1..12 do
                let ms = f.Months[m - 1]
                let future = firstFuture |> Option.exists (fun first -> m >= first)
                let height = if ms = 0L then 3m else max 6m (100m * decimal ms / decimal peak)

                let column =
                    div(class' = "flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1.5")
                        .data("month", string m)
                        .data("minutes", string (ms / 60_000L))

                (if future then column.data("future", "") else column) {
                    // a month that hasn't happened yet: an outline over the whole height, not an empty bar
                    if future then
                        div(class' = "h-full w-full rounded-sm border border-dashed border-outline-variant")
                    else
                        div(class' = (if m = busiest && ms > 0L then "w-full rounded-sm bg-primary" elif ms = 0L then "w-full rounded-sm bg-surface-container-highest" else "w-full rounded-sm bg-primary/45"))
                            .attr("style", $"""height: {height.ToString("0.#", CultureInfo.InvariantCulture)}%%""")

                    // one letter on phones, where twelve abbreviations don't fit side by side
                    span(class' = (if future then "text-label-sm font-semibold uppercase text-outline" else "text-label-sm font-semibold uppercase text-on-surface-variant")).attr("aria-hidden", "true") {
                        span(class' = "sm:hidden") { (shortMonth t m).Substring(0, 1) }
                        span(class' = "hidden sm:inline") { shortMonth t m }
                    }
                }
        }
    }

let private streak (t: Texts) (f: Figures) =
    match f.Streak with
    | Some s ->
        (figureSection "year-streak" t.YearStreakTitle None "")
            .data("value", string s.Days)
            .data("from", s.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .data("to", s.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) {
            sectionHeading t.YearStreakTitle

            div(class' = "flex items-center gap-4") {
                span(class' = "flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-tertiary/10 text-headline-md text-tertiary") {
                    icon Theme.LocalFireDepartment
                }

                div() {
                    p(class' = "text-headline-lg text-on-surface") { t.YearStreakDays s.Days }
                    p(class' = "text-body-md text-on-surface-variant") { t.YearStreakRange (day t s.From) (day t s.To) }
                }
            }
        }
    | None ->
        (figureSection "year-streak" t.YearStreakTitle None "").data("value", "0") {
            sectionHeading t.YearStreakTitle
            p(class' = "text-body-md text-on-surface-variant") { t.YearStreakNone }
        }

let private seasonCoverage (f: Figures) =
    if f.SeasonsTotal = 0L then 0m else Math.Round(100m * decimal f.SeasonsMatched / decimal f.SeasonsTotal, 1, MidpointRounding.AwayFromZero)

let private genres (t: Texts) (username: string) (f: Figures) =
    let el = figureSection "year-genres" t.YearGenresTitle (Some(seasonCoverage f)) ""

    if f.Genres.IsEmpty then
        el {
            sectionHeading t.YearGenresTitle
            notEnough t
        }
    else
        el {
            div(class' = "space-y-1") {
                sectionHeading t.YearGenresTitle
                p(class' = "text-body-md text-on-surface-variant") { t.DnaGenresNote }
            }

            div(class' = "space-y-4") {
                for g in f.Genres do
                    let row =
                        div()
                            .data("genre", (if g.IsOthers then "others" else g.Name))
                            .data("share", inv g.Share)
                            .data("genre-seasons", string g.Seasons)

                    if g.IsOthers then
                        row { shareLine t.DnaOthers None g.Share t }
                    else
                        match g.Top with
                        | Some(seriesId, title) ->
                            row.data("top-series", seriesId) { shareLine g.Name (Some(span() { t.TopAnimePrefix; " "; seriesLink username seriesId title })) g.Share t }
                        | None -> row { shareLine g.Name None g.Share t }
            }

            seasonCoverageNote (t.DnaCoverage f.SeasonsMatched f.SeasonsTotal) f.SeasonsMatched f.SeasonsTotal
        }

let private shareSection (t: Texts) (hook: string) (rowHook: string) (title: string) (rows: ShareRow list) (f: Figures) (span': string) =
    let el = figureSection hook title (Some(seasonCoverage f)) span'

    if rows.IsEmpty then
        el {
            sectionHeading title
            notEnough t
        }
    else
        el {
            sectionHeading title

            div(class' = "space-y-4") {
                for i, x in List.indexed rows do
                    let row =
                        div()
                            .data(rowHook, (if rowHook = "rank" then string (i + 1) else x.Name))
                            .data("share", inv x.Share)
                            .data((if rowHook = "rank" then "studio-seasons" else "format-seasons"), string x.Seasons)

                    (if rowHook = "rank" then row.data("name", x.Name) else row) {
                        shareLine x.Name (Some(span() { t.DnaSeasonsCount x.Seasons })) x.Share t
                    }
            }

            seasonCoverageNote (t.DnaCoverage f.SeasonsMatched f.SeasonsTotal) f.SeasonsMatched f.SeasonsTotal
        }

let private empty (t: Texts) (m: YearModel) =
    div().data("year-empty", "").data("value", string m.FullyWatched) {
        emptyState
            Theme.CalendarMonth
            (t.YearEmptyTitle m.Year)
            t.YearEmptyBody
            (Some(p(class' = "text-label-md text-primary") { t.YearEmptyCount m.FullyWatched }))
    }

let view (ctx: HttpContext) (t: Texts) (m: YearModel) =
    div(class' = "space-y-8").data("year", string m.Year) {
        header ctx t m

        match m.Figures with
        | None -> empty t m
        | Some f ->
            totals t f

            // two columns that stack on their own, so no card stretches to its neighbour's height
            div(class' = "grid grid-cols-1 items-start gap-4 lg:grid-cols-12") {
                div(class' = "flex flex-col gap-4 lg:col-span-7") {
                    months t m.Year m.FirstFutureMonth f
                    genres t m.Username f
                    shareSection t "year-formats" "format" t.YearFormatsTitle f.Formats f ""
                }

                div(class' = "flex flex-col gap-4 lg:col-span-5") {
                    match f.TopSeries with
                    | Some s -> topSeries t m.Username s
                    | None -> Fragment()

                    streak t f
                    shareSection t "year-studios" "rank" t.YearStudiosTitle f.Studios f ""
                }
            }
    }
    |> Layout.page ctx t $"{t.YearTitle m.Year} · {m.DisplayName}"

// ---------------------------------------------------------------------------
// Data access (research R1–R6). The year is UTC. Totals follow the dashboard (not deduplicated),
// so every year adds up to it; the MyAnimeList figures (studio, genre, format) count the DNA page's watched seasons
// (feature 027), here the seasons of the episodes watched in the year.
// ---------------------------------------------------------------------------

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private percent (part: int64) (whole: int64) =
    if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)

type private User = { Id: Guid; DisplayName: string; Years: int list }

/// The user alone: the visibility check (feature 017) runs before any history query, so `Years` comes after it
let private findUser (connection: NpgsqlConnection) (username: string) : Task<(Guid * string) option> =
    task {
        use cmd = new NpgsqlCommand("SELECT id, display_name FROM users WHERE username = @username", connection)
        cmd.Parameters.AddWithValue("username", username) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        return if r.Read() then Some(r.GetGuid 0, r.GetString 1) else None
    }

/// Years with history, newest first
let private loadYears (connection: NpgsqlConnection) (userId: Guid) : Task<int list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT DISTINCT extract(year FROM w.watched_at AT TIME ZONE 'UTC')::int
                FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                ORDER BY 1 DESC
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let years = ResizeArray<int>()

        while r.Read() do
            years.Add(r.GetInt32 0)

        return List.ofSeq years
    }

/// The user, once the viewer is known to see their profile: `Error NotFound` for an unknown username and for a hidden
/// profile alike (the same 404), `Error SignInFirst` for a Members profile and a signed-out viewer (feature 021), with
/// no history query before the check
let private loadUser
    (ctx: HttpContext)
    (connection: NpgsqlConnection)
    (username: string)
    : Task<Result<User * Profiles.PageAccess, Profiles.GateResult>> =
    task {
        match! findUser connection username with
        | None -> return Error Profiles.NotFound
        | Some(id, displayName) ->
            match! Profiles.gate ctx (ctx.GetService<DbConfig>()) id with
            | Profiles.Show access ->
                let! years = loadYears connection id
                return Ok({ Id = id; DisplayName = displayName; Years = years }, access)
            | refused -> return Error refused
    }

let private loadYear (connection: NpgsqlConnection) (userId: Guid) (year: int) : Task<int64 * Figures option> =
    task {
        // One statement, one row shape: (kind, name, ms, n, x1, x2, t1, t2)
        use cmd =
            new NpgsqlCommand(
                """
                WITH w AS (
                    SELECT e.id, e.series_id, e.series_title, e.thumbnail_url, e.episode_number, e.season_id,
                           e.aired_at, w.fully_watched, w.watched_at, (w.watched_at AT TIME ZONE 'UTC') AS utc,
                           CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END AS ms,
                           m.mal_id, coalesce(m.shared_from, e.season_id) AS original_season
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                ),
                y AS (SELECT * FROM w WHERE extract(year FROM utc) = @year),
                keyed AS (
                    SELECT y.*, CASE WHEN y.episode_number IS NULL OR y.original_season IS NULL THEN y.id
                                     ELSE y.original_season || '#' || y.episode_number END AS dedup_key
                    FROM y
                ),
                -- A dub and its original are one episode: it aired when its earliest version did, and it was
                -- fully watched when any version was
                timed AS (
                    SELECT k.*, min(k.aired_at) OVER (PARTITION BY k.dedup_key) AS group_aired,
                           bool_or(k.fully_watched) OVER (PARTITION BY k.dedup_key) AS group_full
                    FROM keyed k
                ),
                -- season_no = year * 4 + quarter (0 = winter), the UTC quarter of the group's air date
                dna AS (
                    SELECT DISTINCT ON (t.dedup_key) t.*,
                           (extract(year FROM t.group_aired AT TIME ZONE 'UTC')::int * 4
                            + (extract(month FROM t.group_aired AT TIME ZONE 'UTC')::int - 1) / 3) AS season_no
                    FROM timed t
                    ORDER BY t.dedup_key, t.ms DESC, t.id
                ),
                titles AS (
                    SELECT d.series_id, coalesce(max(s.title), min(d.series_title), d.series_id) AS title
                    FROM dna d LEFT JOIN cr_series s ON s.id = d.series_id
                    GROUP BY d.series_id
                ),
                series_ms AS (SELECT d.series_id, sum(d.ms)::bigint AS ms FROM dna d GROUP BY d.series_id),
                -- A watched season: a MyAnimeList entry in an airing season (NULL only when none of its watched episodes has a date) in
                -- which the person fully watched an episode that year; unmatched ones count only in the totals
                ws AS (
                    SELECT DISTINCT d.mal_id, d.season_no FROM dna d
                    WHERE d.group_full AND d.mal_id IS NOT NULL
                      AND (d.season_no IS NOT NULL
                           OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id = d.mal_id AND x.season_no IS NOT NULL))
                ),
                ws_unmatched AS (
                    SELECT DISTINCT coalesce(d.original_season, d.series_id) AS cr_season, d.season_no
                    FROM dna d
                    WHERE d.group_full AND d.mal_id IS NULL
                      AND (d.season_no IS NOT NULL
                           OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id IS NULL AND x.season_no IS NOT NULL
                                          AND coalesce(x.original_season, x.series_id) = coalesce(d.original_season, d.series_id)))
                ),
                series_seasons AS (
                    SELECT DISTINCT d.series_id, d.mal_id, d.season_no FROM dna d
                    WHERE d.group_full AND d.mal_id IS NOT NULL
                      AND (d.season_no IS NOT NULL
                           OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id = d.mal_id AND x.season_no IS NOT NULL))
                ),
                genre_count AS (
                    SELECT tg.name, count(*)::bigint AS seasons
                    FROM ws
                    JOIN mal_anime_tags at ON at.mal_id = ws.mal_id AND at.kind = 'genre'
                    JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                    GROUP BY tg.name
                ),
                genre_series AS (
                    SELECT tg.name, ss.series_id, count(*)::bigint AS seasons
                    FROM series_seasons ss
                    JOIN mal_anime_tags at ON at.mal_id = ss.mal_id AND at.kind = 'genre'
                    JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                    GROUP BY tg.name, ss.series_id
                ),
                genre_top AS (
                    SELECT DISTINCT ON (g.name) g.name, g.series_id, ti.title
                    FROM genre_series g
                    JOIN titles ti ON ti.series_id = g.series_id
                    JOIN series_ms sm ON sm.series_id = g.series_id
                    ORDER BY g.name, g.seasons DESC, sm.ms DESC, ti.title, g.series_id
                ),
                top_series AS (
                    SELECT d.series_id, ti.title, sum(d.ms)::bigint AS ms, count(*) FILTER (WHERE d.fully_watched) AS n
                    FROM dna d JOIN titles ti ON ti.series_id = d.series_id
                    GROUP BY d.series_id, ti.title
                    ORDER BY sum(d.ms) DESC, ti.title, d.series_id
                    LIMIT 1
                ),
                top_season AS (
                    SELECT d.mal_id FROM dna d JOIN top_series s ON s.series_id = d.series_id
                    WHERE d.mal_id IS NOT NULL
                    GROUP BY d.mal_id ORDER BY sum(d.ms) DESC, d.mal_id LIMIT 1
                ),
                days AS (SELECT DISTINCT utc::date AS d FROM y WHERE fully_watched),
                runs AS (SELECT d, d - (row_number() OVER (ORDER BY d))::int AS g FROM days),
                series_year AS (SELECT DISTINCT series_id FROM y WHERE fully_watched),
                series_first AS (SELECT series_id, min(utc) AS first FROM w GROUP BY series_id)
                SELECT 'totals'::text, NULL::text,
                       (SELECT coalesce(sum(ms), 0) FROM y)::bigint,
                       (SELECT count(*) FROM y WHERE fully_watched)::bigint,
                       coalesce(sum(d.ms), 0)::bigint,
                       coalesce(sum(d.ms) FILTER (WHERE d.mal_id IS NOT NULL), 0)::bigint,
                       (SELECT count(*) FROM series_year)::text,
                       NULL::text
                FROM dna d
                UNION ALL
                SELECT 'split', NULL, NULL,
                       count(*) FILTER (WHERE extract(year FROM f.first) = @year)::bigint,
                       count(*) FILTER (WHERE extract(year FROM f.first) < @year)::bigint,
                       NULL, NULL, NULL
                FROM series_year s JOIN series_first f USING (series_id)
                UNION ALL
                SELECT 'month', extract(month FROM utc)::int::text, sum(ms)::bigint, NULL, NULL, NULL, NULL, NULL
                FROM y GROUP BY 2
                UNION ALL
                (SELECT 'streak', NULL, NULL, count(*)::bigint, NULL, NULL, min(d)::text, max(d)::text
                 FROM runs GROUP BY g ORDER BY count(*) DESC, min(d) LIMIT 1)
                UNION ALL
                SELECT 'seasons', NULL, ((SELECT count(*) FROM ws) + (SELECT count(*) FROM ws_unmatched))::bigint,
                       (SELECT count(*) FROM ws)::bigint, NULL, NULL, NULL, NULL
                UNION ALL
                (SELECT 'studio', tg.name, count(*)::bigint, NULL, NULL, NULL, NULL, NULL
                 FROM ws
                 JOIN mal_anime_tags at ON at.mal_id = ws.mal_id AND at.kind = 'studio'
                 JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                 GROUP BY tg.name ORDER BY count(*) DESC, tg.name LIMIT 3)
                UNION ALL
                SELECT 'genre', g.name, g.seasons, NULL, NULL, NULL, gt.series_id, gt.title
                FROM genre_count g JOIN genre_top gt ON gt.name = g.name
                UNION ALL
                SELECT 'format', a.type, count(*)::bigint, NULL, NULL, NULL, NULL, NULL
                FROM ws JOIN mal_anime a ON a.mal_id = ws.mal_id
                WHERE a.type IS NOT NULL
                GROUP BY a.type
                UNION ALL
                SELECT 'top', s.title, s.ms, s.n, NULL, NULL, s.series_id,
                       coalesce(a.image_url,
                                (SELECT d.thumbnail_url FROM dna d
                                 WHERE d.series_id = s.series_id AND d.thumbnail_url IS NOT NULL
                                 ORDER BY d.watched_at DESC, d.id LIMIT 1))
                FROM top_series s
                LEFT JOIN top_season ts ON true
                LEFT JOIN mal_anime a ON a.mal_id = ts.mal_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.AddWithValue("year", year) |> ignore

        let! rows =
            task {
                use! r = cmd.ExecuteReaderAsync()
                let rows = ResizeArray()
                let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
                let int64' (i: int) = if r.IsDBNull i then None else Some(r.GetInt64 i)

                while r.Read() do
                    rows.Add(r.GetString 0, text 1, int64' 2, int64' 3, (int64' 4, int64' 5), (text 6, text 7))

                return List.ofSeq rows
            }

        let ofKind kind =
            rows |> List.filter (fun (k, _, _, _, _, _) -> k = kind)

        let allMs, fullyWatched, series =
            match ofKind "totals" with
            | (_, _, Some all, Some n, _, (Some series, _)) :: _ -> all, n, int64 series
            | _ -> 0L, 0L, 0L

        let seasonsTotal, seasonsMatched =
            match ofKind "seasons" with
            | (_, _, Some total, Some matched, _, _) :: _ -> total, matched
            | _ -> 0L, 0L

        if fullyWatched < minimumEpisodes then
            return fullyWatched, None
        else
            // Shares over a whole of watched seasons, ranked by seasons, then name
            let shares kind whole =
                ofKind kind
                |> List.choose (fun (_, name, seasons, _, _, _) ->
                    match name, seasons with
                    | Some n, Some seasons -> Some(n, seasons)
                    | _ -> None)
                |> List.sortBy (fun (n, seasons) -> -seasons, n)
                |> List.map (fun (n, seasons) -> { Name = n; Share = percent seasons whole; Seasons = seasons })

            // Genres: shares over the sum of genre seasons, top 5 + "Others" (as on the DNA page)
            let genreRows =
                ofKind "genre"
                |> List.choose (fun (_, name, seasons, _, _, (top, title)) ->
                    match name, seasons with
                    | Some n, Some seasons -> Some(n, seasons, Option.map2 (fun a b -> a, b) top title)
                    | _ -> None)
                |> List.sortBy (fun (n, seasons, _) -> -seasons, n)

            let genreTotal = genreRows |> List.sumBy (fun (_, seasons, _) -> seasons)
            let top5 = genreRows |> List.truncate 5
            let others = genreTotal - (top5 |> List.sumBy (fun (_, seasons, _) -> seasons))

            let genres =
                [ for n, seasons, top in top5 ->
                      { Name = n
                        Share = percent seasons genreTotal
                        Seasons = seasons
                        Top = top
                        IsOthers = false }
                  if others > 0L then
                      yield
                          { Name = "others"
                            Share = percent others genreTotal
                            Seasons = others
                            Top = None
                            IsOthers = true } ]

            let monthsMs = Array.zeroCreate<int64> 12

            for _, name, ms, _, _, _ in ofKind "month" do
                match name, ms with
                | Some m, Some ms -> monthsMs[int m - 1] <- ms
                | _ -> ()

            let newSeries, continued =
                match ofKind "split" with
                | (_, _, _, Some n, (Some c, _), _) :: _ -> n, c
                | _ -> 0L, 0L

            let streak =
                match ofKind "streak" with
                | (_, _, _, Some n, _, (Some from, Some to')) :: _ ->
                    Some { Days = int n; From = DateOnly.Parse(from, CultureInfo.InvariantCulture); To = DateOnly.Parse(to', CultureInfo.InvariantCulture) }
                | _ -> None

            let topSeries =
                match ofKind "top" with
                | (_, Some title, _, Some n, _, (Some seriesId, poster)) :: _ ->
                    Some { SeriesId = seriesId; Title = title; Poster = poster; FullyWatched = n }
                | _ -> None

            let hours = decimal allMs / 3_600_000m

            return
                fullyWatched,
                Some
                    { Hours = round1 hours
                      Days = round1 (hours / 24m)
                      Episodes = fullyWatched
                      Series = series
                      SeasonsTotal = seasonsTotal
                      SeasonsMatched = seasonsMatched
                      Months = monthsMs
                      TopSeries = topSeries
                      NewSeries = newSeries
                      ContinuedSeries = continued
                      Streak = streak
                      Genres = genres
                      Studios = shares "studio" seasonsMatched
                      Formats = shares "format" seasonsMatched }
    }

// ---------------------------------------------------------------------------
// Handlers (research R7)
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) (t: Texts) (userMissing: bool) =
    ctx.SetStatusCode 404

    if userMissing then
        ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)
    else
        ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

let private currentYear (ctx: HttpContext) =
    ctx.GetService<TimeProvider>().GetUtcNow().UtcDateTime.Year

/// /u/{username}/year: the newest year with history, or the current year
let latest (rawUsername: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            match Username.tryCreate rawUsername with
            | None -> return! notFound ctx t true
            | Some username ->
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadUser ctx connection username with
                | Error Profiles.SignInFirst -> return! WebAuth.redirectToSignInHere ctx
                | Error _ -> return! notFound ctx t true
                | Ok(user, access) ->
                    let year = user.Years |> List.tryHead |> Option.defaultValue (currentYear ctx)
                    ctx.Response.Redirect $"/u/{username}/year/{year}"
                    return ()
        }

/// /u/{username}/year/{year}
let handler (rawUsername: string) (rawYear: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()
            let thisYear = currentYear ctx

            let year =
                match Int32.TryParse(rawYear, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, y when y >= firstYear && y <= thisYear -> Some y
                | _ -> None

            match Username.tryCreate rawUsername with
            | None -> return! notFound ctx t true
            | Some username ->
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadUser ctx connection username with
                | Error Profiles.SignInFirst -> return! WebAuth.redirectToSignInHere ctx
                | Error _ -> return! notFound ctx t true
                | Ok(user, access) ->
                    match year with
                    | None -> return! notFound ctx t false
                    | Some y when y <> thisYear && not (List.contains y user.Years) ->
                        ctx.Response.Redirect $"/u/{username}/year"
                        return ()
                    | Some y ->
                        let! fullyWatched, figures = loadYear connection user.Id y

                        let model =
                            { Username = username
                              DisplayName = user.DisplayName
                              Year = y
                              Years = user.Years
                              FirstFutureMonth =
                                (let today = ctx.GetService<TimeProvider>().GetUtcNow().UtcDateTime
                                 if y = today.Year && today.Month < 12 then Some(today.Month + 1) else None)
                              FullyWatched = fullyWatched
                              Figures = figures
                              Access = access }

                        return! ctx.WriteHtmlView(view ctx t model)
        }

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ routef "/u/{%s}/year" latest ]
      GET [ routef "/u/{%s}/year/{%s}" handler ] ]
