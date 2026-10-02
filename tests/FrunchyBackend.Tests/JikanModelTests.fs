module FrunchyBackend.Tests.JikanModelTests

open System
open Xunit
open FrunchyBackend.Infrastructure.JikanModel
open FrunchyBackend.Tests.Infrastructure.FakeJikan

[<Fact>]
let ``the recorded Frieren response parses into the stored fields`` () =
    match parseFull (readFixture "full-52991.json") with
    | Error e -> failwith e
    | Ok(a, hash) ->
        Assert.Equal(52991, a.MalId)
        Assert.Equal("Sousou no Frieren", a.Title)
        Assert.Equal(Some "Frieren: Beyond Journey's End", a.TitleEnglish)
        Assert.Equal(Some "葬送のフリーレン", a.TitleJapanese)
        Assert.Contains("Frieren at the Funeral", a.Synonyms)
        Assert.Equal(Some "TV", a.Type)
        Assert.Equal(Some(DateOnly(2023, 9, 29)), a.AiredFrom)
        Assert.Equal((Some 2023, Some "fall"), (a.Year, a.Season))
        Assert.Equal(Some 9.26m, a.Score)
        Assert.Equal<string list>([ "Adventure"; "Award Winning"; "Drama"; "Fantasy" ], a.Genres |> List.map _.Name)
        Assert.Equal<string list>([ "Shounen" ], a.Demographics |> List.map _.Name)
        Assert.Equal<string list>([ "Madhouse" ], a.Studios |> List.map _.Name)
        Assert.True(a.ImageUrl.IsSome)
        // Only anime relations are kept (the manga adaptation is not)
        Assert.Contains({ Relation = "Sequel"; Entries = [ { Id = 59978; Name = "Sousou no Frieren 2nd Season" } ] }, a.Relations)
        Assert.DoesNotContain(a.Relations, fun r -> r.Relation = "Adaptation")
        Assert.Equal(64, hash.Length)
        Assert.Contains("Frieren: Beyond Journey's End", a.AllTitles)
        Assert.DoesNotContain("葬送のフリーレン", a.AllTitles)

[<Fact>]
let ``a score of 0 means no score; unknown fields are ignored; missing arrays are empty`` () =
    let body =
        """{"data":{"mal_id":1,"title":"X","score":0,"something_new":{"a":1},"titles":[{"type":"Default","title":"X"}]}}"""

    match parseFull body with
    | Error e -> failwith e
    | Ok(a, _) ->
        Assert.Equal(None, a.Score)
        Assert.Empty a.Genres
        Assert.Empty a.Relations
        Assert.Equal("https://myanimelist.net/anime/1", a.Url)

[<Fact>]
let ``search responses parse in order; broken bodies are errors`` () =
    let body =
        """{"data":[{"mal_id":2,"title":"B","titles":[]},{"mal_id":1,"title":"A","titles":[]}],"pagination":{}}"""

    match parseSearch body with
    | Ok results -> Assert.Equal<int list>([ 2; 1 ], results |> List.map _.MalId)
    | Error e -> failwith e

    Assert.True(Result.isError (parseSearch "<html>"))
    Assert.True(Result.isError (parseFull """{"data":[]}"""))

[<Fact>]
let ``Jikan's error body is read`` () =
    let body =
        """{"status":504,"type":"BadResponseException","message":"Jikan failed to connect to MyAnimeList","error":null}"""

    Assert.Equal(
        Some
            { Status = Some 504
              Type = Some "BadResponseException"
              Message = Some "Jikan failed to connect to MyAnimeList" },
        parseError body
    )

    Assert.Equal(None, parseError "")
