module FrunchyBackend.Operations.ResetBinding

open System.Threading.Tasks
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// Forget the user's Crunchyroll history owner; the next accepted submission binds again. History is kept.
let resetBinding (db: DbConfig) (rawUsername: string) : Task<bool> =
    task {
        match! AccountActions.tryUserIdByUsername db rawUsername with
        | Some userId ->
            do! AccountActions.resetBinding db userId Actor.Operator
            return true
        | None -> return false
    }

/// `user-reset-binding <username>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username ] ->
            match! resetBinding db username with
            | true ->
                printfn "Binding of '%s' reset; the next accepted submission binds it again." (username.ToLowerInvariant())
                return 0
            | false ->
                eprintfn "Unknown user '%s'." username
                return 2
        | _ ->
            eprintfn "Usage: user-reset-binding <username>"
            return 1
    }
