/// Jikan v4 responses → MalModel records (feature 003, research R1). Kept as an alternative
/// provider (`Frunchy:Mal:Provider = jikan`); the default is MyAnimeList's official API.
module FrunchyBackend.Infrastructure.JikanModel

open System
open System.Text.Json
open FrunchyBackend.Infrastructure.MalModel

let private parseAnime (a: JsonElement) : Result<MalAnime, string> =
    match int' a "mal_id" with
    | None -> Error "anime without mal_id"
    | Some malId ->
        let titled kind =
            [ for t in array a "titles" do
                  if str t "type" = Some kind then
                      match str t "title" with
                      | Some s -> yield s
                      | None -> () ]

        let title =
            titled "Default" |> List.tryHead |> Option.orElse (str a "title") |> Option.defaultValue $"#{malId}"

        let synonyms =
            titled "Synonym"
            @ [ for s in array a "title_synonyms" do
                    if s.ValueKind = JsonValueKind.String then
                        match s.GetString() with
                        | null -> ()
                        | v -> yield v ]
            |> List.distinct

        let score =
            prop a "score"
            |> Option.bind (fun v ->
                match v.ValueKind, v.TryGetDecimal() with
                | JsonValueKind.Number, (true, d) when d > 0m -> Some d
                | _ -> None)

        let image =
            let images = prop a "images"
            let from kind = images |> Option.bind (fun i -> prop i kind) |> Option.bind (fun k -> str k "image_url")
            from "webp" |> Option.orElse (from "jpg")

        let relations =
            [ for r in array a "relations" do
                  match str r "relation" with
                  | Some relation ->
                      let entries =
                          [ for e in array r "entry" do
                                match str e "type", int' e "mal_id", str e "name" with
                                | Some "anime", Some id, Some n -> yield { Id = id; Name = n }
                                | _ -> () ]

                      if not entries.IsEmpty then
                          yield { Relation = relation; Entries = entries }
                  | None -> () ]

        Ok
            { MalId = malId
              Url = str a "url" |> Option.defaultValue $"https://myanimelist.net/anime/{malId}"
              Title = title
              TitleEnglish = titled "English" |> List.tryHead |> Option.orElse (str a "title_english")
              TitleJapanese = titled "Japanese" |> List.tryHead |> Option.orElse (str a "title_japanese")
              Synonyms = synonyms
              Type = str a "type"
              Episodes = int' a "episodes"
              Status = str a "status"
              Airing = bool' a "airing"
              AiredFrom = prop a "aired" |> Option.bind (fun aired -> date aired "from")
              Year = int' a "year"
              Season = str a "season"
              Score = score
              ImageUrl = image
              Rating = str a "rating"
              Genres = tags a "genres"
              ExplicitGenres = tags a "explicit_genres"
              Themes = tags a "themes"
              Demographics = tags a "demographics"
              Studios = tags a "studios"
              Relations = relations }

/// `GET anime?q=…`: the candidates, in Jikan's order
let parseSearch (body: string) : Result<MalAnime list, string> =
    withDocument body (fun root ->
        match prop root "data" with
        | Some data when data.ValueKind = JsonValueKind.Array ->
            let parsed = [ for a in data.EnumerateArray() -> parseAnime a ]

            match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some e -> Error e
            | None -> Ok [ for p in parsed do match p with Ok a -> yield a | Error _ -> () ]
        | _ -> Error "search response without a data array")

/// `GET anime/{id}/full`: the anime and the hash of its `data` element
let parseFull (body: string) : Result<MalAnime * string, string> =
    withDocument body (fun root ->
        match prop root "data" with
        | Some data when data.ValueKind = JsonValueKind.Object ->
            parseAnime data |> Result.map (fun a -> a, hashOf (data.GetRawText()))
        | _ -> Error "anime response without a data object")

/// Jikan's JSON error body, when the response has one
let parseError (body: string) : ApiError option =
    if String.IsNullOrWhiteSpace body then
        None
    else
        try
            use doc = JsonDocument.Parse body
            let root = doc.RootElement

            if root.ValueKind = JsonValueKind.Object then
                Some
                    { Status = int' root "status"
                      Type = str root "type"
                      Message = str root "message" }
            else
                None
        with :? JsonException ->
            None
