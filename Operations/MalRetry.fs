module FrunchyBackend.Operations.MalRetry

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

/// Queue a season, or every season of a series, at the front. Automatic rows go back to pending
/// (with their stored searches dropped, so the lookup asks again); manual rows keep their match and
/// their anime is queued for refresh instead. None for an unknown id; else (seasons, anime) queued.
let retry (db: DbConfig) (id: string) : Task<(int * int) option> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        use find =
            new NpgsqlCommand(
                "SELECT se.id FROM cr_seasons se WHERE se.id = @id OR se.series_id = @id",
                conn,
                tx
            )

        find.Parameters.AddWithValue("id", id) |> ignore
        use! r = find.ExecuteReaderAsync()
        let seasons = ResizeArray<string>()

        while r.Read() do
            seasons.Add(r.GetString 0)

        do! r.CloseAsync()

        if seasons.Count = 0 then
            return None
        else
            use cmd =
                new NpgsqlCommand(
                    """
                    INSERT INTO cr_season_matches (season_id, status)
                    SELECT unnest(@ids), 'pending' ON CONFLICT (season_id) DO NOTHING;

                    DELETE FROM mal_searches q
                    USING cr_seasons se JOIN cr_series sr ON sr.id = se.series_id
                    WHERE se.id = ANY(@ids) AND (q.query = sr.title OR q.query LIKE sr.title || ' %');

                    WITH auto AS (
                        UPDATE cr_season_matches SET status = 'pending', mal_id = NULL, method = NULL, shared_from = NULL,
                               attempts = 0, next_attempt_at = NULL, claimed_at = NULL, requested_at = now(), last_error = NULL
                        WHERE season_id = ANY(@ids) AND method IS DISTINCT FROM 'manual'
                        RETURNING season_id
                    ),
                    manual AS (
                        UPDATE mal_anime SET refresh_requested_at = now(), refresh_next_at = NULL
                        WHERE mal_id IN (SELECT mal_id FROM cr_season_matches
                                         WHERE season_id = ANY(@ids) AND method = 'manual' AND mal_id IS NOT NULL)
                        RETURNING mal_id
                    )
                    SELECT (SELECT count(*) FROM auto)::int, (SELECT count(*) FROM manual)::int
                    """,
                    conn,
                    tx
                )

            cmd.Parameters.AddWithValue("ids", seasons.ToArray()) |> ignore
            use! counts = cmd.ExecuteReaderAsync()
            let! _ = counts.ReadAsync()
            let result = counts.GetInt32 0, counts.GetInt32 1
            do! counts.CloseAsync()
            do! tx.CommitAsync()
            return Some result
    }

/// `mal-retry <crSeasonId | crSeriesId>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ id ] ->
            match! retry db id with
            | None ->
                eprintfn "unknown season or series: %s" id
                return 2
            | Some(seasons, anime) ->
                printfn "queued %d season(s) for a new lookup and %d anime for refresh" seasons anime
                return 0
        | _ ->
            eprintfn "usage: mal-retry <crSeasonId | crSeriesId>"
            return 1
    }
