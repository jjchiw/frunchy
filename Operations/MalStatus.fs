module FrunchyBackend.Operations.MalStatus

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

type Status =
    { /// (status, method) → seasons; method "-" when not decided by one
      Seasons: ((string * string) * int64) list
      AnimeHeld: int64
      AnimeMissing: int64
      RefreshesDue: int64
      NextAttempt: DateTime option
      RecentErrors: string list }

let status (db: DbConfig) : Task<Status> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT status, coalesce(method, '-'), count(*) FROM cr_season_matches GROUP BY 1, 2 ORDER BY 1, 2;
                SELECT count(*), count(*) FILTER (WHERE missing_since IS NOT NULL),
                       count(*) FILTER (WHERE missing_since IS NULL
                                        AND checked_at < now() - make_interval(days => CASE WHEN airing THEN 7 ELSE 30 END))
                FROM mal_anime;
                SELECT min(next_attempt_at) FROM cr_season_matches WHERE status IN ('pending', 'failed');
                SELECT last_error FROM cr_season_matches WHERE last_error IS NOT NULL ORDER BY next_attempt_at DESC NULLS LAST LIMIT 5;
                """,
                conn
            )

        use! r = cmd.ExecuteReaderAsync()
        let seasons = ResizeArray()

        while r.Read() do
            seasons.Add((r.GetString 0, r.GetString 1), r.GetInt64 2)

        let! _ = r.NextResultAsync()
        r.Read() |> ignore
        let held, missing, due = r.GetInt64 0, r.GetInt64 1, r.GetInt64 2
        let! _ = r.NextResultAsync()
        r.Read() |> ignore
        let next = if r.IsDBNull 0 then None else Some(r.GetDateTime 0)
        let! _ = r.NextResultAsync()
        let errors = ResizeArray()

        while r.Read() do
            errors.Add(r.GetString 0)

        return
            { Seasons = List.ofSeq seasons
              AnimeHeld = held
              AnimeMissing = missing
              RefreshesDue = due
              NextAttempt = next
              RecentErrors = List.ofSeq errors }
    }

/// `mal-status`
let run (db: DbConfig) (_: string list) : Task<int> =
    task {
        let! s = status db
        printfn "SEASONS"

        for (state, method'), count in s.Seasons do
            printfn "  %-10s %-7s %6d" state method' count

        printfn "ANIME     held %d, missing on MyAnimeList %d, refreshes due %d" s.AnimeHeld s.AnimeMissing s.RefreshesDue

        printfn
            "NEXT TRY  %s"
            (s.NextAttempt |> Option.map _.ToString("yyyy-MM-dd HH:mm 'UTC'") |> Option.defaultValue "-")

        for e in s.RecentErrors do
            printfn "ERROR     %s" e

        return 0
    }
