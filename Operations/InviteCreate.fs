module FrunchyBackend.Operations.InviteCreate

open System
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

type CreatedInvite = InviteActions.CreatedInvite

/// A one-time sign-up invite (the code is returned once; only its hash is stored)
let createInvite (db: DbConfig) (days: int) : Task<CreatedInvite> = InviteActions.create db None days

/// `invite-create [--days N]`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        let days =
            match args with
            | [] -> Some 7
            | [ "--days"; n ] ->
                match Int32.TryParse n with
                | true, d when d >= InviteActions.minDays && d <= InviteActions.maxDays -> Some d
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
