module FrunchyBackend.Operations.InviteCreate

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type CreatedInvite =
    { Id: Guid
      Code: string
      ExpiresAt: DateTime }

/// A one-time sign-up invite (the code is returned once; only its hash is stored)
let createInvite (db: DbConfig) (days: int) : Task<CreatedInvite> =
    task {
        let code = InviteCode.generate ()
        let id = Guid.NewGuid()
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "INSERT INTO invites (id, code_hash, expires_at) VALUES (@id, @hash, now() + make_interval(days => @days)) RETURNING expires_at",
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("hash", InviteCode.hash code) |> ignore
        cmd.Parameters.AddWithValue("days", days) |> ignore
        let! expires = cmd.ExecuteScalarAsync()
        return { Id = id; Code = code; ExpiresAt = unbox<DateTime> expires }
    }

/// `invite-create [--days N]`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        let days =
            match args with
            | [] -> Some 7
            | [ "--days"; n ] ->
                match Int32.TryParse n with
                | true, d when d >= 1 && d <= 365 -> Some d
                | _ -> None
            | _ -> None

        match days with
        | Some d ->
            let! invite = createInvite db d
            printfn "Invite code (shown only once, give it to the person you invite): %s" invite.Code
            printfn "Id: %O · expires %s UTC" invite.Id (invite.ExpiresAt.ToString "yyyy-MM-dd HH:mm")
            return 0
        | None ->
            eprintfn "Usage: invite-create [--days N]   (N between 1 and 365, default 7)"
            return 1
    }
