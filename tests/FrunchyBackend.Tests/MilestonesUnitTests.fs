/// Feature 029 (T004): the pure rules of `Milestones.compute`, no Postgres.
module FrunchyBackend.Tests.MilestonesUnitTests

open System
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Milestones

let private minutes = 24L * 60_000L // 24 minutes, in ms

let private at (y: int) (m: int) (d: int) (h: int) (min: int) =
    DateTime(y, m, d, h, min, 0, DateTimeKind.Utc)

let private row (id: string) (series: string) (key: string) (watchedAt: DateTime) =
    { WatchedAt = watchedAt
      EpisodeId = id
      FullyWatched = true
      WatchedMs = minutes
      SeriesId = series
      SeriesTitle = $"Title {series}"
      DedupKey = key }

/// `n` fully watched episodes of one series, one a minute from `start`
let private run (series: string) (n: int) (start: DateTime) =
    [ for i in 0 .. n - 1 -> row $"{series}-{i:D4}" series $"{series}S1#{i}" (start.AddMinutes(float i)) ]

let private track (key: string) (tracks: Track list) = tracks |> List.find (fun t -> t.Key = key)

[<Fact>]
let ``no history gives six tracks with value 0 and no tier`` () =
    let result = compute []
    Assert.Equal<string list>([ "episodes"; "hours"; "series"; "streak"; "marathon"; "long-run" ], result |> List.map (fun t -> t.Key))

    for t in result do
        Assert.Equal(0m, t.Value)
        Assert.Equal(None, t.Tier)
        Assert.Equal(None, t.ReachedAt)
        Assert.Equal(None, t.Series)

    Assert.Equal(Some 10, (track "episodes" result).Next)
    Assert.Equal(0, reached result)
    Assert.Equal(36, totalTiers)

[<Fact>]
let ``episodes thresholds are exact and dated by the N-th episode`` () =
    let start = at 2025 1 1 10 0
    let nine = track "episodes" (compute (run "A" 9 start))
    Assert.Equal(9m, nine.Value)
    Assert.Equal(None, nine.Tier)
    Assert.Equal(Some 10, nine.Next)
    Assert.Equal(1m, nine.Remaining)

    let ten = track "episodes" (compute (run "A" 10 start))
    Assert.Equal(Some 10, ten.Tier)
    Assert.Equal(Some(start.AddMinutes 9.0), ten.ReachedAt)
    Assert.Equal(Some 50, ten.Next)

    let top = track "episodes" (compute (run "A" 5000 start))
    Assert.Equal(Some 5000, top.Tier)
    Assert.Equal(None, top.Next)
    Assert.Equal(0m, top.Remaining)

[<Fact>]
let ``input order does not matter and ties break by episode id`` () =
    let same = at 2025 3 3 12 0
    // ten episodes at the same instant: the 10th by episode id is dated the same, and the result is order-free
    let rows = [ for i in 0..9 -> row $"E{i}" "A" $"AS1#{i}" same ]
    Assert.Equal<Track list>(compute rows, compute (List.rev rows))

    // two series reach 5 on the same instant: the order of episode ids decides who is first
    let mixed =
        [ row "Z1" "S1" "k1" (at 2025 1 1 0 0)
          row "A1" "S2" "k2" (at 2025 1 1 0 0)
          row "M1" "S3" "k3" (at 2025 1 1 0 1) ]

    Assert.Equal<Track list>(compute mixed, compute (List.rev mixed))

[<Fact>]
let ``hours use exact milliseconds and count the playhead of partial watches`` () =
    let hour = 3_600_000L
    let almost = [ { row "E1" "A" "k1" (at 2025 1 1 0 0) with WatchedMs = 24L * hour - 36_000L } ] // 23.99 hours
    let before = track "hours" (compute almost)
    Assert.Equal(None, before.Tier)
    Assert.Equal(Some 24, before.Next)
    Assert.Equal(24.0m, before.Value) // 23.99 rounds to the dashboard's one decimal
    Assert.Equal(0.1m, before.Remaining) // never "0.0 to go"

    let exact = [ { row "E1" "A" "k1" (at 2025 1 1 0 0) with WatchedMs = 24L * hour } ]
    Assert.Equal(Some 24, (track "hours" (compute exact)).Tier)

    // a partial watch counts its playhead towards hours but not towards episodes
    let partial =
        [ { row "E1" "A" "k1" (at 2025 1 1 0 0) with FullyWatched = false; WatchedMs = 12L * hour }
          { row "E2" "A" "k2" (at 2025 1 2 0 0) with FullyWatched = true; WatchedMs = 12L * hour } ]

    let result = compute partial
    let hours = track "hours" result
    Assert.Equal(Some 24, hours.Tier)
    Assert.Equal(Some(at 2025 1 2 0 0), hours.ReachedAt) // the row whose running sum first reaches 24 hours
    Assert.Equal(1m, (track "episodes" result).Value)
    Assert.Equal(1m, (track "series" result).Value)

