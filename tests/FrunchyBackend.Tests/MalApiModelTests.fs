module FrunchyBackend.Tests.MalApiModelTests

open System
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.MalModel
open FrunchyBackend.Infrastructure.MalApiModel
open Microsoft.Extensions.Configuration

/// A details body in the shape MyAnimeList's API v2 documents (fields as requested by DetailFields)
let private frierenDetails =
    """
    {"id":52991,"title":"Sousou no Frieren",
     "main_picture":{"medium":"https://cdn.myanimelist.net/images/anime/1015/138006.jpg","large":"https://cdn.myanimelist.net/images/anime/1015/138006l.jpg"},
     "alternative_titles":{"synonyms":["Frieren at the Funeral","Frieren The Slayer"],"en":"Frieren: Beyond Journey's End","ja":"葬送のフリーレン"},
     "start_date":"2023-09-29","mean":9.3,"media_type":"tv","status":"finished_airing",
     "genres":[{"id":2,"name":"Adventure"},{"id":46,"name":"Award Winning"},{"id":8,"name":"Drama"},{"id":10,"name":"Fantasy"},{"id":27,"name":"Shounen"},{"id":62,"name":"Isekai"}],
     "num_episodes":28,"start_season":{"year":2023,"season":"fall"},"rating":"pg_13","nsfw":"white",
     "studios":[{"id":11,"name":"Madhouse"}],
     "related_anime":[
       {"node":{"id":59978,"title":"Sousou no Frieren 2nd Season"},"relation_type":"sequel","relation_type_formatted":"Sequel"},
       {"node":{"id":56885,"title":"Sousou no Frieren: ●● no Mahou"},"relation_type":"side_story","relation_type_formatted":"Side story"}]}
    """

[<Fact>]
let ``details map onto the shared record`` () =
    match parseFull frierenDetails with
    | Error e -> failwith e
    | Ok(a, hash) ->
        Assert.Equal(52991, a.MalId)
        Assert.Equal("https://myanimelist.net/anime/52991", a.Url)
        Assert.Equal("Sousou no Frieren", a.Title)
        Assert.Equal(Some "Frieren: Beyond Journey's End", a.TitleEnglish)
        Assert.Equal(Some "葬送のフリーレン", a.TitleJapanese)
        Assert.Equal<string list>([ "Frieren at the Funeral"; "Frieren The Slayer" ], a.Synonyms)
        Assert.Equal((Some "TV", Some "Finished Airing", false), (a.Type, a.Status, a.Airing))
        Assert.Equal(Some(DateOnly(2023, 9, 29)), a.AiredFrom)
        Assert.Equal((Some 2023, Some "fall"), (a.Year, a.Season))
        Assert.Equal(Some 9.3m, a.Score)
        Assert.Equal(Some 28, a.Episodes)
        Assert.Equal(Some "https://cdn.myanimelist.net/images/anime/1015/138006l.jpg", a.ImageUrl)
        // MyAnimeList's single genre list is split by id
        Assert.Equal<string list>([ "Adventure"; "Award Winning"; "Drama"; "Fantasy" ], a.Genres |> List.map _.Name)
        Assert.Equal<string list>([ "Shounen" ], a.Demographics |> List.map _.Name)
        Assert.Equal<string list>([ "Isekai" ], a.Themes |> List.map _.Name)
        Assert.Equal<string list>([ "Madhouse" ], a.Studios |> List.map _.Name)
        // Relations use the names the matcher knows
        Assert.Contains({ Relation = "Sequel"; Entries = [ { Id = 59978; Name = "Sousou no Frieren 2nd Season" } ] }, a.Relations)
        Assert.Contains(a.Relations, fun r -> r.Relation = "Side Story")
        Assert.Equal(64, hash.Length)
        Assert.False(MalMatching.isAdult a)

[<Fact>]
let ``search results are the nodes, in order; empty values become None`` () =
    let body =
        """{"data":[{"node":{"id":2,"title":"B","alternative_titles":{"synonyms":[],"en":"","ja":""},"media_type":"movie","start_date":"2021-12","mean":0}},
                    {"node":{"id":1,"title":"A","media_type":"unknown","start_date":"2021"}}],"paging":{}}"""

    match parseSearch body with
    | Error e -> failwith e
    | Ok [ b; a ] ->
        Assert.Equal((2, Some "Movie", Some(DateOnly(2021, 12, 1)), None, None), (b.MalId, b.Type, b.AiredFrom, b.Score, b.TitleEnglish))
        Assert.Equal((1, None, Some(DateOnly(2021, 1, 1))), (a.MalId, a.Type, a.AiredFrom))
    | Ok other -> failwith $"unexpected %A{other}"

    Assert.True(Result.isError (parseSearch "<html>"))
    Assert.True(Result.isError (parseFull """{"error":"not_found"}"""))

[<Fact>]
let ``adult entries are recognized by rating`` () =
    match parseFull """{"id":9,"title":"X","rating":"rx","genres":[{"id":12,"name":"Hentai"}]}""" with
    | Ok(a, _) ->
        Assert.Equal(Some "Rx - Hentai", a.Rating)
        Assert.True(MalMatching.isAdult a)
    | Error e -> failwith e

[<Fact>]
let ``MyAnimeList's error body is read`` () =
    Assert.Equal(Some { Status = None; Type = Some "not_found"; Message = None }, parseError """{"error":"not_found","message":""}""")

[<Fact>]
let ``long titles are cut before searching MyAnimeList, short ones are sent as they are`` () =
    let tank =
        "The Strongest Tank's Labyrinth Raids -A Tank with a Rare 9999 Resistance Skill Got Kicked from the Hero's Party-"

    Assert.Equal("The Strongest Tank's Labyrinth Raids", MalClient.MalApi.Query tank)
    Assert.Equal("Frieren: Beyond Journey's End", MalClient.MalApi.Query "Frieren: Beyond Journey's End")
    Assert.Equal(tank, MalClient.Jikan.Query tank)
    // MyAnimeList finds nothing with a "(2020)" or "(TV)" marker in the query
    Assert.Equal("Dragon Quest: The Adventure of Dai", MalClient.MalApi.Query "Dragon Quest: The Adventure of Dai (2020)")
    Assert.Equal("The Fire Hunter: Season 1", MalClient.MalApi.Query "The Fire Hunter (TV): Season 1")
    // "gray" entries (ecchi, some PG-13 shows) are only found with nsfw=true
    Assert.Contains("&nsfw=true&", MalClient.MalApi.SearchPath "x")
    let long = String.replicate 20 "word "
    Assert.True((MalClient.MalApi.Query long).Length <= 64)

[<Fact>]
let ``the official API needs a client id; the provider must be known`` () =
    let read (pairs: (string * string) list) =
        Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(dict pairs).Build() |> MalClient.readSettings

    let none = read []
    Assert.Equal(MalClient.MalApi, none.Provider)
    Assert.Equal("https://api.myanimelist.net/v2/", none.BaseUrl)
    Assert.True(none.Missing.IsSome)
    Assert.Equal(None, (read [ "Frunchy:Mal:ClientId", "abc" ]).Missing)
    Assert.Equal(None, (read [ "Frunchy:Mal:Provider", "jikan" ]).Missing)
    Assert.Throws<InvalidOperationException>(fun () -> read [ "Frunchy:Mal:Provider", "anilist" ] |> ignore) |> ignore
