module FrunchyBackend.Tests.Fixtures

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// Account id present in both real captures (2022 meta.next_page, 2026 page URLs)
[<Literal>]
let RealAccountId = "3b6dd336-f005-5b47-9b98-b26a174a2010"

/// Default Crunchyroll profile for test envelopes (the history URL doesn't identify profiles; research R5)
[<Literal>]
let MainProfile = "profile-main"

/// Adds a top-level profileId to an envelope's JSON text (page bodies stay verbatim)
let withProfile (profileId: string) (envelopeJson: string) =
    let i = envelopeJson.IndexOf '{'
    envelopeJson.Substring(0, i + 1) + $"\"profileId\":\"{profileId}\"," + envelopeJson.Substring(i + 1)

let historyUrl (accountId: string) =
    $"https://www.crunchyroll.com/content/v2/{accountId}/watch-history?locale=en-US&page_size=100"

let private samplePath (name: string) =
    Path.Combine(AppContext.BaseDirectory, "JsonSamples", name)

/// The 2022 page body exactly as Crunchyroll returned it
let load2022PageBody () = File.ReadAllText(samplePath "PostHistory.json")

/// The 2022 page wrapped in the submission envelope
let load2022Envelope () =
    $"{{\"profileId\":\"{MainProfile}\",\"pages\":[{{\"url\":\"{historyUrl RealAccountId}\",\"body\":{load2022PageBody ()}}}]}}"

/// The 2026 sample file as committed: the extension's export envelope (4 pages, 400 entries), no profileId
let load2026SampleRaw () = File.ReadAllText(samplePath "crunchyroll-history-2026-sample.json")

/// The 2026 sample as a submission (with the default profileId)
let load2026Sample () = load2026SampleRaw () |> withProfile MainProfile

/// Envelope from (url, raw page body JSON) pairs, without profileId
let envelopeWithoutProfile (pages: (string * string) list) =
    let parts = pages |> List.map (fun (url, body) -> $"{{\"url\":\"{url}\",\"body\":{body}}}")
    $"{{\"pages\":[{String.Join(',', parts)}]}}"

/// Envelope for a given Crunchyroll profile
let envelopeForProfile (profileId: string) (pages: (string * string) list) =
    envelopeWithoutProfile pages |> withProfile profileId

/// Envelope from (url, raw page body JSON) pairs, for the default profile
let envelopeOf (pages: (string * string) list) = envelopeForProfile MainProfile pages

/// A valid Crunchyroll episode entry (fully watched, 23m40s) for synthetic histories
let syntheticEntry (episodeId: string) (seriesId: string) (datePlayed: DateTime) (fullyWatched: bool) (playhead: int) =
    let date = datePlayed.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    $"""{{"id":"{episodeId}","date_played":"{date}","parent_id":"{seriesId}","parent_type":"series","playhead":{playhead},"fully_watched":{(if fullyWatched then "true" else "false")},"panel":{{"id":"{episodeId}","type":"episode","title":"Episode {episodeId}","images":{{"thumbnail":[[{{"width":320,"height":180,"source":"https://img.example/{episodeId}/320.jpg","type":"thumbnail"}},{{"width":640,"height":360,"source":"https://img.example/{episodeId}/640.jpg","type":"thumbnail"}}]]}},"episode_metadata":{{"series_id":"{seriesId}","series_title":"Series {seriesId}","season_id":"{seriesId}S1","season_number":1,"season_title":"Series {seriesId}","episode":"1","episode_number":1,"duration_ms":1420000}}}}}}"""

/// An episode entry with the details feature 027 reads: its season id, episode number and `episode_air_date`
/// (None leaves the field out). Fully watched or not, 23m40s, the series titled "Series {seriesId}".
let datedEntry
    (episodeId: string)
    (seriesId: string)
    (seasonId: string)
    (episodeNumber: int)
    (airedAt: DateTime option)
    (datePlayed: DateTime)
    (fullyWatched: bool)
    =
    let played = datePlayed.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    let airDate =
        match airedAt with
        | Some d -> ",\"episode_air_date\":\"" + d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") + "\""
        | None -> ""

    $"""{{"id":"{episodeId}","date_played":"{played}","parent_id":"{seriesId}","parent_type":"series","playhead":{(if fullyWatched then 1400 else 300)},"fully_watched":{(if fullyWatched then "true" else "false")},"panel":{{"id":"{episodeId}","type":"episode","title":"Episode {episodeId}","episode_metadata":{{"series_id":"{seriesId}","series_title":"Series {seriesId}","season_id":"{seasonId}","season_number":1,"season_title":"Series {seriesId}","episode":"{episodeNumber}","episode_number":{episodeNumber},"duration_ms":1420000{airDate}}}}}}}"""

/// An entry without panel (unresolved)
let unresolvedEntry (episodeId: string) (seriesId: string) (datePlayed: DateTime) =
    let date = datePlayed.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $"""{{"id":"{episodeId}","date_played":"{date}","parent_id":"{seriesId}","parent_type":"series","playhead":10,"fully_watched":true}}"""

let pageOf (entries: string list) =
    $"""{{"total":{entries.Length},"data":[{String.Join(',', entries)}],"meta":{{"next_page":"","prev_page":""}}}}"""

/// pageCount pages of entriesPerPage unique, fully watched episodes (20 episodes per series)
let syntheticPages (accountId: string) (pageCount: int) (entriesPerPage: int) =
    let start = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    [ for p in 0 .. pageCount - 1 ->
          let entries =
              [ for e in 0 .. entriesPerPage - 1 ->
                    let n = p * entriesPerPage + e
                    syntheticEntry $"SYN{n:D6}" $"SER{n / 20:D4}" (start.AddMinutes(float n)) true 1400 ]

          historyUrl accountId, pageOf entries ]

let postImport (client: HttpClient) (key: string option) (json: string) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(HttpMethod.Post, "/api/imports")
    request.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    key |> Option.iter (fun k -> request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", k))
    client.SendAsync request

let getWithKey (client: HttpClient) (key: string option) (path: string) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(HttpMethod.Get, path)
    key |> Option.iter (fun k -> request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", k))
    client.SendAsync request

let getWithLanguage (client: HttpClient) (language: string) (path: string) : Task<HttpResponseMessage> =
    let request = new HttpRequestMessage(HttpMethod.Get, path)
    request.Headers.AcceptLanguage.ParseAdd language
    client.SendAsync request

/// Response HTML with entities decoded (ViewEngine writes non-ASCII as &#NNN;, which browsers render normally)
let readHtml (response: HttpResponseMessage) : Task<string> =
    task {
        let! html = response.Content.ReadAsStringAsync()
        return WebUtility.HtmlDecode html
    }
