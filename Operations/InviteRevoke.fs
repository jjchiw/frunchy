module FrunchyBackend.Operations.InviteRevoke

open System
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

type RevokeError = InviteActions.RevokeError

/// Revoke an invite that is still active (not used, not revoked, not expired)
let revokeInvite (db: DbConfig) (id: Guid) : Task<Result<unit, RevokeError>> = InviteActions.revoke db None id

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
                | Error InviteActions.UnknownInvite ->
                    eprintfn "Unknown invite '%s'." raw
                    return 2
                | Error InviteActions.NotActive ->
                    eprintfn "Invite '%s' isn't active (already used, revoked or expired)." raw
                    return 3
            | _ ->
                eprintfn "Unknown invite '%s'." raw
                return 2
        | _ ->
            eprintfn "Usage: invite-revoke <id>"
            return 1
    }
