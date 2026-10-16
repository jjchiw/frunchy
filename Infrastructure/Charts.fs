/// Charts and trends (feature 012), shared by the charts page, the anime page and the episode page the
/// way `Compatibility` is shared. Nothing is stored: every chart is one SQL statement over `watch_events`.
///
/// Privacy: no row and no count leaves this module for fewer than `minPeople` distinct users. The
/// minimum is enforced in SQL (`HAVING count(DISTINCT user_id) >= @min`) and in the count functions, so a
/// caller can't forget it. No user identity is ever selected; `Mine` is only a boolean about the viewer.
module FrunchyBackend.Infrastructure.Charts

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Model (specs/012-charts-trends/data-model.md)
// ---------------------------------------------------------------------------

type Window =
    | Week
    | Season
    | AllTime

type Move =
    | Up of int
    | Down of int
    | Same
    | New

/// The picture of a row: a MyAnimeList image is portrait, a Crunchyroll episode thumbnail is landscape
type Poster =
    | Portrait of string
    | Thumbnail of string

type ChartRow =
    { SeriesId: string
      Title: string
      Slug: string option
      Poster: Poster option
      /// Distinct users with at least one watched episode of known length in the window
      People: int
      /// Watched episodes of known length in the window, over all those users
      Episodes: int
      /// The viewer watched this series at any time (always false when signed out)
      Mine: bool
      /// Week chart only
      Move: Move option
      /// Week chart only: 8 weekly people counts, oldest first
      Trend: int list }

type Chart =
    { Window: Window
      /// The season's lowercase name (winter, spring, summer, fall) and year, Season window only
      Season: (string * int) option
      MinPeople: int
      Rows: ChartRow list }

// ---------------------------------------------------------------------------
// Pure functions
// ---------------------------------------------------------------------------

let defaultMinPeople = 3

/// The configured minimum from its raw text: missing or not a number is 3, anything below 2 becomes 2
let parseMinPeople (raw: string option) : int =
    match raw with
    | Some text ->
        match Int32.TryParse(text.Trim()) with
        | true, n -> max 2 n
        | _ -> defaultMinPeople
    | None -> defaultMinPeople

/// `Frunchy:Charts:MinPeople`, read per call
let minPeople (config: IConfiguration) : int =
    parseMinPeople (config["Frunchy:Charts:MinPeople"] |> Option.ofObj)

/// The MyAnimeList season of an instant, by UTC quarter: (lowercase name, year, the quarter's first
/// instant at 00:00 UTC). Winter is January to March.
let seasonOf (now: DateTimeOffset) : string * int * DateTimeOffset =
    let utc = now.UtcDateTime
    let quarter = (utc.Month - 1) / 3

    let name =
        match quarter with
        | 0 -> "winter"
        | 1 -> "spring"
        | 2 -> "summer"
        | _ -> "fall"

    name, utc.Year, DateTimeOffset(DateTime(utc.Year, quarter * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc))

/// How a series moved against last week: its previous rank (1-based) against its current one.
/// A series missing from the previous ranks (it was below the minimum, or unwatched) is new.
let movement (previous: Map<string, int>) (seriesId: string) (rank: int) : Move =
    match previous.TryFind seriesId with
    | None -> New
    | Some before when before > rank -> Up(before - rank)
    | Some before when before < rank -> Down(rank - before)
    | Some _ -> Same

/// The 8 weekly blocks of the trend as [from, to) pairs, oldest first. The newest ends at `now`.
let trendBlocks (now: DateTimeOffset) : (DateTimeOffset * DateTimeOffset) list =
    [ for k in 7..-1..0 -> now.AddDays(-7.0 * float (k + 1)), now.AddDays(-7.0 * float k) ]

// ---------------------------------------------------------------------------
// SQL
// ---------------------------------------------------------------------------

let private timestamp (name: string) (value: DateTimeOffset option) =
    let p = NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
    p.Value <- (match value with Some v -> box v.UtcDateTime | None -> box DBNull.Value)
    p

