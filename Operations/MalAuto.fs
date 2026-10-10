module FrunchyBackend.Operations.MalAuto

open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// `mal-auto <crSeasonId>`: send a manual decision back to the matcher (its dubs follow)
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ seasonId ] ->
            match! MalActions.backToAutomatic db MalActions.Manual seasonId with
            | Ok() ->
                printfn "%s sent back to automatic matching (queued at the front)" seasonId
                return 0
            | Error MalActions.UnknownSeason ->
                eprintfn "unknown season: %s" seasonId
                return 2
            | Error _ ->
                eprintfn "%s is not a manual decision; nothing changed" seasonId
                return 3
        | _ ->
            eprintfn "usage: mal-auto <crSeasonId>"
            return 1
    }
