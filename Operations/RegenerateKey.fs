module FrunchyBackend.Operations.RegenerateKey

open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// Issue a new sync key; the previous one stops working immediately. None when the user doesn't exist.
let regenerateKey (db: DbConfig) (rawUsername: string) : Task<string option> =
    task {
        match Username.tryCreate rawUsername with
        | None -> return None
        | Some username ->
            let key = SyncKey.generate ()
            use connection = getConnection db
            do! connection.OpenAsync()

            use cmd =
                new NpgsqlCommand(
                    "UPDATE users SET sync_key_hash = @hash, sync_key_created_at = now() WHERE username = @username",
                    connection
                )

            cmd.Parameters.AddWithValue("hash", SyncKey.hash key) |> ignore
            cmd.Parameters.AddWithValue("username", username) |> ignore
            let! updated = cmd.ExecuteNonQueryAsync()
            return if updated = 1 then Some key else None
    }

/// `user-regenerate-key <username>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username ] ->
            match! regenerateKey db username with
            | Some key ->
                printfn "New sync key for '%s' (shown only once; the old key no longer works): %s" (username.ToLowerInvariant()) key
                return 0
            | None ->
                eprintfn "Unknown user '%s'." username
                return 2
        | _ ->
            eprintfn "Usage: user-regenerate-key <username>"
            return 1
    }
