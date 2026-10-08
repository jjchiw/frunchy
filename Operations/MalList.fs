module FrunchyBackend.Operations.MalList

open System
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Infrastructure.Database

type Row =
    { SeasonId: string
      SeriesTitle: string
      SeasonTitle: string
      Status: string
      Reason: string option
      Attempts: int
      NextAttempt: DateTime option
      /// (malId, title, type, year) for ambiguous and no-match rows
      Candidates: (int * string * string * string) list }

let private parseCandidates (json: string) =
    use doc = JsonDocument.Parse json

    [ for c in doc.RootElement.EnumerateArray() ->
          let text (name: string) =
              match c.TryGetProperty name with
              | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
              | true, v when v.ValueKind = JsonValueKind.Number -> string (v.GetInt32())
              | _ -> "-"

          c.GetProperty("malId").GetInt32(), text "title", text "type", text "year" ]

/// Seasons that aren't matched (or only one status), in queue order (FR-002a)
let list (db: DbConfig) (status: string option) (limit: int) : Task<Row list> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT m.season_id,
                       coalesce(sr.title,
                                (SELECT min(e.series_title) FROM cr_episodes e WHERE e.series_id = se.series_id),
                                se.series_id),
                       coalesce(se.title, ''), m.status, m.reason, m.attempts, m.next_attempt_at, m.candidates::text
                FROM cr_season_matches m
                JOIN cr_seasons se ON se.id = m.season_id
                LEFT JOIN cr_series sr ON sr.id = se.series_id
                WHERE (@status::text IS NULL AND m.status <> 'matched') OR m.status = @status::text
                ORDER BY (m.requested_at IS NULL), m.requested_at,
                         (SELECT max(w.watched_at) FROM cr_episodes e JOIN watch_events w ON w.cr_episode_id = e.id
                          WHERE e.season_id = m.season_id) DESC NULLS LAST,
                         m.season_id
                LIMIT @limit
                """,
                conn
            )

        cmd.Parameters.Add(NpgsqlParameter("status", NpgsqlDbType.Text, Value = (match status with Some s -> box s | None -> box DBNull.Value)))
        |> ignore

        cmd.Parameters.AddWithValue("limit", limit) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while r.Read() do
            rows.Add
                { SeasonId = r.GetString 0
                  SeriesTitle = r.GetString 1
                  SeasonTitle = r.GetString 2
                  Status = r.GetString 3
                  Reason = if r.IsDBNull 4 then None else Some(r.GetString 4)
                  Attempts = r.GetInt32 5
                  NextAttempt = if r.IsDBNull 6 then None else Some(r.GetDateTime 6)
                  Candidates = if r.IsDBNull 7 then [] else parseCandidates (r.GetString 7) }

        return List.ofSeq rows
    }

let private usage () =
    eprintfn "usage: mal-list [--status pending|matched|ambiguous|no_match|failed] [--limit N]"
    1

/// `mal-list [--status S] [--limit N]`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        let rec parse status limit args =
            match args with
            | [] -> Some(status, limit)
            | "--status" :: s :: rest when List.contains s [ "pending"; "matched"; "ambiguous"; "no_match"; "failed" ] ->
                parse (Some s) limit rest
            | "--limit" :: n :: rest ->
                match Int32.TryParse n with
                | true, v when v > 0 -> parse status v rest
                | _ -> None
            | _ -> None

        match parse None 50 args with
        | None -> return usage ()
        | Some(status, limit) ->
            let! rows = list db status limit

            for row in rows do
                let next = row.NextAttempt |> Option.map _.ToString("yyyy-MM-dd HH:mm") |> Option.defaultValue "-"

                printfn
                    "%-18s %-10s %-24s attempts %d, next %s  %s / %s"
                    row.SeasonId
                    row.Status
                    (defaultArg row.Reason "-")
                    row.Attempts
                    next
                    row.SeriesTitle
                    row.SeasonTitle

                for malId, title, kind, year in row.Candidates do
                    printfn "    candidate %7d  %-6s %-5s %s" malId kind year title

            return 0
    }
