module FrunchyBackend.Tests.CrunchyrollPageTests

open System
open System.Text.Json
open Xunit
open FrunchyBackend.Infrastructure.CrunchyrollPage
open FrunchyBackend.Tests.Fixtures

let private parse (json: string) =
    use doc = JsonDocument.Parse json

    match parsePage (doc.RootElement.Clone()) with
    | Ok entries -> entries
    | Error e -> failwith e

let private count predicate entries = entries |> List.filter predicate |> List.length

let private isEpisode =
    function
    | Episode _ -> true
    | _ -> false

let private isUnresolved =
    function
    | Unresolved _ -> true
    | _ -> false

let private episodes entries =
    entries
    |> List.choose (function
        | Episode e -> Some e
        | _ -> None)

let private pages2026 () =
    use doc = JsonDocument.Parse(load2026SampleRaw ())

    [ for page in doc.RootElement.GetProperty("pages").EnumerateArray() ->
          page.GetProperty("url").GetString(), page.GetProperty("body").GetRawText() ]

[<Fact>]
let ``2022 page has 100 episodes (96 fully watched, 4 partial) and 1 unresolved`` () =
    let entries = parse (load2022PageBody ())
    Assert.Equal(101, entries.Length)
    Assert.Equal(100, count isEpisode entries)
    Assert.Equal(1, count isUnresolved entries)
    Assert.Equal(96, episodes entries |> List.filter _.FullyWatched |> List.length)
    Assert.Equal(4, episodes entries |> List.filter (fun e -> not e.FullyWatched) |> List.length)

[<Fact>]
let ``2026 sample has 309 episodes and 91 unresolved, nothing unsupported or failed`` () =
    let entries = pages2026 () |> List.collect (snd >> parse)
    Assert.Equal(400, entries.Length)
    Assert.Equal(309, count isEpisode entries)
    Assert.Equal(91, count isUnresolved entries)

[<Fact>]
let ``thumbnail picks the 320px variant`` () =
    let first = parse (load2022PageBody ()) |> episodes |> List.head
    Assert.Contains("320x180", first.ThumbnailUrl.Value)

[<Fact>]
let ``franchise movie counts under its parent series and doesn't name it`` () =
    let movie =
        """{"id":"MOV1","date_played":"2025-01-01T00:00:00Z","parent_id":"GRDV0019R","parent_type":"series","playhead":10,"fully_watched":true,
            "panel":{"id":"MOV1","type":"episode","title":"JUJUTSU KAISEN 0","episode_metadata":{"series_id":"GMTE00194450","series_title":"JUJUTSU KAISEN 0","duration_ms":6000000}}}"""

    match parse (pageOf [ movie ]) with
    | [ Episode e ] ->
        Assert.Equal("GRDV0019R", e.SeriesId)
        Assert.Equal(None, e.ParentSeriesTitle)
        Assert.Equal(Some "JUJUTSU KAISEN 0", e.SeriesTitle)
    | other -> failwith $"unexpected {other}"

[<Fact>]
let ``series title is taken when metadata describes the parent series`` () =
    let e = parse (load2022PageBody ()) |> episodes |> List.head
    Assert.Equal(e.SeriesTitle, e.ParentSeriesTitle)

[<Fact>]
let ``non-episode content is unsupported`` () =
    let entry =
        """{"id":"X1","date_played":"2025-01-01T00:00:00Z","parent_id":"S1","playhead":0,"fully_watched":false,"panel":{"id":"X1","type":"movie_listing"}}"""

    match parse (pageOf [ entry ]) with
    | [ Unsupported(Some "X1", reason) ] -> Assert.Contains("movie_listing", reason)
    | other -> failwith $"unexpected {other}"

[<Fact>]
let ``panel without duration fails with a reason`` () =
    let entry =
        """{"id":"X2","date_played":"2025-01-01T00:00:00Z","parent_id":"S1","playhead":0,"fully_watched":true,"panel":{"id":"X2","type":"episode","episode_metadata":{"series_id":"S1"}}}"""

    match parse (pageOf [ entry ]) with
    | [ Failed(Some "X2", reason) ] -> Assert.Contains("duration_ms", reason)
    | other -> failwith $"unexpected {other}"

[<Fact>]
let ``non-object entries and missing ids fail`` () =
    match parse """{"data":[42, {"date_played":"2025-01-01T00:00:00Z","parent_id":"S1"}]}""" with
    | [ Failed(None, r1); Failed(None, r2) ] ->
        Assert.Contains("not a JSON object", r1)
        Assert.Contains("'id'", r2)
    | other -> failwith $"unexpected {other}"

[<Fact>]
let ``page body without data array is an error`` () =
    use doc = JsonDocument.Parse """{"total":0,"items":[]}"""
    Assert.True(Result.isError (parsePage doc.RootElement))

[<Theory>]
[<InlineData("https://www.crunchyroll.com/content/v2/3b6dd336-f005-5b47-9b98-b26a174a2010/watch-history?locale=en-US&page=eyJjIjoiR01LVVg1WEc4In0&page_size=100")>]
[<InlineData("/content/v2/3b6dd336-f005-5b47-9b98-b26a174a2010/watch-history?page=2&page_size=100")>]
[<InlineData("content/v2/3b6dd336-f005-5b47-9b98-b26a174a2010/watch-history")>]
let ``history owner comes from the url path, query ignored`` (url: string) =
    Assert.Equal(Ok RealAccountId, tryHistoryOwner url)

[<Theory>]
[<InlineData("https://www.crunchyroll.com/content/v2/abc/watchlist")>]
[<InlineData("https://www.crunchyroll.com/history")>]
[<InlineData("")>]
let ``urls that aren't watch-history addresses have no owner`` (url: string) =
    Assert.True(Result.isError (tryHistoryOwner url))

[<Fact>]
let ``every 2026 page url yields the same real account`` () =
    for (url, _) in pages2026 () do
        Assert.Equal(Ok RealAccountId, tryHistoryOwner url)
