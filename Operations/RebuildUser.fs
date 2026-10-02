module FrunchyBackend.Operations.RebuildUser

open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// Drop the user's derived data and queue all their stored submissions again (research R13).
/// The raw pages are untouched; the shared catalog is kept. Returns the number of imports queued.
let rebuildUser (db: DbConfig) (rawUsername: string) : Task<int option> =
    task {
        match Username.tryCreate rawUsername with
        | None -> return None
        | Some username ->
            use connection = getConnection db
            do! connection.OpenAsync()
            use tx = connection.BeginTransaction()

            use findUser = new NpgsqlCommand("SELECT id FROM users WHERE username = @username FOR UPDATE", connection, tx)
            findUser.Parameters.AddWithValue("username", username) |> ignore
            let! userId = findUser.ExecuteScalarAsync()

            match userId with
            | :? System.Guid as id ->
                // Lock the imports first: waits for a worker mid-import and avoids deadlocking with it
                use lockImports =
                    new NpgsqlCommand("SELECT id FROM cr_imports WHERE user_id = @id FOR UPDATE", connection, tx)

                lockImports.Parameters.AddWithValue("id", id) |> ignore
                let! _ = lockImports.ExecuteNonQueryAsync()

                use reset =
                    new NpgsqlCommand(
                        """
                        DELETE FROM watch_events WHERE user_id = @id;
                        DELETE FROM cr_import_entry_issues WHERE import_id IN (SELECT id FROM cr_imports WHERE user_id = @id);
                        UPDATE cr_imports
                        SET status = 'pending', processed = 0, unresolved = 0, unsupported = 0, failed = 0,
                            error = NULL, processing_started_at = NULL, completed_at = NULL
                        WHERE user_id = @id;
                        """,
                        connection,
                        tx
                    )

                reset.Parameters.AddWithValue("id", id) |> ignore
                let! _ = reset.ExecuteNonQueryAsync()

                use countImports = new NpgsqlCommand("SELECT count(*) FROM cr_imports WHERE user_id = @id", connection, tx)
                countImports.Parameters.AddWithValue("id", id) |> ignore
                let! queued = countImports.ExecuteScalarAsync()
                do! tx.CommitAsync()
                return Some(int (unbox<int64> queued))
            | _ -> return None
    }

/// `user-rebuild <username>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username ] ->
            match! rebuildUser db username with
            | Some queued ->
                printfn "Queued %d import(s) of '%s' for reprocessing; the import worker rebuilds them." queued (username.ToLowerInvariant())
                return 0
            | None ->
                eprintfn "Unknown user '%s'." username
                return 2
        | _ ->
            eprintfn "Usage: user-rebuild <username>"
            return 1
    }
