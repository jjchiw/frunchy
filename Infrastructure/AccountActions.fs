/// Account actions shared by the operator CLI and the website (feature 002, research R9),
/// so both behave identically and both record security events.
module FrunchyBackend.Infrastructure.AccountActions

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

let tryUserIdByUsername (db: DbConfig) (rawUsername: string) : Task<Guid option> =
    task {
        let username = if isNull rawUsername then "" else rawUsername.Trim().ToLowerInvariant()
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT id FROM users WHERE username = @username", connection)
        cmd.Parameters.AddWithValue("username", username) |> ignore
        let! id = cmd.ExecuteScalarAsync()
        return match id with :? Guid as g -> Some g | _ -> None
    }

/// Issue a new sync key (returned once); the previous one stops working immediately
let regenerateKey (db: DbConfig) (userId: Guid) (actor: Actor) : Task<string> =
    task {
        let key = SyncKey.generate ()
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand("UPDATE users SET sync_key_hash = @hash, sync_key_created_at = now() WHERE id = @id", connection, tx)

        cmd.Parameters.AddWithValue("hash", SyncKey.hash key) |> ignore
        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        do! SecurityEvents.record connection tx userId SecurityEventType.SyncKeyGenerated None actor
        do! tx.CommitAsync()
        return key
    }

/// Forget the Crunchyroll history owner; the next accepted submission binds again. History is kept.
let resetBinding (db: DbConfig) (userId: Guid) (actor: Actor) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand("UPDATE users SET cr_account_id = NULL, cr_profile_id = NULL WHERE id = @id", connection, tx)

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        do! SecurityEvents.record connection tx userId SecurityEventType.BindingReset None actor
        do! tx.CommitAsync()
    }

/// Delete the user and everything they own (raw submissions included; constitution I exception).
/// Locks the user's imports first so a running worker finishes and nothing reappears.
/// With federation on (`federated`), a `delete_actor` item for the user joins the transaction (Megáfono then deletes
/// the actor on the fediverse); the web and the CLI both pass it.
/// Returns (imports, watch events) removed, or None when the user doesn't exist.
let deleteUser (db: DbConfig) (federated: bool) (userId: Guid) : Task<(int64 * int64) option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use lockUser = new NpgsqlCommand("SELECT id FROM users WHERE id = @id FOR UPDATE", connection, tx)
        lockUser.Parameters.AddWithValue("id", userId) |> ignore
        let! found = lockUser.ExecuteScalarAsync()

        match found with
        | :? Guid ->
            use lockImports = new NpgsqlCommand("SELECT id FROM cr_imports WHERE user_id = @id FOR UPDATE", connection, tx)
            lockImports.Parameters.AddWithValue("id", userId) |> ignore
            let! _ = lockImports.ExecuteNonQueryAsync()

            use counts =
                new NpgsqlCommand(
                    "SELECT (SELECT count(*) FROM cr_imports WHERE user_id = @id), (SELECT count(*) FROM watch_events WHERE user_id = @id)",
                    connection,
                    tx
                )

            counts.Parameters.AddWithValue("id", userId) |> ignore

            let! removed =
                task {
                    use! r = counts.ExecuteReaderAsync()
                    r.Read() |> ignore
                    return r.GetInt64 0, r.GetInt64 1
                }

            use delete = new NpgsqlCommand("DELETE FROM users WHERE id = @id", connection, tx)
            delete.Parameters.AddWithValue("id", userId) |> ignore
            let! _ = delete.ExecuteNonQueryAsync()

            if federated then
                do! Fediverse.enqueue connection tx "delete_actor" (string userId)

            do! tx.CommitAsync()
            return Some removed
        | _ -> return None
    }
