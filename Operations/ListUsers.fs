module FrunchyBackend.Operations.ListUsers

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

type UserRow =
    { Username: string
      DisplayName: string
      Bound: bool
      Imports: int64
      LastSyncAt: DateTime option }

let listUsers (db: DbConfig) : Task<UserRow list> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT u.username, u.display_name, u.cr_account_id IS NOT NULL, count(i.id), max(i.received_at)
                FROM users u LEFT JOIN cr_imports i ON i.user_id = u.id
                GROUP BY u.id ORDER BY u.username
                """,
                connection
            )

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<UserRow>()

        while r.Read() do
            rows.Add
                { Username = r.GetString 0
                  DisplayName = r.GetString 1
                  Bound = r.GetBoolean 2
                  Imports = r.GetInt64 3
                  LastSyncAt = if r.IsDBNull 4 then None else Some(r.GetDateTime 4) }

        return List.ofSeq rows
    }

/// `user-list`
let run (db: DbConfig) (_: string list) : Task<int> =
    task {
        let! rows = listUsers db
        printfn "%-30s %-30s %-6s %8s  %s" "USERNAME" "DISPLAY NAME" "BOUND" "IMPORTS" "LAST SYNC (UTC)"

        for row in rows do
            let last = row.LastSyncAt |> Option.map _.ToString("yyyy-MM-dd HH:mm") |> Option.defaultValue "-"
            printfn "%-30s %-30s %-6s %8d  %s" row.Username row.DisplayName (if row.Bound then "yes" else "no") row.Imports last

        return 0
    }
