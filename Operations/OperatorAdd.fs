module FrunchyBackend.Operations.OperatorAdd

open System.Threading.Tasks
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// `operator-add <username>`: the same action as the root's page, from the terminal, so it works
/// with no root configured and no one able to sign in (the way back in).
let run (db: DbConfig) (root: string option) (args: string list) : Task<int> =
    task {
        match args with
        | [ raw ] ->
            match Username.tryCreate raw with
            | None ->
                eprintfn "'%s' is not a valid username." raw
                return 1
            | Some username ->
                match! OperatorActions.add db root username OperatorActions.Server with
                | Ok() ->
                    printfn "%s is now an operator." username
                    return 0
                | Error OperatorActions.UnknownUser ->
                    eprintfn "Unknown user '%s'." username
                    return 2
                | Error OperatorActions.AlreadyOperator ->
                    eprintfn "%s is already an operator; nothing changed." username
                    return 3
                | Error _ ->
                    eprintfn "%s is the root operator (set in configuration), so it can't be added; nothing changed." username
                    return 4
        | _ ->
            eprintfn "Usage: operator-add <username>"
            return 1
    }
