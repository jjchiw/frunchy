module FrunchyBackend.Operations.InviteRevoke

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

type RevokeError =
    | UnknownInvite
    | NotActive

/// Revoke an invite that is still active (not used, not revoked, not expired)
let revokeInvite (db: DbConfig) (id: Guid) : Task<Result<unit, RevokeError>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "UPDATE invites SET revoked_at = now() WHERE id = @id AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()",
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        let! updated = cmd.ExecuteNonQueryAsync()

        if updated = 1 then
            return Ok()
        else
            use exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM invites WHERE id = @id)", connection)
            exists.Parameters.AddWithValue("id", id) |> ignore
            let! found = exists.ExecuteScalarAsync()
            return Error(if unbox<bool> found then NotActive else UnknownInvite)
    }

/// `invite-revoke <id>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ raw ] ->
            match Guid.TryParse raw with
            | true, id ->
                match! revokeInvite db id with
                | Ok() ->
                    printfn "Invite %O revoked." id
                    return 0
                | Error UnknownInvite ->
                    eprintfn "Unknown invite '%s'." raw
                    return 2
                | Error NotActive ->
                    eprintfn "Invite '%s' isn't active (already used, revoked or expired)." raw
                    return 3
            | _ ->
                eprintfn "Unknown invite '%s'." raw
                return 2
        | _ ->
            eprintfn "Usage: invite-revoke <id>"
            return 1
    }
