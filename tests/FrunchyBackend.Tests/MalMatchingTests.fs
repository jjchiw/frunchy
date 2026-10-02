module FrunchyBackend.Tests.MalMatchingTests

open System
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure.MalModel
open FrunchyBackend.Infrastructure.MalMatching

// ---------------------------------------------------------------------------------------------
// Titles
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``normalize drops case, accents, punctuation, dub and year markers, a leading the`` () =
    Assert.Equal("frieren beyond journeys end", normalize "Frieren: Beyond Journey's End")
    Assert.Equal("pokemon", normalize "Pokémon")
    Assert.Equal("season 01", normalize "Season 01 (English Dub)")
    Assert.Equal("dragon quest the adventure of dai", normalize "Dragon Quest: The Adventure of Dai (2020)")
    Assert.Equal("fire hunter season 1", normalize "The Fire Hunter (TV): Season 1")
    Assert.Equal("jujutsu kaisen 0", normalize "Jujutsu Kaisen 0 Movie")
    Assert.Equal("bucchigiri", normalize "BUCCHIGIRI?!")
    Assert.Equal("takt op destiny", normalize "takt op.Destiny")

[<Fact>]
let ``season markers: Season N, Nth Season, roman numerals and parts`` () =
    Assert.Equal({ Base = "frieren"; Season = Some 2; Part = None }, mark (normalize "Frieren Season 2"))
    Assert.Equal({ Base = "sousou no frieren"; Season = Some 2; Part = None }, mark (normalize "Sousou no Frieren 2nd Season"))
    Assert.Equal({ Base = "mushoku tensei"; Season = Some 2; Part = Some 2 }, mark (normalize "Mushoku Tensei II Part 2"))
    Assert.Equal({ Base = "clevatess"; Season = Some 2; Part = None }, mark (normalize "Clevatess II"))
    Assert.Equal({ Base = ""; Season = Some 1; Part = None }, mark (normalize "Season 01"))
    // a trailing arabic number is part of the title
    Assert.Equal({ Base = "eyeshield 21"; Season = None; Part = None }, mark (normalize "Eyeshield 21"))
    Assert.Equal({ Base = "jujutsu kaisen 0"; Season = None; Part = None }, mark (normalize "JUJUTSU KAISEN 0"))

[<Fact>]
let ``relative season titles strip the series title`` () =
    Assert.Equal("season 2", relativeTitle "JUJUTSU KAISEN" "JUJUTSU KAISEN Season 2")
    Assert.Equal("season 01", relativeTitle "BUCCHIGIRI?!" "Bucchigiri?! - Season 01")
    Assert.Equal("", relativeTitle "Platinum End" "Platinum End")
    Assert.Equal("ii", relativeTitle "Clevatess" "Clevatess II")
    Assert.Equal("those awaiting a star", relativeTitle "The Ancient Magus' Bride" "The Ancient Magus' Bride: Those Awaiting a Star")
    Assert.Equal("season 1", relativeTitle "The Fire Hunter" "The Fire Hunter (TV): Season 1")

[<Fact>]
let ``title tiers: exact, then subtitle cut on one side; season numbers must agree`` () =
    Assert.Equal(Some 1, titleTier "Frieren: Beyond Journey's End" [ "Sousou no Frieren"; "Frieren: Beyond Journey's End" ])
    Assert.Equal(None, titleTier "Frieren: Beyond Journey's End" [ "Frieren: Beyond Journey's End Season 2" ])
    Assert.Equal(Some 2, titleTier "ROLL OVER AND DIE" [ "Roll Over and Die: I Will Fight for an Ordinary Life with My Love and Cursed Sword!" ])
    Assert.Equal(None, titleTier "Frieren" [ "Freedom" ])

// ---------------------------------------------------------------------------------------------
// Dubs
// ---------------------------------------------------------------------------------------------

let season id title number locale =
    { Id = id
      Title = Some title
      Number = Some number
      AudioLocale = Some locale
      EpisodeCount = 12
      MaxDurationMs = Some(24 * 60 * 1000) }

