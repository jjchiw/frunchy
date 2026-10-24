module FrunchyBackend.Tests.RecommendationRulesTests

open System
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Profiles
open FrunchyBackend.Infrastructure.Recommendations

// Feature 025: the pure rules of the recommendations (visibility filter, matches, ranking). No database.

let private id' (n: int) = Guid(n, 0s, 0s, [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy |])

let private person (name: string) (series: (string * string) list) : Compatibility.Person =
    { Compatibility.emptyPerson with
        Username = name
        DisplayName = name.ToUpperInvariant()
        FullyWatched = 20L
        Series =
            series
            |> List.map (fun (id, title) -> id, ({ Title = title; Ms = 1000L; FullyWatched = 2L }: Compatibility.SeriesFacts))
            |> Map.ofList }

/// n series shared by everybody: S00, S01…
let private shared (n: int) = [ for i in 0 .. n - 1 -> $"S{i:D2}", $"Shared {i}" ]

let private matchOf (n: int) (name: string) (level: int) (score: float) : Match =
    { UserId = id' n
      Username = name
      DisplayName = name.ToUpperInvariant()
      Level = level
      Score = score
      Person = person name [ "X1", "Title X1"; "X2", "Title X2" ] }

let private access (n: int) (visibility: Visibility) (link: Guid option) : OwnerAccess =
    { UserId = id' n
      Choice = Some visibility
      Visibility = visibility
      LinkId = link }

let private candidate (n: int) (name: string) (visibility: Visibility) (link: Guid option) (sharedCount: int) : Candidate =
    { Access = access n visibility link
      Username = name
      Shared = sharedCount }

let private names (xs: Candidate list) = xs |> List.map _.Username
let private viewer = id' 99

// --- Visibility filter ---------------------------------------------------------

