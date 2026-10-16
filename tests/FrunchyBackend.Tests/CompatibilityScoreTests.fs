module FrunchyBackend.Tests.CompatibilityScoreTests

open System
open Xunit
open FrunchyBackend.Infrastructure.Compatibility

// Feature 011: the score, the levels and the sections, as pure functions (research R3, R4). No database.

let private genres (xs: (string * int64) list) = Map.ofList xs
let private ids (xs: string list) = Set.ofList xs

// --- Bands -------------------------------------------------------------------

[<Theory>]
[<InlineData(0.0, 1)>]
[<InlineData(0.1999, 1)>]
[<InlineData(0.2, 2)>]
[<InlineData(0.3999, 2)>]
[<InlineData(0.4, 3)>]
[<InlineData(0.5999, 3)>]
[<InlineData(0.6, 4)>]
[<InlineData(0.7999, 4)>]
[<InlineData(0.8, 5)>]
[<InlineData(1.0, 5)>]
let ``the level follows the bands, each boundary belongs to the level above`` (value: float, expected: int) =
    Assert.Equal(expected, level value)

// --- Cosine ------------------------------------------------------------------

[<Fact>]
let ``identical vectors give 1`` () =
    let g = genres [ "Action", 300L; "Comedy", 100L; "Drama", 50L ]
    Assert.Equal(1.0, cosine g g)
    // the same shape at another scale is still identical
    Assert.Equal(1.0, cosine g (g |> Map.map (fun _ v -> v * 7L)), 12)

[<Fact>]
let ``disjoint vectors give 0`` () =
    Assert.Equal(0.0, cosine (genres [ "Action", 10L ]) (genres [ "Comedy", 10L ]))

[<Fact>]
let ``a known pair gives its expected value`` () =
    // (1,1) . (1,0) / (sqrt 2 * 1) = 1 / sqrt 2
    let a = genres [ "Action", 1L; "Comedy", 1L ]
    let b = genres [ "Action", 1L ]
    Assert.Equal(1.0 / sqrt 2.0, cosine a b, 12)

[<Fact>]
let ``an empty vector gives 0`` () =
    Assert.Equal(0.0, cosine Map.empty (genres [ "Action", 10L ]))

// --- Shared series -----------------------------------------------------------

[<Fact>]
let ``shared series are measured against the smaller history`` () =
    let small = ids [ "A"; "B"; "C"; "D" ]
    let big = ids [ "A"; "B"; "C"; "D"; "E"; "F"; "G"; "H"; "I"; "J" ]
    // every series of the newcomer is in the big history
    Assert.Equal(1.0, sharedSignal small big)
    Assert.Equal(1.0, sharedSignal big small)
    Assert.Equal(0.5, sharedSignal (ids [ "A"; "B"; "X"; "Y" ]) big)

[<Fact>]
let ``an empty side gives 0 shared series`` () =
    Assert.Equal(0.0, sharedSignal Set.empty (ids [ "A" ]))
    Assert.Equal(0.0, sharedSignal (ids [ "A" ]) Set.empty)
    Assert.Equal(0.0, sharedSignal Set.empty Set.empty)

// --- Fallback ----------------------------------------------------------------

[<Fact>]
let ``the score is the shared signal alone when either genre map is empty`` () =
    let a = ids [ "A"; "B"; "C"; "D" ]
    let b = ids [ "A"; "B"; "X"; "Y" ]
    let g = genres [ "Action", 10L ]
    Assert.Equal(0.5, score a Map.empty b g)
    Assert.Equal(0.5, score a g b Map.empty)
    Assert.Equal(0.5, score a Map.empty b Map.empty)

[<Fact>]
let ``the score averages the shared signal and the genre cosine`` () =
    let a = ids [ "A"; "B" ]
    let b = ids [ "A"; "B" ]
    // shared = 1, cosine = 0
    Assert.Equal(0.5, score a (genres [ "Action", 1L ]) b (genres [ "Comedy", 1L ]))
    // shared = 1, cosine = 1
    Assert.Equal(1.0, score a (genres [ "Action", 1L ]) b (genres [ "Action", 5L ]), 12)

