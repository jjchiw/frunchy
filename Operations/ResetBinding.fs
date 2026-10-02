module FrunchyBackend.Operations.ResetBinding

open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// Forget the user's Crunchyroll history owner; the next accepted submission binds again. History is kept.
let resetBinding (db: DbConfig) (rawUsername: string) : Task<bool> =
    task {
        match Username.tryCreate rawUsername with
        | None -> return false
        | Some username ->
            use connection = getConnection db
            do! connection.OpenAsync()

            use cmd =
                new NpgsqlCommand(
                    "UPDATE users SET cr_account_id = NULL, cr_profile_id = NULL WHERE username = @username",
                    connection
                )

            cmd.Parameters.AddWithValue("username", username) |> ignore
            let! updated = cmd.ExecuteNonQueryAsync()
            return updated = 1
    }

/// `user-reset-binding <username>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username ] ->
            match! resetBinding db username with
            | true ->
                printfn "Binding of '%s' reset; the next accepted submission binds it again." (username.ToLowerInvariant())
                return 0
            | false ->
                eprintfn "Unknown user '%s'." username
                return 2
        | _ ->
            eprintfn "Usage: user-reset-binding <username>"
            return 1
    }
