module FrunchyBackend.Operations.CreateUser

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type CreateUserError =
    | InvalidUsername
    | InvalidDisplayName
    | UsernameTaken

type CreatedUser =
    { UserId: Guid
      Username: string
      SyncKey: string }

/// Create a user and issue their sync key (only the hash is stored; the key is returned once)
let createUser (db: DbConfig) (rawUsername: string) (rawDisplayName: string) : Task<Result<CreatedUser, CreateUserError>> =
    task {
        let displayName = if isNull rawDisplayName then "" else rawDisplayName.Trim()

        match Username.tryCreateNew rawUsername with
        | None -> return Error InvalidUsername
        | Some _ when displayName.Length = 0 || displayName.Length > 60 -> return Error InvalidDisplayName
        | Some username ->
            let key = SyncKey.generate ()
            let userId = Guid.NewGuid()
            use connection = getConnection db
            do! connection.OpenAsync()

            use cmd =
                new NpgsqlCommand(
                    """
                    INSERT INTO users (id, username, display_name, sync_key_hash, sync_key_created_at)
                    VALUES (@id, @username, @display_name, @hash, now())
                    """,
                    connection
                )

            cmd.Parameters.AddWithValue("id", userId) |> ignore
            cmd.Parameters.AddWithValue("username", username) |> ignore
            cmd.Parameters.AddWithValue("display_name", displayName) |> ignore
            cmd.Parameters.AddWithValue("hash", SyncKey.hash key) |> ignore

            try
                let! _ = cmd.ExecuteNonQueryAsync()
                return Ok { UserId = userId; Username = username; SyncKey = key }
            with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
                return Error UsernameTaken
    }

/// `user-create <username> <displayName>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username; displayName ] ->
            match! createUser db username displayName with
            | Ok created ->
                printfn "Created user '%s'." created.Username
                printfn "Sync key (shown only once, paste it in the extension): %s" created.SyncKey
                printfn "Public dashboard: /u/%s" created.Username
                return 0
            | Error InvalidUsername ->
                eprintfn "Invalid username '%s': use 3-30 chars, a-z 0-9 _ -, starting with a letter or digit and ending with a letter, digit or _." username
                return 3
            | Error UsernameTaken ->
                eprintfn "Username '%s' is already taken." (username.ToLowerInvariant())
                return 3
            | Error InvalidDisplayName ->
                eprintfn "Display name must be 1-60 characters."
                return 1
        | _ ->
            eprintfn "Usage: user-create <username> <displayName>"
            return 1
    }
