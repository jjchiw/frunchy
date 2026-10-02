/// Jikan-shaped bodies for integration tests (feature 003). The shape follows the recorded real
/// response in JsonSamples/jikan/full-52991.json; these stand in for recordings until record.py has
/// captured the rest (tasks T003). Also: a scripted MyAnimeList for some 2026-sample series.
module FrunchyBackend.Tests.MalFixtures

open System.Text.Json
open System.Threading.Tasks
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.FakeJikan
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

type Spec =
    { Id: int
      Title: string
      English: string option
      Kind: string
      Aired: string
      Airing: bool
      Score: float option
      Genres: string list
      Studios: string list
      /// (relation, related id, related default title)
      Relations: (string * int * string) list }

let spec id title english kind aired =
    { Id = id
      Title = title
      English = english
      Kind = kind
      Aired = aired
      Airing = false
      Score = Some 8.0
      Genres = [ "Action" ]
      Studios = []
      Relations = [] }

let private genreIds =
    dict
        [ "Action", 1
          "Adventure", 2
          "Comedy", 4
          "Mystery", 7
          "Drama", 8
          "Fantasy", 10
          "Romance", 22
          "Sci-Fi", 24
          "Slice of Life", 36
          "Sports", 30
          "Supernatural", 37
          "Award Winning", 46 ]

let private tag (kind: string) (name: string) =
    {| mal_id = (if kind = "genre" then genreIds[name] else abs (name.GetHashCode()) % 10000)
       ``type`` = "anime"
       name = name
       url = "" |}

let private animeObject (s: Spec) (withRelations: bool) =
    let year = int (s.Aired.Substring(0, 4))

    let titles =
        [ yield {| ``type`` = "Default"; title = s.Title |}
          match s.English with
          | Some e -> yield {| ``type`` = "English"; title = e |}
          | None -> () ]

    let relations =
        s.Relations
        |> List.groupBy (fun (r, _, _) -> r)
        |> List.map (fun (r, entries) ->
            {| relation = r
               entry = entries |> List.map (fun (_, id, name) -> {| mal_id = id; ``type`` = "anime"; name = name; url = "" |}) |})

    dict
        [ yield "mal_id", box s.Id
          yield "url", box $"https://myanimelist.net/anime/{s.Id}"
          yield "titles", box titles
          yield "title", box s.Title
          yield "title_english", (match s.English with Some e -> box e | None -> null)
          yield "type", box s.Kind
          yield "episodes", box 12
          yield "status", box (if s.Airing then "Currently Airing" else "Finished Airing")
          yield "airing", box s.Airing
          yield "aired", box {| from = s.Aired + "T00:00:00+00:00" |}
          yield "score", (match s.Score with Some v -> box v | None -> box 0)
          yield "year", box year
          yield "season", box "fall"
          yield "images", box {| jpg = {| image_url = $"https://cdn.myanimelist.net/images/anime/{s.Id}.jpg" |} |}
          yield "genres", box (s.Genres |> List.map (tag "genre"))
          yield "explicit_genres", box []
          yield "themes", box []
          yield "demographics", box []
          yield "studios", box (s.Studios |> List.map (tag "studio"))
          if withRelations then yield "relations", box relations ]

let fullBody (s: Spec) =
    JsonSerializer.Serialize {| data = animeObject s true |}

let searchBody (specs: Spec list) =
    JsonSerializer.Serialize
        {| data = specs |> List.map (fun s -> animeObject s false)
           pagination = {| has_next_page = false |} |}

/// Route a spec's /full and nothing else
let routeFull (fake: FakeJikanHandler) (s: Spec) =
    fake.Route($"full:{s.Id}", [ Body(fullBody s) ])

let routeSearch (fake: FakeJikanHandler) (query: string) (results: Spec list) =
    fake.Route($"search:{query}", [ Body(searchBody results) ])

// ---------------------------------------------------------------------------------------------
// A scripted MyAnimeList for some series of the 2026 sample
// ---------------------------------------------------------------------------------------------

let frierenS2 =
    { spec 59978 "Sousou no Frieren 2nd Season" (Some "Frieren: Beyond Journey's End Season 2") "TV" "2026-01-16" with
        Genres = [ "Adventure"; "Drama"; "Fantasy" ]
        Relations = [ "Prequel", 52991, "Sousou no Frieren" ] }

