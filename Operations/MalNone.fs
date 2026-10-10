module FrunchyBackend.Operations.MalNone

open System
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

/// `mal-none <crSeasonId>`: the season isn't on MyAnimeList (manual, never re-matched automatically)
let run (db: DbConfig) (args: string list) : Task<int> =
    task {
        match args with
        | [ seasonId ] ->
            match! MalActions.setNone db TimeProvider.System MalActions.Any seasonId with
            | Ok() ->
                printfn "%s marked as not on MyAnimeList (manual)" seasonId
                return 0
            | Error _ ->
                eprintfn "unknown season: %s" seasonId
                return 2
        | _ ->
            eprintfn "usage: mal-none <crSeasonId>"
            return 1
    }