[<Fact>]
let ``dubs: a dub-marked title, or the same title and number in another audio, follow the original`` () =
    let tamer = season "GRK5CNJ14" "Season 01" 1 "ja-JP"
    let tamerDub = season "GYDQCGK5Q" "Season 01 (English Dub)" 1 "en-US"
    let rollJa = season "GS00365593JAJP" "Season 1" 1 "ja-JP"
    let rollEs = season "GS00365593ES419" "Season 1" 1 "es-419"

    let originals, dubs = splitDubs [ tamer; tamerDub ]
    Assert.Equal<string list>([ "GRK5CNJ14" ], originals |> List.map _.Id)
    Assert.Equal<(string * string) list>([ "GYDQCGK5Q", "GRK5CNJ14" ], dubs |> List.map (fun (d, o) -> d.Id, o.Id))

    let originals, dubs = splitDubs [ rollEs; rollJa ]
    Assert.Equal<string list>([ "GS00365593JAJP" ], originals |> List.map _.Id)
    Assert.Equal<(string * string) list>([ "GS00365593ES419", "GS00365593JAJP" ], dubs |> List.map (fun (d, o) -> d.Id, o.Id))

    // A dub without its original is matched like an original
    let originals, dubs = splitDubs [ tamerDub ]
    Assert.Equal<string list>([ "GYDQCGK5Q" ], originals |> List.map _.Id)
    Assert.Empty dubs

// ---------------------------------------------------------------------------------------------
// Resolving series against an in-memory MyAnimeList
// ---------------------------------------------------------------------------------------------

let anime id (title: string) (english: string option) (kind: string) (aired: string) (relations: (string * int list) list) =
    { MalId = id
      Url = $"https://myanimelist.net/anime/{id}"
      Title = title
      TitleEnglish = english
      TitleJapanese = None
      Synonyms = []
      Type = Some kind
      Episodes = Some 12
      Status = Some "Finished Airing"
      Airing = false
      AiredFrom = Some(DateOnly.Parse aired)
      Year = Some(DateOnly.Parse(aired).Year)
      Season = None
      Score = Some 8.5m
      ImageUrl = None
      Rating = None
      Genres = [ { Id = 1; Name = "Action" } ]
      ExplicitGenres = []
      Themes = []
      Demographics = []
      Studios = []
      Relations =
        relations
        |> List.map (fun (r, ids) -> { Relation = r; Entries = ids |> List.map (fun i -> { Id = i; Name = $"#{i}" }) }) }

/// A lookup over a fixed catalog; searches return the given ids; counts requests
type Catalog(entries: MalAnime list, searches: (string * int list) list) =
    let titles = entries |> List.map (fun a -> a.MalId, a.Title) |> Map.ofList

    // Relation entries carry the related anime's default title, as the providers' do
    let byId =
        entries
        |> List.map (fun a ->
            a.MalId,
            { a with
                Relations =
                    a.Relations
                    |> List.map (fun r ->
                        { r with
                            Entries = r.Entries |> List.map (fun e -> { e with Name = titles.TryFind e.Id |> Option.defaultValue e.Name }) }) })
        |> Map.ofList
    member val Requests = ResizeArray<string>()

    member this.Lookup: Lookup =
        { Search =
            fun q ->
                this.Requests.Add $"search:{q}"

                match searches |> List.tryFind (fun (query, _) -> query = q) with
                | Some(_, ids) -> Task.FromResult(Got(ids |> List.choose byId.TryFind))
                | None -> Task.FromResult(Got [])
          Full =
            fun id ->
                this.Requests.Add $"details:{id}"

                match byId.TryFind id with
                | Some a -> Task.FromResult(Got a)
                | None -> Task.FromResult Gone }

let series title seasons =
    { SeriesId = "S"; Title = title; Seasons = seasons }

let resolve (catalog: Catalog) (s: SeriesInfo) =
    (resolveSeries catalog.Lookup s (s.Seasons |> List.map _.Id |> Set.ofList)).Result

let matchedId (outcome: Outcome) =
    match outcome with
    | Decided(Matched(id, _, _)) -> Some id
    | _ -> None

