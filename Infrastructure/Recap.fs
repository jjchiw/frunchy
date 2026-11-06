/// Watch streaks and the weekly recap (feature 029): what the user did over the last 7 days, and how many UTC days in a
/// row they watched. Both are computed at request time from `watch_events`; nothing is stored. The days are UTC calendar
/// days, like the year page (005), the airing seasons (027) and the watch updates (028).
module FrunchyBackend.Infrastructure.Recap

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes

/// A run of consecutive UTC streak days, both ends included
type Run = { From: DateOnly; To: DateOnly; Days: int }

/// The current streak in days (0 when it is over) and the longest run ever (None without a single streak day)
type Streaks = { Current: int; Longest: Run option }

/// The recap's figures over the window; `New` + `Continued` = `Series`. `Any` is whether the window holds at least one
/// watch event of known length (fully watched or not)
type Week =
    { Episodes: int64
      Minutes: int64
      Series: int64
      New: int64
      Continued: int64
      Any: bool }

/// The recap's window: the last 7 days, the charts' week (feature 012), so the card is never empty because a week began
let weekWindow = TimeSpan.FromDays 7.0

/// The streak rule. A streak isn't broken until a whole UTC day passes without watching, so the current streak is the
/// run that ends today or yesterday, else 0. The longest is the longest run; a tie goes to the latest. Pure.
let ofRuns (today: DateOnly) (runs: Run list) : Streaks =
    let current =
        runs
        |> List.tryFind (fun r -> r.To = today || r.To = today.AddDays -1)
        |> Option.map (fun r -> r.Days)
        |> Option.defaultValue 0

    let longest =
        match runs with
        | [] -> None
        | _ -> runs |> List.sortByDescending (fun r -> r.Days, r.To) |> List.head |> Some

    { Current = current; Longest = longest }

let private timestamp (name: string) (value: DateTimeOffset) =
    let p = NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
    p.Value <- value.UtcDateTime
    p

/// The last 7 days by the dashboard's own rules, undeduplicated (so the figures read like the tiles above them).
/// One statement. A series is *new* when the user has no watch event of any of its episodes (an unresolved one
/// included) before the window, *continued* otherwise.
let loadWeek (connection: NpgsqlConnection) (userId: Guid) (now: DateTimeOffset) : Task<Week> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH win AS (
                    SELECT w.fully_watched, w.playhead_s, e.duration_ms, e.series_id
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL AND w.watched_at >= @from
                ),
                fs AS (SELECT DISTINCT series_id FROM win WHERE fully_watched)
                SELECT count(*) FILTER (WHERE fully_watched),
                       round(coalesce(sum(CASE WHEN fully_watched THEN duration_ms::bigint
                                               ELSE least(playhead_s::bigint * 1000, duration_ms) END), 0)::numeric / 60000)::bigint,
                       (SELECT count(*) FROM fs),
                       (SELECT count(*) FROM fs
                        WHERE NOT EXISTS (SELECT 1 FROM watch_events p
                                          JOIN cr_episodes pe ON pe.id = p.cr_episode_id
                                          WHERE p.user_id = @user_id AND pe.series_id = fs.series_id AND p.watched_at < @from)),
                       count(*)
                FROM win
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.Add(timestamp "from" (now - weekWindow)) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        r.Read() |> ignore
        let series = r.GetInt64 2
        let fresh = r.GetInt64 3

        return
            { Episodes = r.GetInt64 0
              Minutes = r.GetInt64 1
              Series = series
              New = fresh
              Continued = series - fresh
              Any = r.GetInt64 4 > 0L }
    }

/// The user's runs of consecutive UTC days with at least one fully watched episode of known length, by the streak rule.
/// One statement: the distinct UTC days, islands by `day - row_number()`.
let loadStreaks (connection: NpgsqlConnection) (userId: Guid) (now: DateTimeOffset) : Task<Streaks> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH days AS (
                    SELECT DISTINCT (w.watched_at AT TIME ZONE 'UTC')::date AS day
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id = @user_id AND w.fully_watched AND e.duration_ms IS NOT NULL
                ),
                islands AS (
                    SELECT day, day - (row_number() OVER (ORDER BY day))::int AS island FROM days
                )
                SELECT min(day), max(day), count(*)::int
                FROM islands
                GROUP BY island
                ORDER BY min(day)
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let runs = ResizeArray<Run>()

        while! r.ReadAsync() do
            runs.Add
                { From = r.GetFieldValue<DateOnly> 0
                  To = r.GetFieldValue<DateOnly> 1
                  Days = r.GetInt32 2 }

        return ofRuns (DateOnly.FromDateTime now.UtcDateTime) (List.ofSeq runs)
    }
