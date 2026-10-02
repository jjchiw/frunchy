module FrunchyBackend.Operations.DeleteUser

open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type Deleted =
    { Imports: int64
      WatchEvents: int64 }

/// Delete the user and everything they own, raw submissions included (FR-019, constitution I exception).
/// The ON DELETE CASCADE foreign keys remove imports, pages, issues and watch events.
let deleteUser (db: DbConfig) (rawUsername: string) : Task<Deleted option> =
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
                // Lock the imports first: waits for a worker mid-import, so nothing reappears afterwards
                use lockImports =
                    new NpgsqlCommand("SELECT id FROM cr_imports WHERE user_id = @id FOR UPDATE", connection, tx)

                lockImports.Parameters.AddWithValue("id", id) |> ignore
                let! _ = lockImports.ExecuteNonQueryAsync()

                use counts =
                    new NpgsqlCommand(
                        "SELECT (SELECT count(*) FROM cr_imports WHERE user_id = @id), (SELECT count(*) FROM watch_events WHERE user_id = @id)",
                        connection,
                        tx
                    )

                counts.Parameters.AddWithValue("id", id) |> ignore

                let! deleted =
                    task {
                        use! r = counts.ExecuteReaderAsync()
                        r.Read() |> ignore
                        return { Imports = r.GetInt64 0; WatchEvents = r.GetInt64 1 }
                    }

                use delete = new NpgsqlCommand("DELETE FROM users WHERE id = @id", connection, tx)
                delete.Parameters.AddWithValue("id", id) |> ignore
                let! _ = delete.ExecuteNonQueryAsync()
                do! tx.CommitAsync()
                return Some deleted
            | _ -> return None
    }

/// `user-delete <username> --yes`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username; "--yes" ] ->
            match! deleteUser db username with
            | Some deleted ->
                printfn
                    "Deleted user '%s' with %d import(s) and %d watch event(s). This can't be undone."
                    (username.ToLowerInvariant())
                    deleted.Imports
                    deleted.WatchEvents

                return 0
            | None ->
                eprintfn "Unknown user '%s'." username
                return 2
        | [ username ] ->
            eprintfn "Refusing to delete '%s' without --yes: this removes all of their data, raw submissions included." username
            return 1
        | _ ->
            eprintfn "Usage: user-delete <username> --yes"
            return 1
    }