// --- Symmetry ----------------------------------------------------------------

[<Fact>]
let ``the score is the same from either side on 200 random pairs`` () =
    let random = Random 20261015
    let names = [| "Action"; "Comedy"; "Drama"; "Fantasy"; "Romance"; "Sci-Fi"; "Slice of Life"; "Horror"; "Sports" |]
    let pool = [| for i in 1..60 -> $"S{i}" |]

    let history () =
        let series = pool |> Array.filter (fun _ -> random.Next 3 = 0) |> Set.ofArray

        let g =
            names
            |> Array.choose (fun n -> if random.Next 2 = 0 then Some(n, int64 (random.Next(1, 500_000))) else None)
            |> Map.ofArray

        series, g

    for _ in 1..200 do
        let sa, ga = history ()
        let sb, gb = history ()
        let forward = score sa ga sb gb
        let backward = score sb gb sa ga
        Assert.Equal(forward, backward)
        Assert.Equal(level forward, level backward)

// --- Shared genres (card) ----------------------------------------------------

let private shares (xs: (string * decimal) list) = Map.ofList xs

[<Fact>]
let ``shared genres are in both top 5, ordered by summed share, up to 3`` () =
    let a = shares [ "Action", 40m; "Comedy", 30m; "Drama", 10m; "Fantasy", 8m; "Romance", 6m; "Horror", 6m ]
    let b = shares [ "Comedy", 35m; "Action", 20m; "Drama", 15m; "Horror", 12m; "Sports", 10m; "Fantasy", 1m ]
    // a's top 5: Action Comedy Drama Fantasy Romance (Horror ties Romance at 6, Horror > Romance by name? "Horror" < "Romance")
    // b's top 5: Comedy Action Drama Horror Sports
    // in both: Action (60), Comedy (65), Drama (25)
    Assert.Equal<string list>([ "Comedy"; "Action"; "Drama" ], sharedGenres a b)

[<Fact>]
let ``shared genres are capped at 3 and need both top 5`` () =
    let all = shares [ "A", 20m; "B", 19m; "C", 18m; "D", 17m; "E", 16m ]
    Assert.Equal(3, (sharedGenres all all).Length)
    // a genre outside one person's top 5 isn't shared
    let other = shares [ "V", 30m; "W", 25m; "X", 20m; "Y", 15m; "Z", 9m; "A", 1m ]
    Assert.Empty(sharedGenres all other)
    Assert.Empty(sharedGenres Map.empty all)

// --- Genre rows (page) -------------------------------------------------------

[<Fact>]
let ``genre rows are the union of the top 4, ordered by summed share, up to 6`` () =
    let viewer = shares [ "Action", 40m; "Comedy", 30m; "Drama", 15m; "Fantasy", 10m; "Romance", 5m ]
    let owner = shares [ "Horror", 45m; "Comedy", 25m; "Sports", 15m; "Action", 10m; "Drama", 5m ]
    let rows = genreRows viewer owner
    // top 4 viewer: Action Comedy Drama Fantasy; top 4 owner: Horror Comedy Sports Action; union of 7, up to 6
    Assert.Equal<string list>([ "Comedy"; "Action"; "Horror"; "Drama"; "Sports"; "Fantasy" ], rows |> List.map _.Name)
    let comedy = rows |> List.find (fun r -> r.Name = "Comedy")
    Assert.Equal(30m, comedy.ViewerShare)
    Assert.Equal(25m, comedy.OwnerShare)
    // a genre only one of them has shows 0 for the other
    let horror = rows |> List.find (fun r -> r.Name = "Horror")
    Assert.Equal(0m, horror.ViewerShare)
    Assert.Equal(45m, horror.OwnerShare)

[<Fact>]
let ``genre shares are percents of the matched minutes`` () =
    let result = genreShares 200L (genres [ "Action", 50L; "Comedy", 200L ])
    Assert.Equal(25m, result["Action"])
    Assert.Equal(100m, result["Comedy"])
    Assert.Empty(genreShares 0L (genres [ "Action", 50L ]))
