module FrunchyBackend.Operations.InviteList

open System
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

type InviteRow = InviteActions.InviteRow

let listInvites (db: DbConfig) : Task<InviteRow list> = InviteActions.list db None

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