[<Fact>]
let ``Frieren: S1 by search, S2 through the sequel link`` () =
    let s1 = anime 52991 "Sousou no Frieren" (Some "Frieren: Beyond Journey's End") "TV" "2023-09-29" [ "Sequel", [ 59978 ] ]
    let s2 = anime 59978 "Sousou no Frieren 2nd Season" (Some "Frieren: Beyond Journey's End Season 2") "TV" "2026-01-16" [ "Prequel", [ 52991 ] ]
    let catalog = Catalog([ s1; s2 ], [ "Frieren: Beyond Journey's End", [ 52991; 59978 ] ])

    let outcomes =
        resolve catalog (series "Frieren: Beyond Journey's End" [ season "A" "Season 1" 1 "ja-JP"; season "B" "Season 2" 2 "ja-JP" ])

    Assert.Equal(Decided(Matched(52991, BySearch, None)), outcomes["A"])
    Assert.Equal(Decided(Matched(59978, ByLink, None)), outcomes["B"])
    Assert.Equal(1, catalog.Requests |> Seq.filter (fun r -> r.StartsWith "search:") |> Seq.length)

[<Fact>]
let ``JUJUTSU KAISEN: Season 3 numbered 5 is the 3rd season of the chain; 0 is the movie`` () =
    let s1 = anime 40748 "Jujutsu Kaisen" (Some "Jujutsu Kaisen") "TV" "2020-10-03" [ "Sequel", [ 51009 ]; "Prequel", [ 48561 ] ]
    let s2 = anime 51009 "Jujutsu Kaisen 2nd Season" (Some "Jujutsu Kaisen Season 2") "TV" "2023-07-06" [ "Prequel", [ 40748 ]; "Sequel", [ 57658 ] ]
    let s3 = anime 57658 "Jujutsu Kaisen: Shimetsu Kaiyuu - Zenpen" (Some "Jujutsu Kaisen: The Culling Game Part 1") "TV" "2026-01-08" [ "Prequel", [ 51009 ] ]
    let movie = anime 48561 "Jujutsu Kaisen 0 Movie" (Some "Jujutsu Kaisen 0") "Movie" "2021-12-24" [ "Sequel", [ 40748 ] ]
    let catalog = Catalog([ s1; s2; s3; movie ], [ "JUJUTSU KAISEN", [ 40748; 51009; 48561 ] ])

    let zero =
        { season "M" "JUJUTSU KAISEN 0" 0 "ja-JP" with
            EpisodeCount = 1
            MaxDurationMs = Some(105 * 60 * 1000) }

    let outcomes =
        resolve
            catalog
            (series
                "JUJUTSU KAISEN"
                [ season "A" "JUJUTSU KAISEN" 1 "ja-JP"
                  season "B" "JUJUTSU KAISEN Season 2" 2 "ja-JP"
                  season "C" "JUJUTSU KAISEN Season 3" 5 "ja-JP"
                  zero ])

    Assert.Equal(Some 40748, matchedId outcomes["A"])
    Assert.Equal(Some 51009, matchedId outcomes["B"])
    Assert.Equal(Some 57658, matchedId outcomes["C"])
    Assert.Equal(Some 48561, matchedId outcomes["M"])

[<Fact>]
let ``a season with no TV sequel on MyAnimeList shares the anchor (Eyeshield 21, One Piece arcs)`` () =
    let eyeshield = anime 15 "Eyeshield 21" None "TV" "2005-04-06" [ "Side Story", [ 999 ] ]
    let onePiece = anime 21 "One Piece" (Some "One Piece") "TV" "1999-10-20" [ "Side Story", [ 459 ] ]
    let catalog = Catalog([ eyeshield; onePiece ], [ "Eyeshield 21", [ 15 ]; "One Piece", [ 21 ] ])

    let e = resolve catalog (series "Eyeshield 21" [ season "A" "Eyeshield 21: Season 1" 1 "ja-JP"; season "B" "Eyeshield 21: Season 2" 2 "ja-JP" ])
    Assert.Equal(Some 15, matchedId e["A"])
    Assert.Equal(Some 15, matchedId e["B"])

    let o = resolve catalog (series "One Piece" [ season "W" "Land of Wano (892-1088)" 21 "ja-JP"; season "E" "Elbaph (1156-current)" 24 "ja-JP" ])
    Assert.Equal(Some 21, matchedId o["W"])
    Assert.Equal(Some 21, matchedId o["E"])

