/// MyAnimeList's official API v2 responses → MalModel records (feature 003, the default provider).
/// Search: `GET anime?q=…&fields=…` → { data: [ { node: {…} } ], paging }. Details: `GET anime/{id}?fields=…`
/// → the anime object itself. Values are mapped onto the shapes the matcher already uses
/// (Jikan-style type, status and relation names), so the matcher doesn't care which provider answered.
module FrunchyBackend.Infrastructure.MalApiModel

open System
open System.Text.Json
open FrunchyBackend.Infrastructure.MalModel

/// Fields asked for on search results: enough to judge a candidate without another request
[<Literal>]
let SearchFields = "alternative_titles,media_type,start_date,start_season,mean,rating,nsfw,num_episodes,status"

/// Fields asked for on details
[<Literal>]
let DetailFields =
    "id,title,main_picture,alternative_titles,start_date,mean,media_type,status,genres,num_episodes,start_season,rating,nsfw,studios,related_anime"

/// MyAnimeList puts genres, themes and demographics in one `genres` list; its ids are stable, so a fixed
/// table splits them (ids from MyAnimeList's genre pages).
let private mainGenres =
    set [ 1; 2; 4; 5; 7; 8; 10; 14; 22; 24; 26; 28; 30; 36; 37; 41; 46; 47 ]

let private explicitGenres = set [ 9; 12; 49 ]
let private demographicGenres = set [ 15; 25; 27; 42; 43 ]

let private mediaTypes =
    dict
        [ "tv", "TV"
          "ova", "OVA"
          "movie", "Movie"
          "special", "Special"
          "tv_special", "TV Special"
          "ona", "ONA"
          "music", "Music"
          "cm", "CM"
          "pv", "PV" ]

let private statuses =
    dict
        [ "finished_airing", "Finished Airing"
          "currently_airing", "Currently Airing"
          "not_yet_aired", "Not yet aired" ]

let private ratings =
    dict
        [ "g", "G - All Ages"
          "pg", "PG - Children"
          "pg_13", "PG-13 - Teens 13 or older"
          "r", "R - 17+ (violence & profanity)"
          "r+", "R+ - Mild Nudity"
          "rx", "Rx - Hentai" ]

let private relations =
    dict
        [ "sequel", "Sequel"
          "prequel", "Prequel"
          "side_story", "Side Story"
          "parent_story", "Parent Story"
          "summary", "Summary"
          "full_story", "Full Story"
          "alternative_version", "Alternative Version"
          "alternative_setting", "Alternative Setting"
          "spin_off", "Spin-Off"
          "character", "Character"
          "other", "Other" ]

let private mapped (table: Collections.Generic.IDictionary<string, string>) (value: string option) =
    value
    |> Option.map (fun v ->
        match table.TryGetValue v with
        | true, m -> m
        | _ -> v)

/// "2017-10-23", "2017-10" or "2017" → the first day it can mean
let private partialDate (value: string option) =
    value
    |> Option.bind (fun s ->
        let parts = s.Split '-'

        match parts |> Array.map Int32.TryParse with
        | [| (true, y) |] -> Some(DateOnly(y, 1, 1))
        | [| (true, y); (true, m) |] when m >= 1 && m <= 12 -> Some(DateOnly(y, m, 1))
        | [| (true, y); (true, m); (true, d) |] ->
            try
                Some(DateOnly(y, m, d))
            with :? ArgumentOutOfRangeException ->
                None
        | _ -> None)

/// `[{ id, name }]` (the shared `tags` helper reads Jikan's `mal_id`)
let private idTags (e: JsonElement) (name: string) : MalTag list =
    [ for t in array e name do
          match int' t "id", str t "name" with
          | Some id, Some n -> yield { Id = id; Name = n }
          | _ -> () ]