let private optionalString (r: Data.Common.DbDataReader) (i: int) = if r.IsDBNull i then None else Some(r.GetString i)

/// The window's rows: the top 20 (people, episodes, title, series id) with at least `@min` people.
/// The viewer id is only compared inside `mine`; NULL when signed out. The poster is the MyAnimeList
/// image of the series' most watched matched season in the window, else its newest episode thumbnail.
/// Last week's ranking with no LIMIT, so a series that ranked 25th still has its rank
/// The people per weekly block for the charted series. The 8 blocks come from `trendBlocks`, so SQL and
/// F# share one definition (`@froms`/`@tos`, ordinal k = 1..8, oldest first); empty weeks are 0. The
/// minimum filters which series are charted, not these weekly values.
let private loadRows
    (connection: NpgsqlConnection)
    (from: DateTimeOffset option)
    (until: DateTimeOffset option)
    (minPeople: int)
    (viewer: Guid option)
    : Task<ChartRow list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
    WITH win AS (
        SELECT w.user_id, e.series_id, e.series_title, e.id AS episode_id, e.season_id, e.thumbnail_url, w.watched_at
        FROM watch_events w
        JOIN cr_episodes e ON e.id = w.cr_episode_id
        WHERE e.duration_ms IS NOT NULL
          AND (@from IS NULL OR w.watched_at >= @from)
          AND (@to IS NULL OR w.watched_at < @to)
    ),
    ranked AS (
        SELECT series_id, count(DISTINCT user_id) AS people, count(*) AS episodes, min(series_title) AS episode_title
        FROM win
        GROUP BY series_id
        HAVING count(DISTINCT user_id) >= @min
    ),
    top AS (
        SELECT r.series_id, r.people, r.episodes,
               coalesce(s.title, r.episode_title, r.series_id) AS title, s.slug
        FROM ranked r
        LEFT JOIN cr_series s ON s.id = r.series_id
        ORDER BY r.people DESC, r.episodes DESC, coalesce(s.title, r.episode_title, r.series_id), r.series_id
        LIMIT 20
    ),
    portrait AS (
        SELECT DISTINCT ON (t.series_id) t.series_id, a.image_url
        FROM top t
        JOIN win ON win.series_id = t.series_id
        JOIN cr_season_matches m ON m.season_id = win.season_id AND m.status = 'matched'
        JOIN mal_anime a ON a.mal_id = m.mal_id AND a.image_url IS NOT NULL
        GROUP BY t.series_id, a.mal_id, a.image_url
        ORDER BY t.series_id, count(*) DESC, a.mal_id
    ),
    thumbnail AS (
        SELECT DISTINCT ON (t.series_id) t.series_id, win.thumbnail_url
        FROM top t
        JOIN win ON win.series_id = t.series_id
        WHERE win.thumbnail_url IS NOT NULL
        ORDER BY t.series_id, win.watched_at DESC, win.episode_id
    )
    SELECT t.series_id, t.people, t.episodes, t.title, t.slug, p.image_url, th.thumbnail_url,
           EXISTS (SELECT 1
                   FROM watch_events mw
                   JOIN cr_episodes me ON me.id = mw.cr_episode_id
                   WHERE mw.user_id = @viewer AND me.series_id = t.series_id AND me.duration_ms IS NOT NULL) AS mine
    FROM top t
    LEFT JOIN portrait p ON p.series_id = t.series_id
    LEFT JOIN thumbnail th ON th.series_id = t.series_id
    ORDER BY t.people DESC, t.episodes DESC, t.title, t.series_id
    """,
                connection
            )
        cmd.Parameters.Add(timestamp "from" from) |> ignore
        cmd.Parameters.Add(timestamp "to" until) |> ignore
        cmd.Parameters.AddWithValue("min", minPeople) |> ignore
        let viewerParameter = NpgsqlParameter("viewer", NpgsqlDbType.Uuid)
        viewerParameter.Value <- (match viewer with Some id -> box id | None -> box DBNull.Value)
        cmd.Parameters.Add viewerParameter |> ignore

        let rows = ResizeArray<ChartRow>()
        use! r = cmd.ExecuteReaderAsync()

        while! r.ReadAsync() do
            let poster =
                match optionalString r 5, optionalString r 6 with
                | Some image, _ -> Some(Portrait image)
                | None, Some thumbnail -> Some(Thumbnail thumbnail)
                | None, None -> None

            rows.Add
                { SeriesId = r.GetString 0
                  Title = r.GetString 3
                  Slug = optionalString r 4
                  Poster = poster
                  People = int (r.GetInt64 1)
                  Episodes = int (r.GetInt64 2)
                  Mine = r.GetBoolean 7
                  Move = None
                  Trend = [] }

        return List.ofSeq rows
    }

let private loadPreviousRanks (connection: NpgsqlConnection) (now: DateTimeOffset) (minPeople: int) : Task<Map<string, int>> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
    WITH win AS (
        SELECT w.user_id, e.series_id, e.series_title
        FROM watch_events w
        JOIN cr_episodes e ON e.id = w.cr_episode_id
        WHERE e.duration_ms IS NOT NULL AND w.watched_at >= @from AND w.watched_at < @to
    ),
    ranked AS (
        SELECT series_id, count(DISTINCT user_id) AS people, count(*) AS episodes, min(series_title) AS episode_title
        FROM win
        GROUP BY series_id
        HAVING count(DISTINCT user_id) >= @min
    )
    SELECT r.series_id,
           row_number() OVER (ORDER BY r.people DESC, r.episodes DESC,
                                       coalesce(s.title, r.episode_title, r.series_id), r.series_id) AS rank
    FROM ranked r
    LEFT JOIN cr_series s ON s.id = r.series_id
    """,
                connection
            )
        cmd.Parameters.Add(timestamp "from" (Some(now.AddDays -14.0))) |> ignore
        cmd.Parameters.Add(timestamp "to" (Some(now.AddDays -7.0))) |> ignore
        cmd.Parameters.AddWithValue("min", minPeople) |> ignore

        let mutable ranks = Map.empty
        use! r = cmd.ExecuteReaderAsync()

        while! r.ReadAsync() do
            ranks <- ranks.Add(r.GetString 0, int (r.GetInt64 1))

        return ranks
    }

