module FrunchyBackend.Operations.MalRetryUnmatched

open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// `mal-retry-unmatched`: queue every automatic no-match for a new lookup (feature 032). Manual and ambiguous
/// rows are left alone. Prints how many seasons were queued.
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [] ->
            let! queued = MalActions.retryUnmatched db
            printfn "queued %d unmatched season(s) for a new lookup" queued
            return 0
        | _ ->
            eprintfn "usage: mal-retry-unmatched"
            return 1
    }