let jjk1 =
    { spec 40748 "Jujutsu Kaisen" (Some "Jujutsu Kaisen") "TV" "2020-10-03" with
        Score = Some 8.55
        Genres = [ "Action"; "Supernatural" ]
        Relations = [ "Sequel", 51009, "Jujutsu Kaisen 2nd Season"; "Prequel", 48561, "Jujutsu Kaisen 0 Movie" ] }

let jjk2 =
    { spec 51009 "Jujutsu Kaisen 2nd Season" (Some "Jujutsu Kaisen Season 2") "TV" "2023-07-06" with
        Score = Some 8.8
        Genres = [ "Action"; "Supernatural" ]
        Relations = [ "Prequel", 40748, "Jujutsu Kaisen" ] }

let jjk0 =
    { spec 48561 "Jujutsu Kaisen 0 Movie" (Some "Jujutsu Kaisen 0") "Movie" "2021-12-24" with
        Score = Some 8.4
        Genres = [ "Action"; "Supernatural" ]
        Relations = [ "Sequel", 40748, "Jujutsu Kaisen" ] }

let tamer =
    { spec 54744 "Saijaku Tamer wa Gomi Hiroi no Tabi wo Hajimemashita." (Some "The Weakest Tamer Began a Journey to Pick Up Trash") "TV" "2024-01-07" with
        Score = Some 7.2
        Genres = [ "Adventure"; "Fantasy" ] }

let rollOver =
    { spec 55998 "Ikenaikyo" (Some "Roll Over and Die: I Will Fight for an Ordinary Life with My Love and Cursed Sword!") "TV" "2025-01-08" with
        Score = None
        Genres = [ "Fantasy" ] }

let eyeshield =
    { spec 15 "Eyeshield 21" None "TV" "2005-04-06" with
        Score = Some 7.9
        Genres = [ "Sports" ] }

/// Frieren uses the real recorded response; the rest are scripted
let routeSampleCatalog (fake: FakeJikanHandler) =
    fake.Route("full:52991", [ File "full-52991.json" ])
    let frieren1 = { spec 52991 "Sousou no Frieren" (Some "Frieren: Beyond Journey's End") "TV" "2023-09-29" with Score = Some 9.26 }
    routeSearch fake "Frieren: Beyond Journey's End" [ frieren1; frierenS2 ]
    routeFull fake frierenS2
    routeSearch fake "JUJUTSU KAISEN" [ jjk1; jjk2; jjk0 ]

    for s in [ jjk1; jjk2; jjk0; tamer; rollOver; eyeshield ] do
        routeFull fake s

    routeSearch fake "The Weakest Tamer Began a Journey to Pick Up Trash" [ tamer ]
    routeSearch fake "ROLL OVER AND DIE" [ rollOver ]
    routeSearch fake "Eyeshield 21" [ eyeshield ]

// ---------------------------------------------------------------------------------------------
// Running the worker in tests
// ---------------------------------------------------------------------------------------------

let settings: FrunchyBackend.Workers.Enrichment.EnrichmentSettings =
    { PauseAfterFailures = 5
      PauseFor = System.TimeSpan.FromMinutes 5.0
      RefreshAfterDays = 30
      RefreshAiringAfterDays = 7 }

let httpFactory (app: TestApp) =
    Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<System.Net.Http.IHttpClientFactory>(app.Services)

/// processNext until Idle (bounded); returns the results
let enrichAllWith (app: TestApp) (clock: System.TimeProvider) : Task<FrunchyBackend.Workers.Enrichment.ProcessResult list> =
    task {
        let results = ResizeArray()
        let mutable go = true

        while go && results.Count < 500 do
            let! r = FrunchyBackend.Workers.Enrichment.processNext app.Db (httpFactory app) clock settings

            match r with
            | FrunchyBackend.Workers.Enrichment.Idle -> go <- false
            | _ -> results.Add r

        return List.ofSeq results
    }

let enrichAll (app: TestApp) = enrichAllWith app System.TimeProvider.System

/// Season match rows as text: season | status | mal id | method | shared_from
let matches (db: DbConfig) =
    Db.dump
        db
        "SELECT season_id || ' ' || status || ' ' || coalesce(mal_id::text, '-') || ' ' || coalesce(method, '-') || ' ' || coalesce(shared_from, '-') FROM cr_season_matches ORDER BY season_id"

let matchOf (db: DbConfig) (seasonId: string) =
    task {
        let! rows = matches db
        return rows |> List.tryFind (fun r -> r.StartsWith(seasonId + " ")) |> Option.defaultValue "(none)"
    }
