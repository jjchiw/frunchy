/// Pure parsing of Crunchyroll watch-history pages (no I/O). Paging fields are never read.
/// Classification rules: specs/001-crunchyroll-history-import/data-model.md ("Entry → outcome mapping")
module FrunchyBackend.Infrastructure.CrunchyrollPage

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

/// An entry with full episode details (outcome: processed)
type EpisodeFacts =
    { EpisodeId: string
      /// The entry's parent_id: the Crunchyroll series this watch counts for (franchise movies included)
      SeriesId: string
      /// Series title, only when the metadata describes the parent series itself (series_id = parent_id)
      ParentSeriesTitle: string option
      SeriesSlug: string option
      SeasonId: string option
      SeasonNumber: int option
      SeasonDisplayNumber: string option
      SeasonTitle: string option
      AudioLocale: string option
      /// episode_metadata.series_title, what the recent list shows (a movie shows its own title)
      SeriesTitle: string option
      Title: string option
      EpisodeNumber: int option
      EpisodeLabel: string option
      DurationMs: int
      ThumbnailUrl: string option
      /// episode_metadata.episode_air_date: when the episode first aired (None when absent or not a date)
      AiredAt: DateTimeOffset option
      WatchedAt: DateTimeOffset
      PlayheadS: int
      FullyWatched: bool }

/// An entry without panel: the watch facts are known, the episode details aren't (outcome: unresolved)
type UnresolvedFacts =
    { EpisodeId: string
      SeriesId: string
      WatchedAt: DateTimeOffset
      PlayheadS: int
      FullyWatched: bool }

type ParsedEntry =
    | Episode of EpisodeFacts
    | Unresolved of UnresolvedFacts
    | Unsupported of episodeId: string option * reason: string
    | Failed of episodeId: string option * reason: string

let unresolvedReason = "Entry has no episode details (panel missing)"

let private tryProp (element: JsonElement) (name: string) =
    if element.ValueKind <> JsonValueKind.Object then
        None
    else
        match element.TryGetProperty name with
        | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
        | _ -> None

let private tryString element name =
    tryProp element name
    |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private tryInt element name =
    tryProp element name
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, i -> Some i
            | _ -> None
        | _ -> None)

let private tryBool element name =
    tryProp element name
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> None)

let private tryDate element name =
    tryString element name
    |> Option.bind (fun s ->
        match
            DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, d -> Some d
        | _ -> None)

/// The thumbnail variant whose width is closest to 320 px (images.thumbnail is an array of size arrays)
let private thumbnail (panel: JsonElement) =
    tryProp panel "images"
    |> Option.bind (fun images -> tryProp images "thumbnail")
    |> Option.bind (fun sets ->
        if sets.ValueKind <> JsonValueKind.Array then
            None
        else
            [ for set in sets.EnumerateArray() do
                  if set.ValueKind = JsonValueKind.Array then
                      for variant in set.EnumerateArray() do
                          match tryString variant "source", tryInt variant "width" with
                          | Some source, Some width -> yield source, width
                          | _ -> () ]
            |> List.sortBy (fun (_, width) -> abs (width - 320))
            |> List.tryHead
            |> Option.map fst)

let private parseEntry (entry: JsonElement) : ParsedEntry =
    if entry.ValueKind <> JsonValueKind.Object then
        Failed(None, "Entry is not a JSON object")
    else
        let id = tryString entry "id"

        match id, tryDate entry "date_played", tryString entry "parent_id" with
        | None, _, _ -> Failed(None, "Missing or invalid 'id'")
        | Some _, None, _ -> Failed(id, "Missing or invalid 'date_played'")
        | Some _, _, None -> Failed(id, "Missing or invalid 'parent_id'")
        | Some episodeId, Some watchedAt, Some parentId ->
            let playhead = tryInt entry "playhead" |> Option.defaultValue 0 |> max 0
            let fullyWatched = tryBool entry "fully_watched" |> Option.defaultValue false

            match tryProp entry "panel" with
            | None ->
                Unresolved
                    { EpisodeId = episodeId
                      SeriesId = parentId
                      WatchedAt = watchedAt
                      PlayheadS = playhead
                      FullyWatched = fullyWatched }
            | Some panel ->
                match tryString panel "type" with
                | Some "episode" ->
                    let metadata = tryProp panel "episode_metadata"

                    let meta name =
                        metadata |> Option.bind (fun m -> tryString m name)

                    let metaInt name =
                        metadata |> Option.bind (fun m -> tryInt m name)

                    match metaInt "duration_ms" with
                    | Some duration when duration > 0 ->
                        let metadataSeriesId = meta "series_id"

                        Episode
                            { EpisodeId = episodeId
                              SeriesId = parentId
                              ParentSeriesTitle =
                                if metadataSeriesId = Some parentId then meta "series_title" else None
                              SeriesSlug =
                                if metadataSeriesId = Some parentId then meta "series_slug_title" else None
                              SeasonId = meta "season_id"
                              SeasonNumber = metaInt "season_number"
                              SeasonDisplayNumber = meta "season_display_number"
                              SeasonTitle = meta "season_title"
                              AudioLocale = meta "audio_locale"
                              SeriesTitle = meta "series_title"
                              Title = tryString panel "title"
                              EpisodeNumber = metaInt "episode_number"
                              EpisodeLabel = meta "episode"
                              DurationMs = duration
                              ThumbnailUrl = thumbnail panel
                              AiredAt = metadata |> Option.bind (fun m -> tryDate m "episode_air_date")
                              WatchedAt = watchedAt
                              PlayheadS = playhead
                              FullyWatched = fullyWatched }
                    | _ -> Failed(id, "Missing or invalid 'panel.episode_metadata.duration_ms'")
                | Some other -> Unsupported(id, $"Unsupported content type '{other}'")
                | None -> Unsupported(id, "Unsupported content type (panel.type missing)")

/// Classify every entry of one page body (Crunchyroll's response verbatim). Error when there is no data array.
let parsePage (body: JsonElement) : Result<ParsedEntry list, string> =
    match tryProp body "data" with
    | Some data when data.ValueKind = JsonValueKind.Array -> Ok [ for entry in data.EnumerateArray() -> parseEntry entry ]
    | _ -> Error "Page body has no 'data' array"

let private historyPath =
    Regex("^/content/v2/([^/]+)/watch-history/?$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// The Crunchyroll account id in a watch-history request URL (absolute or relative; the query is opaque)
let tryHistoryOwner (url: string) : Result<string, string> =
    if String.IsNullOrWhiteSpace url then
        Error "Page url is empty"
    else
        let path =
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> uri.AbsolutePath
            | _ ->
                let withoutQuery = url.Split([| '?'; '#' |], 2).[0]
                if withoutQuery.StartsWith "/" then withoutQuery else "/" + withoutQuery

        let m = historyPath.Match path

        if m.Success then
            Ok m.Groups[1].Value
        else
            Error $"Page url is not a Crunchyroll watch-history address (/content/v2/{{id}}/watch-history): {url}"
