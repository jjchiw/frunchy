module FrunchyBackend.Operations.ListUsers

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.Profiles

type UserRow =
    { Username: string
      DisplayName: string
      Bound: bool
      Imports: int64
      LastSyncAt: DateTime option
      /// Linked sign-in providers (feature 002), e.g. ["google"; "umbral"]
      Providers: string list
      /// Effective profile visibility (feature 017)
      Visibility: Visibility
      /// True when the user never chose, so the instance default decides
      FromDefault: bool }

let listUsers (db: DbConfig) (settings: ProfileSettings) : Task<UserRow list> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT u.username, u.display_name, u.cr_account_id IS NOT NULL, count(i.id), max(i.received_at),
                       coalesce((SELECT string_agg(provider, ',' ORDER BY provider) FROM user_identities WHERE user_id = u.id), ''),
                       (SELECT visibility FROM profile_settings WHERE user_id = u.id)
                FROM users u LEFT JOIN cr_imports i ON i.user_id = u.id
                GROUP BY u.id ORDER BY u.username
                """,
                connection
            )

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<UserRow>()

        while r.Read() do
            let choice = if r.IsDBNull 6 then None else Visibility.tryParse (r.GetString 6)

            rows.Add
                { Username = r.GetString 0
                  DisplayName = r.GetString 1
                  Bound = r.GetBoolean 2
                  Imports = r.GetInt64 3
                  LastSyncAt = if r.IsDBNull 4 then None else Some(r.GetDateTime 4)
                  Providers = r.GetString 5 |> fun p -> if p = "" then [] else List.ofArray (p.Split ',')
                  Visibility = effective settings.DefaultVisibility choice
                  FromDefault = choice.IsNone }

        return List.ofSeq rows
    }

/// `user-list`
let run (db: DbConfig) (settings: ProfileSettings) (_: string list) : Task<int> =
    task {
        let! rows = listUsers db settings
        printfn "%-30s %-30s %-6s %8s  %-17s %-14s %s" "USERNAME" "DISPLAY NAME" "BOUND" "IMPORTS" "LAST SYNC (UTC)" "SIGN-IN" "VISIBILITY"

        for row in rows do
            let last = row.LastSyncAt |> Option.map _.ToString("yyyy-MM-dd HH:mm") |> Option.defaultValue "-"
            let providers = if row.Providers.IsEmpty then "-" else String.Join(",", row.Providers)
            // A star marks a visibility that comes from the instance default rather than the user's choice
            let visibility = Visibility.toDb row.Visibility + (if row.FromDefault then "*" else "")
            printfn "%-30s %-30s %-6s %8d  %-17s %-14s %s" row.Username row.DisplayName (if row.Bound then "yes" else "no") row.Imports last providers visibility

        return 0
    }
