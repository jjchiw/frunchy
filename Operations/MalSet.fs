module FrunchyBackend.Operations.MalSet

open System
open System.Net.Http
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MalActions

/// `mal-set <crSeasonId> <malId>`
let run (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (mal: MalClient.MalSettings) (args: string list) : Task<int> =
    task {
        match args with
        | [ seasonId; raw ] ->
            match Int32.TryParse raw with
            | true, malId when malId > 0 ->
                match! MalActions.setMatch db factory clock mal Any seasonId malId with
                | Ok title ->
                    printfn "%s → %d %s (manual)" seasonId malId title
                    return 0
                | Error UnknownSeason ->
                    eprintfn "unknown season: %s" seasonId
                    return 2
                | Error UnknownAnime ->
                    eprintfn "MyAnimeList has no anime %d; %s is unchanged" malId seasonId
                    return 3
                | Error(Unavailable reason) ->
                    eprintfn "MyAnimeList is unavailable (%s); nothing changed, try again later" reason
                    return 4
                | Error NotOpen ->
                    // not reachable without a state check, but the match must be complete
                    eprintfn "%s is not in the expected state; nothing changed" seasonId
                    return 3
            | _ ->
                eprintfn "usage: mal-set <crSeasonId> <malId>"
                return 1
        | _ ->
            eprintfn "usage: mal-set <crSeasonId> <malId>"
            return 1
    }
