/// The operator actions shared by the operator CLI (`operator-add`, `operator-remove`,
/// `operator-list`) and the root's page, so both behave identically (feature 008, research R3, R4).
/// Each change and its security events share one transaction.
module FrunchyBackend.Infrastructure.OperatorActions

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// Who makes the change
type By =
    /// The root, signed in on the website (their user id): their own account records it too
    | Web of rootUserId: Guid
    /// The terminal: no root account is involved
    | Server

type ActionError =
    | UnknownUser
    /// The root is configuration only: never added, never removed
    | IsRoot
    | AlreadyOperator
    | NotOperator

/// One row of the `operators` table
type OperatorRow =
    { Username: string
      DisplayName: string
      /// The root who granted it; None when added from the terminal or when that account was deleted
      AddedBy: string option
      AddedAt: DateTime }

let private isRootName (root: string option) (username: string) =
    match root with
    | Some r -> String.Equals(r, username, StringComparison.OrdinalIgnoreCase)
    | None -> false

/// The affected user's account records the event as the operator's doing; on the web the root's
/// own account records it as theirs. `security_events` has no detail column, so neither row names
/// the other person (the application log does).
let private recordEvents connection tx (by: By) (affected: Guid) (event: SecurityEventType) : Task<unit> =
    task {
        do! SecurityEvents.record connection tx affected event None Actor.Operator

        match by with
        | Web rootUserId -> do! SecurityEvents.record connection tx rootUserId event None Actor.User
        | Server -> ()
    }

let private change
    (db: DbConfig)
    (root: string option)
    (rawUsername: string)
    (apply: NpgsqlConnection -> NpgsqlTransaction -> Guid -> Task<Result<SecurityEventType, ActionError>>)
    (by: By)
    : Task<Result<unit, ActionError>> =
    task {
        let username = if isNull rawUsername then "" else rawUsername.Trim().ToLowerInvariant()

        if isRootName root username then
            return Error IsRoot
        else
            use connection = getConnection db
            do! connection.OpenAsync()
            use tx = connection.BeginTransaction()

            use lookup = new NpgsqlCommand("SELECT id FROM users WHERE username = @username", connection, tx)
            lookup.Parameters.AddWithValue("username", username) |> ignore
            let! found = lookup.ExecuteScalarAsync()

            match found with
            | :? Guid as userId ->
                match! apply connection tx userId with
                | Ok event ->
                    do! recordEvents connection tx by userId event
                    do! tx.CommitAsync()
                    return Ok()
                | Error e ->
                    do! tx.RollbackAsync()
                    return Error e
            | _ ->
                do! tx.RollbackAsync()
                return Error UnknownUser
    }

/// Make an existing user an operator. The primary key settles a race between two adds.
let add (db: DbConfig) (root: string option) (username: string) (by: By) : Task<Result<unit, ActionError>> =
    change
        db
        root
        username
        (fun connection tx userId ->
            task {
                use cmd =
                    new NpgsqlCommand(
                        "INSERT INTO operators (user_id, added_by) VALUES (@user_id, @added_by) ON CONFLICT (user_id) DO NOTHING",
                        connection,
                        tx
                    )

                cmd.Parameters.AddWithValue("user_id", userId) |> ignore

                cmd.Parameters.AddWithValue(
                    "added_by",
                    NpgsqlTypes.NpgsqlDbType.Uuid,
                    (match by with
                     | Web rootUserId -> box rootUserId
                     | Server -> box DBNull.Value)
                )
                |> ignore

                let! inserted = cmd.ExecuteNonQueryAsync()
                return if inserted = 1 then Ok SecurityEventType.OperatorAdded else Error AlreadyOperator
            })
        by

/// Take a user out of the `operators` table; their access closes on their next request
let remove (db: DbConfig) (root: string option) (username: string) (by: By) : Task<Result<unit, ActionError>> =
    change
        db
        root
        username
        (fun connection tx userId ->
            task {
                use cmd = new NpgsqlCommand("DELETE FROM operators WHERE user_id = @user_id", connection, tx)
                cmd.Parameters.AddWithValue("user_id", userId) |> ignore
                let! deleted = cmd.ExecuteNonQueryAsync()
                return if deleted = 1 then Ok SecurityEventType.OperatorRemoved else Error NotOperator
            })
        by

/// The operators in the table, oldest first (the root is configuration, not a row)
let list (db: DbConfig) : Task<OperatorRow list> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "SELECT u.username, u.display_name, g.username, o.added_at FROM operators o JOIN users u ON u.id = o.user_id LEFT JOIN users g ON g.id = o.added_by ORDER BY o.added_at, u.username",
                connection
            )

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<OperatorRow>()

        while r.Read() do
            rows.Add
                { Username = r.GetString 0
                  DisplayName = r.GetString 1
                  AddedBy = if r.IsDBNull 2 then None else Some(r.GetString 2)
                  AddedAt = r.GetDateTime 3 }

        return List.ofSeq rows
    }
