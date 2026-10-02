module FrunchyBackend.Operations.MalShow

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

type SeasonRow =
    { SeasonId: string
      SeasonTitle: string
      Number: int option
      Status: string
      Method: string option
      MalId: int option
      MalTitle: string option
      MalType: string option
      Year: int option
      Score: decimal option
      Genres: string list }

/// Every season of a series with its match; None for an unknown series
let show (db: DbConfig) (seriesId: string) : Task<(string * SeasonRow list) option> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT coalesce(sr.title, sr.id), se.id, coalesce(se.title, ''), se.season_number,
                       coalesce(m.status, 'pending'), m.method, a.mal_id, coalesce(a.title_english, a.title), a.type,
                       coalesce(a.year, extract(year FROM a.aired_from)::int), a.score,
                       coalesce((SELECT string_agg(t.name, ' / ' ORDER BY at.position) FROM mal_anime_tags at
                                 JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                                 WHERE at.mal_id = a.mal_id AND at.kind = 'genre'), '')
                FROM cr_series sr
                JOIN cr_seasons se ON se.series_id = sr.id
                LEFT JOIN cr_season_matches m ON m.season_id = se.id
                LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                WHERE sr.id = @id
                ORDER BY se.season_number NULLS LAST, se.id
                """,
                conn
            )

        cmd.Parameters.AddWithValue("id", seriesId) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()
        let mutable title = None

        while r.Read() do
            title <- Some(r.GetString 0)

            rows.Add
                { SeasonId = r.GetString 1
                  SeasonTitle = r.GetString 2
                  Number = if r.IsDBNull 3 then None else Some(r.GetInt32 3)
                  Status = r.GetString 4
                  Method = if r.IsDBNull 5 then None else Some(r.GetString 5)
                  MalId = if r.IsDBNull 6 then None else Some(r.GetInt32 6)
                  MalTitle = if r.IsDBNull 7 then None else Some(r.GetString 7)
                  MalType = if r.IsDBNull 8 then None else Some(r.GetString 8)
                  Year = if r.IsDBNull 9 then None else Some(r.GetInt32 9)
                  Score = if r.IsDBNull 10 then None else Some(r.GetDecimal 10)
                  Genres = r.GetString 11 |> fun g -> if g = "" then [] else List.ofArray (g.Split " / ") }

        return title |> Option.map (fun t -> t, List.ofSeq rows)
    }

/// `mal-show <crSeriesId>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ seriesId ] ->
            match! show db seriesId with
            | None ->
                eprintfn "unknown series: %s" seriesId
                return 2
            | Some(title, rows) ->
                printfn "%s (%s)" title seriesId

                for row in rows do
                    let number = row.Number |> Option.map string |> Option.defaultValue "-"
                    printfn "  %-18s #%-3s %-40s %-9s %-7s" row.SeasonId number row.SeasonTitle row.Status (defaultArg row.Method "-")

                    match row.MalId with
                    | Some id ->
                        let score = row.Score |> Option.map (fun s -> s.ToString "0.00") |> Option.defaultValue "-"
                        let year = row.Year |> Option.map string |> Option.defaultValue "-"

                        printfn
                            "      → %d %s (%s, %s) ★ %s  %s"
                            id
                            (defaultArg row.MalTitle "")
                            (defaultArg row.MalType "-")
                            year
                            score
                            (String.Join(" / ", row.Genres))
                    | None -> ()

                return 0
        | _ ->
            eprintfn "usage: mal-show <crSeriesId>"
            return 1
    }