let private parseAnime (a: JsonElement) : Result<MalAnime, string> =
    match int' a "id" with
    | None -> Error "anime without id"
    | Some id ->
        let alternative = prop a "alternative_titles"
        let alt name = alternative |> Option.bind (fun t -> str t name)

        let synonyms =
            match alternative with
            | Some t ->
                [ for s in array t "synonyms" do
                      if s.ValueKind = JsonValueKind.String then
                          match s.GetString() with
                          | null -> ()
                          | v when String.IsNullOrWhiteSpace v -> ()
                          | v -> yield v ]
            | None -> []

        let score =
            prop a "mean"
            |> Option.bind (fun v ->
                match v.ValueKind, v.TryGetDecimal() with
                | JsonValueKind.Number, (true, d) when d > 0m -> Some(Math.Round(d, 2))
                | _ -> None)

        let picture = prop a "main_picture"
        let image = picture |> Option.bind (fun p -> str p "large" |> Option.orElse (str p "medium"))
        let startSeason = prop a "start_season"
        let allGenres = idTags a "genres"
        let status = str a "status"

        let related =
            array a "related_anime"
            |> List.choose (fun r ->
                match prop r "node", str r "relation_type" with
                | Some node, Some relation ->
                    match int' node "id", str node "title" with
                    | Some rid, Some title -> Some(mapped relations (Some relation) |> Option.get, { Id = rid; Name = title })
                    | _ -> None
                | _ -> None)
            |> List.groupBy fst
            |> List.map (fun (relation, entries) -> { Relation = relation; Entries = entries |> List.map snd })

        Ok
            { MalId = id
              Url = $"https://myanimelist.net/anime/{id}"
              Title = str a "title" |> Option.defaultValue $"#{id}"
              TitleEnglish = alt "en"
              TitleJapanese = alt "ja"
              Synonyms = synonyms |> List.distinct
              Type = mapped mediaTypes (str a "media_type" |> Option.filter ((<>) "unknown"))
              Episodes = int' a "num_episodes" |> Option.filter (fun n -> n > 0)
              Status = mapped statuses status
              Airing = (status = Some "currently_airing")
              AiredFrom = partialDate (str a "start_date")
              Year = startSeason |> Option.bind (fun s -> int' s "year")
              Season = startSeason |> Option.bind (fun s -> str s "season")
              Score = score
              ImageUrl = image
              Rating = mapped ratings (str a "rating")
              Genres = allGenres |> List.filter (fun g -> mainGenres.Contains g.Id)
              ExplicitGenres = allGenres |> List.filter (fun g -> explicitGenres.Contains g.Id)
              Themes =
                allGenres
                |> List.filter (fun g -> not (mainGenres.Contains g.Id || explicitGenres.Contains g.Id || demographicGenres.Contains g.Id))
              Demographics = allGenres |> List.filter (fun g -> demographicGenres.Contains g.Id)
              Studios = idTags a "studios"
              Relations = related }

/// `GET anime?q=…`: the candidates, in MyAnimeList's order
let parseSearch (body: string) : Result<MalAnime list, string> =
    withDocument body (fun root ->
        match prop root "data" with
        | Some data when data.ValueKind = JsonValueKind.Array ->
            let parsed =
                [ for item in data.EnumerateArray() ->
                      match prop item "node" with
                      | Some node -> parseAnime node
                      | None -> Error "search item without node" ]

            match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some e -> Error e
            | None -> Ok [ for p in parsed do match p with Ok a -> yield a | Error _ -> () ]
        | _ -> Error "search response without a data array")

/// `GET anime/{id}`: the anime and the hash of the whole body
let parseFull (body: string) : Result<MalAnime * string, string> =
    withDocument body (fun root ->
        if root.ValueKind = JsonValueKind.Object && (prop root "id").IsSome then
            parseAnime root |> Result.map (fun a -> a, hashOf (root.GetRawText()))
        else
            Error "anime response without an id")

/// MyAnimeList's error body: { error, message }
let parseError (body: string) : ApiError option =
    if String.IsNullOrWhiteSpace body then
        None
    else
        try
            use doc = JsonDocument.Parse body
            let root = doc.RootElement

            if root.ValueKind = JsonValueKind.Object then
                Some
                    { Status = None
                      Type = str root "error"
                      Message = str root "message" }
            else
                None
        with :? JsonException ->
            None
