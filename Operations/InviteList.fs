module FrunchyBackend.Operations.InviteList

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

type InviteRow =
    { Id: Guid
      CreatedAt: DateTime
      ExpiresAt: DateTime
      /// active | used | revoked | expired
      State: string
      UsedBy: string option }

let listInvites (db: DbConfig) : Task<InviteRow list> =
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
                ORDER BY i.created_at DESC
                """,
                connection
            )

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

/// `invite-list`
let run (db: DbConfig) (_: string list) : Task<int> =
    task {
        let! rows = listInvites db
        printfn "%-36s %-16s %-16s %-8s %s" "ID" "CREATED (UTC)" "EXPIRES (UTC)" "STATE" "USED BY"

        for row in rows do
            printfn
                "%-36O %-16s %-16s %-8s %s"
                row.Id
                (row.CreatedAt.ToString "yyyy-MM-dd HH:mm")
                (row.ExpiresAt.ToString "yyyy-MM-dd HH:mm")
                row.State
                (row.UsedBy |> Option.defaultValue "-")

        return 0
    }