let private loadTrends (connection: NpgsqlConnection) (now: DateTimeOffset) (seriesIds: string[]) : Task<Map<string, int list>> =
    task {
        let blocks = trendBlocks now

        use cmd =
            new NpgsqlCommand(
                """
    SELECT s.series_id, b.k, count(DISTINCT x.user_id) AS people
    FROM unnest(@series) AS s(series_id)
    CROSS JOIN unnest(@froms, @tos) WITH ORDINALITY AS b(f, t, k)
    LEFT JOIN (SELECT w.user_id, e.series_id, w.watched_at
               FROM watch_events w
               JOIN cr_episodes e ON e.id = w.cr_episode_id
               WHERE e.duration_ms IS NOT NULL AND e.series_id = ANY(@series)
                 AND w.watched_at >= @all_from AND w.watched_at < @all_to) x
           ON x.series_id = s.series_id AND x.watched_at >= b.f AND x.watched_at < b.t
    GROUP BY s.series_id, b.k
    """,
                connection
            )
        cmd.Parameters.AddWithValue("series", seriesIds) |> ignore

        let timestamps (name: string) (values: DateTimeOffset list) =
            let p = NpgsqlParameter(name, NpgsqlDbType.Array ||| NpgsqlDbType.TimestampTz)
            p.Value <- values |> List.map _.UtcDateTime |> Array.ofList
            p

        cmd.Parameters.Add(timestamps "froms" (blocks |> List.map fst)) |> ignore
        cmd.Parameters.Add(timestamps "tos" (blocks |> List.map snd)) |> ignore
        cmd.Parameters.Add(timestamp "all_from" (blocks |> List.tryHead |> Option.map fst)) |> ignore
        cmd.Parameters.Add(timestamp "all_to" (blocks |> List.tryLast |> Option.map snd)) |> ignore

        let counts = Dictionary<string, int[]>()
        use! r = cmd.ExecuteReaderAsync()

        while! r.ReadAsync() do
            let id = r.GetString 0

            let values =
                match counts.TryGetValue id with
                | true, v -> v
                | _ ->
                    let v = Array.zeroCreate blocks.Length
                    counts[id] <- v
                    v

            values[int (r.GetInt64 1) - 1] <- int (r.GetInt64 2)

        return counts |> Seq.map (fun kv -> kv.Key, List.ofArray kv.Value) |> Map.ofSeq
    }

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/// The chart of a window, up to 20 rows. `now` fixes the window ends (tests pass a fixed clock); `viewer`
/// is the signed-in user's id, only used to flag the series they watched. The week chart also carries
/// each row's movement against the previous 7 days and its 8-week trend.
let chart (db: DbConfig) (window: Window) (minPeople: int) (now: DateTimeOffset) (viewer: Guid option) : Task<Chart> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        let minPeople = max 2 minPeople

        match window with
        | Week ->
            let! rows = loadRows connection (Some(now.AddDays -7.0)) (Some now) minPeople viewer
            let! previous = loadPreviousRanks connection now minPeople

            let! trends =
                if rows.IsEmpty then
                    Task.FromResult Map.empty
                else
                    loadTrends connection now (rows |> List.map _.SeriesId |> Array.ofList)

            let rows =
                rows
                |> List.mapi (fun i row ->
                    { row with
                        Move = Some(movement previous row.SeriesId (i + 1))
                        Trend = trends.TryFind row.SeriesId |> Option.defaultValue (List.replicate 8 0) })

            return { Window = Week; Season = None; MinPeople = minPeople; Rows = rows }
        | Season ->
            let name, year, start = seasonOf now
            let! rows = loadRows connection (Some start) (Some(start.AddMonths 3)) minPeople viewer
            return { Window = Season; Season = Some(name, year); MinPeople = minPeople; Rows = rows }
        | AllTime ->
            let! rows = loadRows connection None None minPeople viewer
            return { Window = AllTime; Season = None; MinPeople = minPeople; Rows = rows }
    }

