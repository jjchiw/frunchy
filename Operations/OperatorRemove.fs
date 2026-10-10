module FrunchyBackend.Operations.OperatorRemove

open System.Threading.Tasks
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// `operator-remove <username>`: their access closes on their next request
let run (db: DbConfig) (root: string option) (args: string list) : Task<int> =
    task {
        match args with
        | [ raw ] ->
            match Username.tryCreate raw with
            | None ->
                eprintfn "'%s' is not a valid username." raw
                return 1
            | Some username ->
                match! OperatorActions.remove db root username OperatorActions.Server with
                | Ok() ->
                    printfn "%s is no longer an operator." username
                    return 0
                | Error OperatorActions.UnknownUser ->
                    eprintfn "Unknown user '%s'." username
                    return 2
                | Error OperatorActions.NotOperator ->
                    eprintfn "%s is not an operator; nothing changed." username
                    return 3
                | Error _ ->
                    eprintfn "%s is the root operator (set in configuration), so it can't be removed here; nothing changed." username
                    return 4
        | _ ->
            eprintfn "Usage: operator-remove <username>"
            return 1
    }