[<Fact>]
let ``a later season alone (Classroom of the Elite S2 without S1) is placed relative to the franchise`` () =
    let s1 = anime 35507 "Youkoso Jitsuryoku Shijou Shugi no Kyoushitsu e" (Some "Classroom of the Elite") "TV" "2017-07-12" [ "Sequel", [ 51096 ] ]
    let s2 = anime 51096 "Youkoso Jitsuryoku Shijou Shugi no Kyoushitsu e 2nd Season" (Some "Classroom of the Elite Season 2") "TV" "2022-07-04" [ "Sequel", [ 51180 ] ]
    let s3 = anime 51180 "Youkoso Jitsuryoku Shijou Shugi no Kyoushitsu e 3rd Season" (Some "Classroom of the Elite Season 3") "TV" "2024-01-03" []
    let catalog = Catalog([ s1; s2; s3 ], [ "Classroom of the Elite", [ 35507; 51096; 51180 ] ])

    let o = resolve catalog (series "Classroom of the Elite" [ season "B" "Season 2" 2 "ja-JP"; season "C" "Season 3" 3 "ja-JP" ])
    Assert.Equal(Some 51096, matchedId o["B"])
    Assert.Equal(Some 51180, matchedId o["C"])

[<Fact>]
let ``cours marked Part 2 don't count as seasons (Mushoku Tensei Season 3)`` () =
    let s1 = anime 39535 "Mushoku Tensei: Isekai Ittara Honki Dasu" (Some "Mushoku Tensei: Jobless Reincarnation") "TV" "2021-01-11" [ "Sequel", [ 45576 ] ]
    let p2 = anime 45576 "Mushoku Tensei: Isekai Ittara Honki Dasu Part 2" (Some "Mushoku Tensei: Jobless Reincarnation Part 2") "TV" "2021-10-04" [ "Sequel", [ 51179 ] ]
    let ii = anime 51179 "Mushoku Tensei II: Isekai Ittara Honki Dasu" (Some "Mushoku Tensei: Jobless Reincarnation Season 2") "TV" "2023-07-03" [ "Sequel", [ 55888 ] ]
    let iip2 = anime 55888 "Mushoku Tensei II: Isekai Ittara Honki Dasu Part 2" (Some "Mushoku Tensei: Jobless Reincarnation Season 2 Part 2") "TV" "2024-04-08" [ "Sequel", [ 60000 ] ]
    let iii = anime 60000 "Mushoku Tensei III: Isekai Ittara Honki Dasu" (Some "Mushoku Tensei: Jobless Reincarnation Season 3") "TV" "2026-07-01" []
    let catalog = Catalog([ s1; p2; ii; iip2; iii ], [ "Mushoku Tensei: Jobless Reincarnation", [ 39535; 45576; 51179 ] ])

    let o = resolve catalog (series "Mushoku Tensei: Jobless Reincarnation" [ season "C" "Season 3" 3 "ja-JP" ])
    Assert.Equal(Some 60000, matchedId o["C"])

[<Fact>]
let ``a descriptive season title matches a linked entry by the combined title (MASHLE arc)`` () =
    let s1 = anime 52211 "Mashle" (Some "Mashle: Magic and Muscles") "TV" "2023-04-08" [ "Sequel", [ 55651 ] ]
    let s2 = anime 55651 "Mashle 2nd Season" (Some "Mashle: Magic and Muscles - The Divine Visionary Candidate Exam Arc") "TV" "2024-01-07" []
    let catalog = Catalog([ s1; s2 ], [ "MASHLE: MAGIC AND MUSCLES", [ 52211; 55651 ] ])

    let o = resolve catalog (series "MASHLE: MAGIC AND MUSCLES" [ season "B" "The Divine Visionary Candidate Exam Arc" 2 "ja-JP" ])
    Assert.Equal(Decided(Matched(55651, ByLink, None)), o["B"])

