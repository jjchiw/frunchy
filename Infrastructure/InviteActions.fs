/// The invite actions shared by the operator CLI (`invite-create`, `invite-list`, `invite-revoke`) and the
/// operators' page `/review/invites` (feature 019), so both behave identically. When a web operator makes a
/// change, their own account records it in the change's transaction; the terminal records nothing.
/// Only a code's hash is ever stored, and nothing here logs a code.
module FrunchyBackend.Infrastructure.InviteActions

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type CreatedInvite =
    { Id: Guid
      Code: string
      ExpiresAt: DateTime }

type InviteRow =
    { Id: Guid
      CreatedAt: DateTime
      ExpiresAt: DateTime
      /// active | used | revoked | expired
      State: string
      UsedBy: string option }

type RevokeError =
    | UnknownInvite
    | NotActive

/// The days an invite may last
let minDays = 1
let maxDays = 365

/// A one-time sign-up invite (the code is returned once; only its hash is stored). `recordFor` is the web
/// operator whose account records `invite_created` in the same transaction.
let create (db: DbConfig) (recordFor: Guid option) (days: int) : Task<CreatedInvite> =
    task {
        let code = InviteCode.generate ()
        let id = Guid.NewGuid()
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand(
                "INSERT INTO invites (id, code_hash, expires_at) VALUES (@id, @hash, now() + make_interval(days => @days)) RETURNING expires_at",
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("hash", InviteCode.hash code) |> ignore
        cmd.Parameters.AddWithValue("days", days) |> ignore
        let! expires = cmd.ExecuteScalarAsync()

        match recordFor with
        | Some userId -> do! SecurityEvents.record connection tx userId SecurityEventType.InviteCreated None Actor.User
        | None -> ()

        do! tx.CommitAsync()
        return { Id = id; Code = code; ExpiresAt = unbox<DateTime> expires }
    }

/// The newest invites first; `limit` None lists them all
let list (db: DbConfig) (limit: int option) : Task<InviteRow list> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT i.id, i.created_at, i.expires_at,
                       CASE WHEN i.used_at IS NOT NULL THEN 'used'
                            WHEN i.revoked_at IS NOT NULL THEN 'revoked'
                            WHEN i.expires_at <= now() THEN 'expired'
                            ELSE 'active' END,
                       u.username
                FROM invites i LEFT JOIN users u ON u.id = i.used_by_user_id
                ORDER BY i.created_at DESC, i.id
                LIMIT @limit
                """,
                connection
            )

        // LIMIT NULL means no limit
        cmd.Parameters.Add(
            NpgsqlParameter(
                "limit",
                NpgsqlTypes.NpgsqlDbType.Integer,
                Value = (limit |> Option.map box |> Option.defaultValue (box DBNull.Value))
            )
        )
        |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<InviteRow>()

        while r.Read() do
            rows.Add
                { Id = r.GetGuid 0
                  CreatedAt = r.GetDateTime 1
                  ExpiresAt = r.GetDateTime 2
                  State = r.GetString 3
                  UsedBy = if r.IsDBNull 4 then None else Some(r.GetString 4) }

        return List.ofSeq rows
    }

/// Revoke an invite that is still active (not used, not revoked, not expired). The single UPDATE settles a race
/// between two operators: one changes the row, the other gets `NotActive`. `recordFor` is the web operator whose
/// account records `invite_revoked`, only when something changed.
let revoke (db: DbConfig) (recordFor: Guid option) (id: Guid) : Task<Result<unit, RevokeError>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand(
                "UPDATE invites SET revoked_at = now() WHERE id = @id AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()",
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        let! updated = cmd.ExecuteNonQueryAsync()

        if updated = 1 then
            match recordFor with
            | Some userId -> do! SecurityEvents.record connection tx userId SecurityEventType.InviteRevoked None Actor.User
            | None -> ()

            do! tx.CommitAsync()
            return Ok()
        else
            use exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM invites WHERE id = @id)", connection, tx)
            exists.Parameters.AddWithValue("id", id) |> ignore
            let! found = exists.ExecuteScalarAsync()
            do! tx.RollbackAsync()
            return Error(if unbox<bool> found then NotActive else UnknownInvite)
    }