[<Fact>]
let ``the viewer sees public and members people, a private one only with the grant of the current link`` () =
    let link = Guid.NewGuid()

    let people =
        [ candidate 1 "pub" Public None 5
          candidate 2 "mem" Members None 5
          candidate 3 "priv" Private (Some link) 5
          candidate 4 "private-nolink" Private None 5 ]

    Assert.Equal<string list>([ "mem"; "pub" ], names (visibleCandidates viewer [] people))

    let grant = { UserId = id' 3; LinkId = link; At = 0L }
    Assert.Equal<string list>([ "mem"; "priv"; "pub" ], names (visibleCandidates viewer [ grant ] people))

    // a grant of a replaced link counts for nothing
    let stale = { grant with LinkId = Guid.NewGuid() }
    Assert.Equal<string list>([ "mem"; "pub" ], names (visibleCandidates viewer [ stale ] people))

    // a grant for another user doesn't open this one, and a grant on the private user with no link opens nothing
    let wrong = { grant with UserId = id' 4 }
    Assert.Equal<string list>([ "mem"; "pub" ], names (visibleCandidates viewer [ wrong ] people))

[<Fact>]
let ``the viewer themself is never a candidate`` () =
    let me = candidate 99 "me" Public None 9
    Assert.Empty(visibleCandidates viewer [] [ me ])

[<Fact>]
let ``candidates sort by shared series then username and stop at the maximum`` () =
    let people =
        [ candidate 1 "b" Public None 3
          candidate 2 "a" Public None 3
          candidate 3 "c" Public None 7 ]

    Assert.Equal<string list>([ "c"; "a"; "b" ], names (visibleCandidates viewer [] people))

    let many = [ for i in 1..60 -> candidate i $"u{i:D2}" Public None (if i <= 5 then 100 else 1) ]
    let kept = visibleCandidates viewer [] many
    Assert.Equal(maxCandidates, kept.Length)
    // the five with the most shared series are in, in username order, then the rest by username
    Assert.Equal<string list>([ "u01"; "u02"; "u03"; "u04"; "u05"; "u06" ], names kept |> List.truncate 6)

// --- Matches -------------------------------------------------------------------

[<Fact>]
let ``a match needs level 3 or more, and the best scores come first with username ties`` () =
    let me = person "me" (shared 10)
    // same 10 series: score 1.0, level 5
    let same name = id' (name.GetHashCode() % 1000), person name (shared 10)
    // 6 of 10 shared series: 0.6, level 4
    let some = id' 2, person "some" (shared 6 @ [ for i in 0..3 -> $"O{i}", "other" ])
    // 3 of 10: 0.3, level 2: not a match
    let little = id' 3, person "little" (shared 3 @ [ for i in 0..6 -> $"P{i}", "other" ])
    let found = matches me [ little; some; (id' 5, person "zed" (shared 10)); (id' 4, person "amy" (shared 10)) ]

    Assert.Equal<string list>([ "amy"; "zed"; "some" ], found |> List.map _.Username)
    Assert.Equal<int list>([ 5; 5; 4 ], found |> List.map _.Level)
    Assert.Equal(id' 4, found.Head.UserId)
    ignore same

[<Fact>]
let ``a short history has no level and is not a match`` () =
    let me = person "me" (shared 10)
    let short = { person "short" (shared 10) with FullyWatched = 14L }
    Assert.Empty(matches me [ id' 1, short ])
    Assert.Empty(matches { me with FullyWatched = 3L } [ id' 1, person "other" (shared 10) ])

[<Fact>]
let ``at most the ten best matches are kept`` () =
    let me = person "me" (shared 10)
    let people = [ for i in 1..14 -> id' i, person $"p{i:D2}" (shared 10) ]
    let found = matches me people
    Assert.Equal(maxMatches, found.Length)
    Assert.Equal<string list>([ for i in 1..10 -> $"p{i:D2}" ], found |> List.map _.Username)

// --- Ranking -------------------------------------------------------------------

let private titles (rs: Recommendation list) = rs |> List.map _.SeriesId

[<Fact>]
let ``series rank by the best score among finishers, then how many finished, then title, then id`` () =
    let high = matchOf 1 "high" 5 0.95
    let mid = matchOf 2 "mid" 4 0.7
    let low = matchOf 3 "low" 3 0.5
    // titles come from the best finisher's person: give them distinct names per series
    let p = { high.Person with Series = Map.ofList [ "A1", { Title = "Beta"; Ms = 1L; FullyWatched = 1L }; "A2", { Title = "Alpha"; Ms = 1L; FullyWatched = 1L }; "A3", { Title = "Alpha"; Ms = 1L; FullyWatched = 1L } ] }
    let high = { high with Person = p }
    let mid = { mid with Person = { mid.Person with Series = Map.ofList [ "M1", { Title = "Gamma"; Ms = 1L; FullyWatched = 1L }; "A1", { Title = "Beta"; Ms = 1L; FullyWatched = 1L } ] } }
    let low = { low with Person = { low.Person with Series = Map.ofList [ "L1", { Title = "Delta"; Ms = 1L; FullyWatched = 1L } ] } }

    let through =
        [ high.UserId, "A1"      // best 0.95, two finishers
          mid.UserId, "A1"
          high.UserId, "A2"      // best 0.95, one finisher, title Alpha, id A2
          high.UserId, "A3"      // same title, later id
          mid.UserId, "M1"       // best 0.7
          low.UserId, "L1" ]     // best 0.5

    let ranked = recommend [ high; mid; low ] through
    // A1 (two finishers) first, then A2 and A3 (same score and count, same title: by id), M1, L1
    Assert.Equal<string list>([ "A1"; "A2"; "A3"; "M1"; "L1" ], titles ranked)
    Assert.Equal<string list>([ "high"; "mid" ], ranked.Head.Finishers |> List.map _.Username)
    Assert.Equal<int list>([ 5; 4 ], ranked.Head.Finishers |> List.map _.Level)
    Assert.Equal("Beta", ranked.Head.Title)

[<Fact>]
let ``the title breaks a tie of score and count`` () =
    let a = { matchOf 1 "a" 5 0.9 with Person = person "a" [ "Z1", "Zeta"; "Y1", "Yankee" ] }
    let ranked = recommend [ a ] [ a.UserId, "Z1"; a.UserId, "Y1" ]
    Assert.Equal<string list>([ "Y1"; "Z1" ], titles ranked)

[<Fact>]
let ``finishers with the same score are ordered by username, people who are not matches never appear`` () =
    let b = matchOf 1 "b" 4 0.8
    let a = matchOf 2 "a" 4 0.8
    let stranger = id' 77
    let ranked = recommend [ b; a ] [ b.UserId, "X1"; a.UserId, "X1"; stranger, "X1"; stranger, "ONLY" ]
    Assert.Equal<string list>([ "X1" ], titles ranked)
    Assert.Equal<string list>([ "a"; "b" ], ranked.Head.Finishers |> List.map _.Username)

[<Fact>]
let ``a duplicated pair counts once`` () =
    let a = matchOf 1 "a" 4 0.8
    let ranked = recommend [ a ] [ a.UserId, "X1"; a.UserId, "X1" ]
    Assert.Equal(1, ranked.Head.Finishers.Length)

[<Fact>]
let ``no more than twelve series are kept`` () =
    let m = { matchOf 1 "a" 5 0.9 with Person = person "a" [ for i in 0..19 -> $"S{i:D2}", $"T{i:D2}" ] }
    let ranked = recommend [ m ] [ for i in 0..19 -> m.UserId, $"S{i:D2}" ]
    Assert.Equal(maxSeries, ranked.Length)
    Assert.Equal<string list>([ for i in 0 .. maxSeries - 1 -> $"S{i:D2}" ], titles ranked)