[<Fact>]
let ``a year in the series title picks the remake`` () =
    let old = anime 2242 "Dragon Quest: Dai no Daibouken" (Some "Dragon Quest: The Adventure of Dai") "TV" "1991-10-17" []
    let remake = anime 40906 "Dragon Quest: Dai no Daibouken (2020)" (Some "Dragon Quest: The Adventure of Dai") "TV" "2020-10-03" []
    let catalog = Catalog([ old; remake ], [ "Dragon Quest: The Adventure of Dai (2020)", [ 2242; 40906 ] ])

    let o =
        resolve catalog (series "Dragon Quest: The Adventure of Dai (2020)" [ season "A" "Dragon Quest: The Adventure of Dai" 1 "ja-JP" ])

    Assert.Equal(Some 40906, matchedId o["A"])

[<Fact>]
let ``several first seasons that aren't one chain are ambiguous, never guessed`` () =
    let a = anime 1 "Hunter x Hunter" (Some "Hunter x Hunter") "TV" "1999-10-16" []
    let b = anime 11061 "Hunter x Hunter (2011)" (Some "Hunter x Hunter") "TV" "2011-10-02" []
    let catalog = Catalog([ a; b ], [ "Hunter x Hunter", [ 1; 11061 ] ])

    let o = resolve catalog (series "Hunter x Hunter" [ season "A" "Season 1" 1 "ja-JP" ])

    match o["A"] with
    | Decided(Ambiguous("several-first-seasons", candidates)) ->
        Assert.Equal<int list>([ 1; 11061 ], candidates |> List.map _.MalId |> List.sort)
    | other -> failwith $"expected ambiguous, got %A{other}"

[<Fact>]
let ``no qualifying title is no match; a temporary failure defers instead of deciding`` () =
    let other = anime 7 "Something Else" None "TV" "2010-01-01" []
    let catalog = Catalog([ other ], [ "Platinum End", [ 7 ] ])
    let o = resolve catalog (series "Platinum End" [ season "A" "Platinum End" 1 "ja-JP" ])

    match o["A"] with
    | Decided(NoMatch("no-title-match", _)) -> ()
    | x -> failwith $"expected no match, got %A{x}"

    let failing =
        { Search = fun _ -> Task.FromResult(Unavailable "504 BadResponseException")
          Full = fun _ -> Task.FromResult(Unavailable "504") }

    let deferred =
        (resolveSeries failing (series "Platinum End" [ season "A" "Platinum End" 1 "ja-JP" ]) (set [ "A" ])).Result

    Assert.Equal(Deferred "504 BadResponseException", deferred["A"])

[<Fact>]
let ``a dub season shares its original's match without lookups of its own`` () =
    let s1 = anime 52991 "Sousou no Frieren" (Some "Frieren: Beyond Journey's End") "TV" "2023-09-29" []
    let catalog = Catalog([ s1 ], [ "Frieren: Beyond Journey's End", [ 52991 ] ])

    let o =
        resolve
            catalog
            (series "Frieren: Beyond Journey's End" [ season "A" "Season 1" 1 "ja-JP"; season "D" "Season 1 (English Dub)" 1 "en-US" ])

    Assert.Equal(Decided(Matched(52991, BySearch, None)), o["A"])
    Assert.Equal(Decided(Matched(52991, ByDub, Some "A")), o["D"])
    Assert.Equal(1, catalog.Requests |> Seq.filter (fun r -> r.StartsWith "search:") |> Seq.length)

[<Fact>]
let ``decisions are deterministic`` () =
    let s1 = anime 52991 "Sousou no Frieren" (Some "Frieren: Beyond Journey's End") "TV" "2023-09-29" [ "Sequel", [ 59978 ] ]
    let s2 = anime 59978 "Sousou no Frieren 2nd Season" (Some "Frieren: Beyond Journey's End Season 2") "TV" "2026-01-16" []
    let catalog = Catalog([ s1; s2 ], [ "Frieren: Beyond Journey's End", [ 52991; 59978 ] ])
    let s = series "Frieren: Beyond Journey's End" [ season "A" "Season 1" 1 "ja-JP"; season "B" "Season 2" 2 "ja-JP" ]
    Assert.Equal<Map<string, Outcome>>(resolve catalog s, resolve catalog s)
