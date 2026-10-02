module FrunchyBackend.Operations.RegenerateKey

open System.Threading.Tasks
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// Issue a new sync key; the previous one stops working immediately. None when the user doesn't exist.
let regenerateKey (db: DbConfig) (rawUsername: string) : Task<string option> =
    task {
        match! AccountActions.tryUserIdByUsername db rawUsername with
        | Some userId ->
            let! key = AccountActions.regenerateKey db userId Actor.Operator
            return Some key
        | None -> return None
    }

/// `user-regenerate-key <username>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ username ] ->
            match! regenerateKey db username with
            | Some key ->
                printfn "New sync key for '%s' (shown only once; the old key no longer works): %s" (username.ToLowerInvariant()) key
                return 0
            | None ->
                eprintfn "Unknown user '%s'." username
                return 2
        | _ ->
            eprintfn "Usage: user-regenerate-key <username>"
            return 1
    }
