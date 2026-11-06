module FrunchyBackend.Tests.RecapRulesTests

open System
open Xunit
open FrunchyBackend.Infrastructure.Recap

// Feature 029: the streak rule, pure

let private today = DateOnly(2026, 11, 5)

let private run (first: DateOnly) (days: int) =
    { From = first
      To = first.AddDays(days - 1)
      Days = days }

[<Fact>]
let ``no runs: no current streak and no longest`` () =
    let s = ofRuns today []
    Assert.Equal(0, s.Current)
    Assert.Equal(None, s.Longest)

[<Fact>]
let ``a run ending today is the current streak`` () =
    let r = run (DateOnly(2026, 11, 3)) 3
    let s = ofRuns today [ r ]
    Assert.Equal(3, s.Current)
    Assert.Equal(Some r, s.Longest)

[<Fact>]
let ``a run ending yesterday is still alive`` () =
    let r = run (DateOnly(2026, 11, 2)) 3
    let s = ofRuns today [ r ]
    Assert.Equal(3, s.Current)
    Assert.Equal(Some r, s.Longest)

[<Fact>]
let ``a run ending two days ago is over: current 0, longest kept`` () =
    let r = run (DateOnly(2026, 11, 1)) 3
    let s = ofRuns today [ r ]
    Assert.Equal(0, s.Current)
    Assert.Equal(Some r, s.Longest)

[<Fact>]
let ``a tie for the longest goes to the latest run`` () =
    let older = run (DateOnly(2026, 10, 1)) 4
    let newer = run (DateOnly(2026, 10, 20)) 4
    Assert.Equal(Some newer, (ofRuns today [ older; newer ]).Longest)
    Assert.Equal(Some newer, (ofRuns today [ newer; older ]).Longest)

[<Fact>]
let ``the longest is never shorter than the current streak`` () =
    let long = run (DateOnly(2026, 9, 1)) 10
    let alive = run (DateOnly(2026, 11, 4)) 2
    let s = ofRuns today [ long; alive ]
    Assert.Equal(2, s.Current)
    Assert.Equal(Some long, s.Longest)

    for runs in [ []; [ alive ]; [ long; alive ]; [ run (DateOnly(2026, 11, 5)) 1 ] ] do
        let s = ofRuns today runs
        Assert.True(s.Current <= (s.Longest |> Option.map _.Days |> Option.defaultValue 0))