let private countOrNone (minPeople: int) (people: int64) : int option =
    if people >= int64 (max 2 minPeople) then Some(int people) else None

/// Distinct users, over all time, with at least one watched episode of known length of the series.
/// None below the minimum, so a caller can't print a count that points at one person.
let seriesPeople (db: DbConfig) (seriesId: string) (minPeople: int) : Task<int option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd =
            new NpgsqlCommand(
                """
    SELECT count(DISTINCT w.user_id)
    FROM watch_events w
    JOIN cr_episodes e ON e.id = w.cr_episode_id
    WHERE e.series_id = @series_id AND e.duration_ms IS NOT NULL
    """,
                connection
            )
        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore
        let! people = cmd.ExecuteScalarAsync()
        return countOrNone minPeople (unbox<int64> people)
    }

/// The same for a group of episodes (an episode and its dubs): users who watched any of them
let episodePeople (db: DbConfig) (episodeIds: string list) (minPeople: int) : Task<int option> =
    task {
        if episodeIds.IsEmpty then
            return None
        else
            use connection = getConnection db
            do! connection.OpenAsync()
            use cmd =
                new NpgsqlCommand(
                    """
    SELECT count(DISTINCT w.user_id)
    FROM watch_events w
    JOIN cr_episodes e ON e.id = w.cr_episode_id
    WHERE e.id = ANY(@episode_ids) AND e.duration_ms IS NOT NULL
    """,
                    connection
                )
            cmd.Parameters.AddWithValue("episode_ids", Array.ofList episodeIds) |> ignore
            let! people = cmd.ExecuteScalarAsync()
            return countOrNone minPeople (unbox<int64> people)
    }