[<Fact>]
let ``series are the distinct series with a fully watched episode, dated by their first`` () =
    let rows =
        [ for i in 0..5 -> row $"E{i}" $"S{i % 5}" $"S{i % 5}S1#{i}" (at 2025 1 (i + 1) 9 0) ]
        @ [ { row "P1" "PARTIAL" "kp" (at 2025 1 20 0 0) with FullyWatched = false } ]

    let s = track "series" (compute rows)
    Assert.Equal(5m, s.Value) // 6 episodes, 5 series; the partial one adds none
    Assert.Equal(Some 5, s.Tier)
    Assert.Equal(Some(at 2025 1 5 9 0), s.ReachedAt)

[<Fact>]
let ``streak counts consecutive UTC days, across a gap and two runs`` () =
    let day d = row $"D{d}" "A" $"AS1#{d}" (at 2025 1 d 23 30)
    // run 1: days 1-3 (3 days), gap, run 2: days 10-16 (7 days)
    let rows = [ 1; 2; 3; 10; 11; 12; 13; 14; 15; 16 ] |> List.map day
    let s = track "streak" (compute rows)
    Assert.Equal(7m, s.Value)
    Assert.Equal(Some 7, s.Tier)
    Assert.Equal(Some 14, s.Next)
    Assert.Equal(Some(at 2025 1 16 23 30), s.ReachedAt) // the 7th day of the run

    // the date of the 3-day tier is the first run's third day, not the later run's
    let threeOnly = [ 1; 2; 3; 10; 11; 12 ] |> List.map day |> compute |> track "streak"
    Assert.Equal(Some 3, threeOnly.Tier)
    Assert.Equal(Some(at 2025 1 3 23 30), threeOnly.ReachedAt)

    // two watches on one day are one day; a gap of one day breaks the run
    let broken = [ day 1; day 2; day 4 ] |> compute |> track "streak"
    Assert.Equal(2m, broken.Value)
    Assert.Equal(None, broken.Tier)

[<Fact>]
let ``marathon counts a UTC day, so 23:30 and 00:30 are two days`` () =
    let night =
        [ for i in 0..2 -> row $"A{i}" "A" $"AS1#{i}" (at 2025 1 1 23 30) ]
        @ [ for i in 0..2 -> row $"B{i}" "A" $"AS1#{i + 3}" (at 2025 1 2 0 30) ]

    Assert.Equal(3m, (track "marathon" (compute night)).Value)

    // 6 on one day reaches the first tier, dated at that day's 6th; an earlier day with fewer doesn't count
    let day2 = [ for i in 0..5 -> row $"B{i}" "A" $"AS1#{i}" (at 2025 1 2 8 i) ]
    let day1 = [ for i in 0..4 -> row $"C{i}" "A" $"AS1#{i + 6}" (at 2025 1 1 8 i) ]
    let m = track "marathon" (compute (day1 @ day2))
    Assert.Equal(6m, m.Value)
    Assert.Equal(Some 6, m.Tier)
    Assert.Equal(Some(at 2025 1 2 8 5), m.ReachedAt)

[<Fact>]
let ``long-run counts a dub and its original once and names the series with the most`` () =
    // 24 episodes of series A, each watched in the original and in a dub (same dedup key): 24, not 48
    let original = [ for i in 0..23 -> row $"O{i:D2}" "A" $"AS1#{i}" (at 2025 1 1 8 i) ]
    let dub = [ for i in 0..23 -> row $"U{i:D2}" "A" $"AS1#{i}" (at 2025 1 2 8 i) ]
    let result = compute (original @ dub)
    let l = track "long-run" result
    Assert.Equal(24m, l.Value)
    Assert.Equal(Some 24, l.Tier)
    Assert.Equal(Some("A", "Title A"), l.Series)
    // each key is dated by its earliest watch: the 24th key is the original's 24th
    Assert.Equal(Some(at 2025 1 1 8 23), l.ReachedAt)
    // the episodes track does not dedupe (the dashboard's rule)
    Assert.Equal(48m, (track "episodes" result).Value)

[<Fact>]
let ``long-run names the series by most keys, then title, then id, and dates the earliest tier over all series`` () =
    // B and C both have 24 keys; B reaches 24 later than C. The title decides the name, the date is the earliest.
    let series id title start =
        [ for i in 0..23 -> { row $"{id}{i:D2}" id $"{id}S1#{i}" (start |> fun (s: DateTime) -> s.AddMinutes(float i)) with SeriesTitle = title } ]

    let b = series "B" "Alpha" (at 2025 2 1 0 0)
    let c = series "C" "Beta" (at 2025 1 1 0 0)
    let l = compute (b @ c) |> track "long-run"
    Assert.Equal(Some("B", "Alpha"), l.Series)
    Assert.Equal(Some(at 2025 1 1 0 23), l.ReachedAt)

    // same title: the id decides
    let d = series "D" "Same" (at 2025 1 1 0 0)
    let e = series "E" "Same" (at 2025 1 1 0 0)
    Assert.Equal(Some "D", (compute (e @ d) |> track "long-run").Series |> Option.map fst)

    // fewer keys loses even with an earlier title
    let few = [ for i in 0..4 -> { row $"F{i}" "F" $"FS1#{i}" (at 2025 1 1 0 i) with SeriesTitle = "Aaa" } ]
    Assert.Equal(Some "D", (compute (few @ d) |> track "long-run").Series |> Option.map fst)
