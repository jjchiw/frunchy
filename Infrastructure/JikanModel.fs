/// Jikan v4 response shapes and their parsing (feature 003, research R1).
/// Parsing is tolerant: unknown fields are ignored, missing values become None or empty lists.
module FrunchyBackend.Infrastructure.JikanModel

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

type JikanTag = { Id: int; Name: string }

/// A linked anime as listed in `relations` (only `type = anime` entries are kept)
type JikanRelatedEntry = { Id: int; Name: string }

type JikanRelation = { Relation: string; Entries: JikanRelatedEntry list }

type JikanAnime =
    { MalId: int
      Url: string
      /// titles[type=Default], e.g. "Sousou no Frieren"
      Title: string
      TitleEnglish: string option
      TitleJapanese: string option
      Synonyms: string list
      Type: string option
      Episodes: int option
      Status: string option
      Airing: bool
      AiredFrom: DateOnly option
      Year: int option
      Season: string option
      /// None when MyAnimeList has no score (Jikan sends 0 or null)
      Score: decimal option
      ImageUrl: string option
      Rating: string option
      Genres: JikanTag list
      ExplicitGenres: JikanTag list
      Themes: JikanTag list
      Demographics: JikanTag list
      Studios: JikanTag list
      /// Only present in `/full` responses
      Relations: JikanRelation list }

    /// Every title the anime is known by: default, English, synonyms (Japanese script excluded)
    member a.AllTitles =
        [ yield a.Title
          yield! Option.toList a.TitleEnglish
          yield! a.Synonyms ]
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.distinct

/// Jikan's error body: { status, type, message, error, report_url }
type JikanError =
    { Status: int option
      Type: string option
      Message: string option }

let private prop (e: JsonElement) (name: string) =
    match e.ValueKind with
    | JsonValueKind.Object ->
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
        | _ -> None
    | _ -> None

let private str e name =
    prop e name
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.String ->
            match v.GetString() with
            | null -> None
            | s when String.IsNullOrWhiteSpace s -> None
            | s -> Some s
        | _ -> None)

let private int' e name =
    prop e name
    |> Option.bind (fun v ->
        match v.ValueKind, v.TryGetInt32() with
        | JsonValueKind.Number, (true, i) -> Some i
        | _ -> None)

let private bool' e name =
    prop e name
    |> Option.map (fun v -> v.ValueKind = JsonValueKind.True)
    |> Option.defaultValue false

let private array e name =
    match prop e name with
    | Some v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x ]
    | _ -> []

let private tags e name : JikanTag list =
    [ for t in array e name do
          match int' t "mal_id", str t "name" with
          | Some id, Some n -> yield { Id = id; Name = n }
          | _ -> () ]

let private date e name =
    str e name
    |> Option.bind (fun s ->
        match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, d -> Some(DateOnly.FromDateTime d.UtcDateTime)
        | _ -> None)

let private parseAnime (a: JsonElement) : Result<JikanAnime, string> =
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

let private withDocument (body: string) (f: JsonElement -> Result<'T, string>) =
    try
        use doc = JsonDocument.Parse body
        f doc.RootElement
    with :? JsonException as ex ->
        Error $"unreadable JSON: {ex.Message}"

/// SHA-256 (lowercase hex) of a JSON element's raw text, used to detect changed details (research R7)
let hashOf (raw: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes raw) |> Convert.ToHexStringLower

/// `GET anime?q=…`: the candidates, in Jikan's order
let parseSearch (body: string) : Result<JikanAnime list, string> =
    withDocument body (fun root ->
        match prop root "data" with
        | Some data when data.ValueKind = JsonValueKind.Array ->
            let parsed = [ for a in data.EnumerateArray() -> parseAnime a ]

            match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some e -> Error e
            | None -> Ok [ for p in parsed do match p with Ok a -> yield a | Error _ -> () ]
        | _ -> Error "search response without a data array")

/// `GET anime/{id}/full`: the anime and the hash of its `data` element
let parseFull (body: string) : Result<JikanAnime * string, string> =
    withDocument body (fun root ->
        match prop root "data" with
        | Some data when data.ValueKind = JsonValueKind.Object ->
            parseAnime data |> Result.map (fun a -> a, hashOf (data.GetRawText()))
        | _ -> Error "anime response without a data object")

/// Jikan's JSON error body, when the response has one
let parseError (body: string) : JikanError option =
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
