module FrunchyBackend.Operations.DeleteUser

open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

type Deleted =
    { Imports: int64
      WatchEvents: int64 }

/// Delete the user and everything they own, raw submissions included (FR-019, constitution I exception)
let deleteUser (db: DbConfig) (federated: bool) (rawUsername: string) : Task<Deleted option> =
    task {
        match! AccountActions.tryUserIdByUsername db rawUsername with
        | Some userId ->
            match! AccountActions.deleteUser db federated userId with
            | Some(imports, watchEvents) -> return Some { Imports = imports; WatchEvents = watchEvents }
            | None -> return None
        | None -> return None
    }

/// `user-delete <username> --yes`
let run (db: DbConfig) (federated: bool) (args: string list) : Task<int> =
    task {
        match args with
        | [ username; "--yes" ] ->
            match! deleteUser db federated username with
            | Some deleted ->
                printfn
                    "Deleted user '%s' with %d import(s) and %d watch event(s). This can't be undone."
                    (username.ToLowerInvariant())
                    deleted.Imports
                    deleted.WatchEvents

                return 0
            | None ->
                eprintfn "Unknown user '%s'." username
                return 2
        | [ username ] ->
            eprintfn "Refusing to delete '%s' without --yes: this removes all of their data, raw submissions included." username
            return 1
        | _ ->
            eprintfn "Usage: user-delete <username> --yes"
            return 1
    }
