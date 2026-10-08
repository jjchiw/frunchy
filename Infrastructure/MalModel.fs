/// Shared shape of MyAnimeList data, whichever provider it came from (feature 003).
/// Each provider has its own parser into these records: JikanModel (Jikan v4), MalApiModel
/// (MyAnimeList's official API v2). The matcher, the catalog and the dashboard only see these.
module FrunchyBackend.Infrastructure.MalModel

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

type MalTag = { Id: int; Name: string }

/// A linked anime as listed in `relations` (only `type = anime` entries are kept)
type MalRelatedEntry = { Id: int; Name: string }

type MalRelation = { Relation: string; Entries: MalRelatedEntry list }

type MalAnime =
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
      Genres: MalTag list
      ExplicitGenres: MalTag list
      Themes: MalTag list
      Demographics: MalTag list
      Studios: MalTag list
      /// Only present in `/full` responses
      Relations: MalRelation list }

    /// Every title the anime is known by: default, English, synonyms (Japanese script excluded)
    member a.AllTitles =
        [ yield a.Title
          yield! Option.toList a.TitleEnglish
          yield! a.Synonyms ]
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> List.distinct

/// A provider's error body, read for `last_error` (Jikan: { status, type, message }; MyAnimeList: { error, message })
type ApiError =
    { Status: int option
      Type: string option
      Message: string option }

// ---------------------------------------------------------------------------------------------
// JSON helpers shared by the provider parsers (tolerant: missing or null values become None)
// ---------------------------------------------------------------------------------------------

let prop (e: JsonElement) (name: string) =
    match e.ValueKind with
    | JsonValueKind.Object ->
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
        | _ -> None
    | _ -> None

let str e name =
    prop e name
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.String ->
            match v.GetString() with
            | null -> None
            | s when String.IsNullOrWhiteSpace s -> None
            | s -> Some s
        | _ -> None)

let int' e name =
    prop e name
    |> Option.bind (fun v ->
        match v.ValueKind, v.TryGetInt32() with
        | JsonValueKind.Number, (true, i) -> Some i
        | _ -> None)

let bool' e name =
    prop e name
    |> Option.map (fun v -> v.ValueKind = JsonValueKind.True)
    |> Option.defaultValue false

let array e name =
    match prop e name with
    | Some v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x ]
    | _ -> []

let tags e name : MalTag list =
    [ for t in array e name do
          match int' t "mal_id", str t "name" with
          | Some id, Some n -> yield { Id = id; Name = n }
          | _ -> () ]

let date e name =
    str e name
    |> Option.bind (fun s ->
        match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, d -> Some(DateOnly.FromDateTime d.UtcDateTime)
        | _ -> None)

let withDocument (body: string) (f: JsonElement -> Result<'T, string>) =
    try
        use doc = JsonDocument.Parse body
        f doc.RootElement
    with :? JsonException as ex ->
        Error $"unreadable JSON: {ex.Message}"

/// SHA-256 (lowercase hex) of a JSON element's raw text, used to detect changed details (research R7)
let hashOf (raw: string) =
    SHA256.HashData(Encoding.UTF8.GetBytes raw) |> Convert.ToHexStringLower

