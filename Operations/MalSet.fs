module FrunchyBackend.Operations.MalSet

open System
open System.Net.Http
open System.Threading.Tasks
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MalMatching

type SetError =
    | UnknownSeason
    /// MyAnimeList says that id doesn't exist (exit 3)
    | UnknownAnime
    /// The service failed temporarily; nothing changed (exit 4)
    | Unavailable of string

/// Match a season by hand. The anime is fetched (once, paced) only when it isn't held yet.
let setMatch (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (mal: MalClient.MalSettings) (seasonId: string) (malId: int) : Task<Result<string, SetError>> =
    task {
        let! season = MalCatalog.seriesOfSeason db seasonId

        match season, mal.Missing with
        | None, _ -> return Error UnknownSeason
        | Some _, Some why -> return Error(Unavailable why)
        | Some _, None ->
            match! (MalClient.Live(factory, db, clock, mal.Provider)).Lookup.Full malId with
            | Gone -> return Error UnknownAnime
            | Fetch.Unavailable reason -> return Error(Unavailable reason)
            | Got anime ->
                let! _ = MalCatalog.writeManual db seasonId (Some malId) (clock.GetUtcNow())
                return Ok(anime.TitleEnglish |> Option.defaultValue anime.Title)
    }

/// `mal-set <crSeasonId> <malId>`
let run (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (mal: MalClient.MalSettings) (args: string list) : Task<int> =
    task {
        match args with
        | [ seasonId; raw ] ->
            match Int32.TryParse raw with
            | true, malId when malId > 0 ->
                match! setMatch db factory clock mal seasonId malId with
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
            | _ ->
                eprintfn "usage: mal-set <crSeasonId> <malId>"
                return 1
        | _ ->
            eprintfn "usage: mal-set <crSeasonId> <malId>"
            return 1
    }
