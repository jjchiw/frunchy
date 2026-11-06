module FrunchyBackend.Tests.ProgressUnitTests

open System
open Xunit
open FrunchyBackend.Handlers.Users
open FrunchyBackend.Infrastructure.Progress

// Feature 031: the pure rules of the Progress tab (specs/031-progress/contracts/hooks.md).

let private now = DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)

let private entry (malId: int) (watched: int) (total: int option) (lastWatch: DateTimeOffset) : Entry =
    { MalId = malId
      SeriesId = $"SERIES{malId}"
      Title = $"Show {malId}"
      SeasonTitle = None
      Slug = None
      Poster = None
      Watched = watched
      Total = total
      Airing = false
      LastWatch = lastWatch
      NewestAired = None
      LongRunner = false
      NextNumber = None
      NextEpisode = None }

let private daysAgo (days: float) = now - TimeSpan.FromDays days

let private stateOfEntry (e: Entry) = stateOf now e

[<Fact>]
let ``the constants are the ones the spec names`` () =
    Assert.Equal(30, WatchingDays)
    Assert.Equal(21, AiringKeepUpDays)
    Assert.Equal(12, CompletedListed)

[<Fact>]
let ``completed means at least the known total, so an extra recap episode still counts`` () =
    Assert.Equal(Completed, stateOfEntry (entry 1 12 (Some 12) (daysAgo 200.0)))
    Assert.Equal(Completed, stateOfEntry (entry 1 13 (Some 12) (daysAgo 200.0)))
    Assert.NotEqual(Completed, stateOfEntry (entry 1 11 (Some 12) (daysAgo 200.0)))

[<Fact>]
let ``a completed show stays completed however recent its last watch`` () =
    Assert.Equal(Completed, stateOfEntry (entry 1 12 (Some 12) (daysAgo 0.0)))

[<Fact>]
let ``an unknown total is never completed`` () =
    Assert.Equal(Unfinished, stateOfEntry (entry 1 500 None (daysAgo 200.0)))
    Assert.Equal(Watching, stateOfEntry (entry 1 500 None (daysAgo 1.0)))

[<Fact>]
let ``watching is a last watch of at most 30 days, to the second`` () =
    Assert.Equal(Watching, stateOfEntry (entry 1 4 (Some 12) (daysAgo 30.0)))
    Assert.Equal(Unfinished, stateOfEntry (entry 1 4 (Some 12) (daysAgo 30.0 - TimeSpan.FromSeconds 1.0)))
    Assert.Equal(Watching, stateOfEntry (entry 1 4 (Some 12) (daysAgo 29.0)))
    Assert.Equal(Unfinished, stateOfEntry (entry 1 4 (Some 12) (daysAgo 90.0)))

[<Fact>]
let ``an airing show whose newest episode aired within 21 days is kept up with`` () =
    let airing aired =
        { entry 1 4 None (daysAgo 60.0) with
            Airing = true
            NewestAired = aired }

    Assert.Equal(Watching, stateOfEntry (airing (Some(daysAgo 5.0))))
    Assert.Equal(Watching, stateOfEntry (airing (Some(daysAgo 21.0))))
    Assert.Equal(Unfinished, stateOfEntry (airing (Some(daysAgo 21.0 - TimeSpan.FromSeconds 1.0))))
    Assert.Equal(Unfinished, stateOfEntry (airing (Some(daysAgo 60.0))))
    Assert.Equal(Unfinished, stateOfEntry (airing None))

[<Fact>]
let ``the keep-up rule is for airing shows only`` () =
    let finished =
        { entry 1 4 (Some 12) (daysAgo 60.0) with
            NewestAired = Some(daysAgo 5.0) }

    Assert.Equal(Unfinished, stateOfEntry finished)

[<Fact>]
let ``a long runner takes the same states`` () =
    let long days =
        { entry 1 266 (Some 1155) (daysAgo days) with LongRunner = true }

    Assert.Equal(Watching, stateOfEntry (long 3.0))
    Assert.Equal(Unfinished, stateOfEntry (long 400.0))

[<Fact>]
let ``an entry with no fully watched episode is not started and is dropped`` () =
    let result = classify now [ entry 1 0 (Some 12) (daysAgo 1.0); entry 2 1 (Some 12) (daysAgo 1.0) ]
    Assert.Equal<int list>([ 2 ], result.Watching |> List.map _.MalId)
    Assert.Empty result.Unfinished
    Assert.Equal(0, result.CompletedCount)

[<Fact>]
let ``each state lists the newest watch first, ties by title then id`` () =
    let at = daysAgo 100.0

    let result =
        classify
            now
            [ entry 3 2 (Some 12) (daysAgo 200.0)
              { entry 2 2 (Some 12) at with Title = "Bravo" }
              { entry 1 2 (Some 12) at with Title = "Alpha" }
              { entry 5 2 (Some 12) at with Title = "Alpha" }
              entry 4 2 (Some 12) (daysAgo 150.0) ]

    // 1 and 5 are both "Alpha" at the same time: the lower id first
    Assert.Equal<int list>([ 1; 5; 2; 4; 3 ], result.Unfinished |> List.map _.MalId)

    let watching = classify now [ entry 1 2 (Some 12) (daysAgo 10.0); entry 2 2 (Some 12) (daysAgo 2.0) ]
    Assert.Equal<int list>([ 2; 1 ], watching.Watching |> List.map _.MalId)

[<Fact>]
let ``the completed list is the 12 newest while the count is all of them`` () =
    let completed = [ for i in 1..15 -> entry i 12 (Some 12) (daysAgo (float (i * 10))) ]
    let result = classify now completed

    Assert.Equal(15, result.CompletedCount)
    Assert.Equal<int list>([ 1..12 ], result.Completed |> List.map _.MalId)

[<Fact>]
let ``the result doesn't depend on the order of the entries`` () =
    let entries =
        [ for i in 1..20 -> entry i (if i % 3 = 0 then 12 else 3) (Some 12) (daysAgo (float ((i * 37) % 120))) ]

    let forward = classify now entries
    let backward = classify now (List.rev entries)
    Assert.Equal(forward, backward)

[<Fact>]
let ``the next episode is one above the highest watched, never past a known total`` () =
    Assert.Equal(Some 5, nextNumber (Some 4) (Some 12))
    Assert.Equal(Some 5, nextNumber (Some 4) None)
    Assert.Equal(Some 12, nextNumber (Some 11) (Some 12))
    Assert.Equal(None, nextNumber (Some 12) (Some 12))
    Assert.Equal(None, nextNumber None (Some 12))
    Assert.Equal(None, nextNumber None None)

[<Fact>]
let ``unfinished shows group by the UTC year of the last watch, newest year first`` () =
    let at (y: int) (m: int) = DateTimeOffset(y, m, 15, 0, 0, 0, TimeSpan.Zero)

    let groups =
        Progress.groupByYear
            [ entry 1 2 (Some 12) (at 2026 8)
              entry 2 2 (Some 12) (at 2024 3)
              entry 3 2 (Some 12) (at 2026 1)
              // 23:30 in UTC-5 on 31 December is already 2026 in UTC
              entry 4 2 (Some 12) (DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.FromHours -5.0))
              entry 5 2 (Some 12) (DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.Zero)) ]

    Assert.Equal<(int * int list) list>(
        [ 2026, [ 1; 3; 4 ]; 2025, [ 5 ]; 2024, [ 2 ] ],
        groups |> List.map (fun (y, es) -> y, es |> List.map _.MalId)
    )
