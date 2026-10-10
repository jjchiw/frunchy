module FrunchyBackend.Operations.MalRetry

open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// Queue a season, or every season of a series, at the front (`MalActions.retry` without a state
/// check). None for an unknown id; else (seasons, anime) queued.
let retry (db: DbConfig) (id: string) : Task<(int * int) option> =
    task {
        match! MalActions.retry db MalActions.Any id with
        | Ok counts -> return Some counts
        | Error _ -> return None
    }

/// `mal-retry <crSeasonId | crSeriesId>`
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ id ] ->
            match! retry db id with
            | None ->
                eprintfn "unknown season or series: %s" id
                return 2
            | Some(seasons, anime) ->
                printfn "queued %d season(s) for a new lookup and %d anime for refresh" seasons anime
                return 0
        | _ ->
            eprintfn "usage: mal-retry <crSeasonId | crSeriesId>"
            return 1
    }
