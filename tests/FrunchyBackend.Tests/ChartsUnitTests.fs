module FrunchyBackend.Tests.ChartsUnitTests

open System
open System.Collections.Generic
open Xunit
open Microsoft.Extensions.Configuration
open FrunchyBackend.Infrastructure.Charts

// Feature 012: the pure parts of the Charts module (research R1, R5, R6). No database.

let private at (text: string) = DateTimeOffset.Parse(text, null, Globalization.DateTimeStyles.AssumeUniversal)

// --- seasonOf ----------------------------------------------------------------

[<Theory>]
[<InlineData("2026-01-01T00:00:00Z", "winter", 2026, "2026-01-01T00:00:00Z")>]
[<InlineData("2026-02-14T10:00:00Z", "winter", 2026, "2026-01-01T00:00:00Z")>]
[<InlineData("2026-03-31T23:59:59Z", "winter", 2026, "2026-01-01T00:00:00Z")>]
[<InlineData("2026-04-01T00:00:00Z", "spring", 2026, "2026-04-01T00:00:00Z")>]
[<InlineData("2026-06-30T23:59:59Z", "spring", 2026, "2026-04-01T00:00:00Z")>]
[<InlineData("2026-07-01T00:00:00Z", "summer", 2026, "2026-07-01T00:00:00Z")>]
[<InlineData("2026-09-30T23:59:59Z", "summer", 2026, "2026-07-01T00:00:00Z")>]
[<InlineData("2026-10-01T00:00:00Z", "fall", 2026, "2026-10-01T00:00:00Z")>]
[<InlineData("2026-12-31T23:59:59Z", "fall", 2026, "2026-10-01T00:00:00Z")>]
let ``seasonOf names the UTC quarter and starts it at 00:00 UTC`` (now: string, name: string, year: int, start: string) =
    let n, y, s = seasonOf (at now)
    Assert.Equal(name, n)
    Assert.Equal(year, y)
    Assert.Equal(at start, s)

[<Fact>]
let ``seasonOf reads the instant in UTC, whatever its offset`` () =
    // 1 April 00:30 in +02:00 is still 31 March in UTC: winter
    let n, y, _ = seasonOf (DateTimeOffset(2026, 4, 1, 0, 30, 0, TimeSpan.FromHours 2.0))
    Assert.Equal(("winter", 2026), (n, y))
    // 31 December 23:30 at -05:00 is already 1 January UTC: winter of the next year
    let n, y, _ = seasonOf (DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours -5.0))
    Assert.Equal(("winter", 2027), (n, y))

// --- minPeople ---------------------------------------------------------------

[<Theory>]
[<InlineData(null, 3)>]
[<InlineData("", 3)>]
[<InlineData("abc", 3)>]
[<InlineData("0", 2)>]
[<InlineData("1", 2)>]
[<InlineData("-4", 2)>]
[<InlineData("2", 2)>]
[<InlineData("5", 5)>]
[<InlineData(" 7 ", 7)>]
let ``the minimum defaults to 3 and never goes below 2`` (raw: string, expected: int) =
    Assert.Equal(expected, parseMinPeople (Option.ofObj raw))

[<Fact>]
let ``the minimum is read from Frunchy:Charts:MinPeople`` () =
    let config (pairs: (string * string) list) =
        ConfigurationBuilder()
            .AddInMemoryCollection(pairs |> List.map (fun (k, v) -> KeyValuePair(k, v)))
            .Build()

    Assert.Equal(3, minPeople (config []))
    Assert.Equal(4, minPeople (config [ "Frunchy:Charts:MinPeople", "4" ]))
    Assert.Equal(2, minPeople (config [ "Frunchy:Charts:MinPeople", "1" ]))

// --- movement ----------------------------------------------------------------

[<Fact>]
let ``movement compares the previous rank with the current one`` () =
    let previous = Map [ "A", 1; "B", 2; "C", 5; "D", 3 ]
    Assert.Equal(Up 3, movement previous "C" 2)
    Assert.Equal(Down 2, movement previous "A" 3)
    Assert.Equal(Same, movement previous "B" 2)
    Assert.Equal(Up 1, movement previous "D" 2)

[<Fact>]
let ``a series missing from the previous ranks is new`` () =
    Assert.Equal(New, movement Map.empty "A" 1)
    Assert.Equal(New, movement (Map [ "B", 1 ]) "A" 2)

// --- trendBlocks -------------------------------------------------------------

[<Fact>]
let ``the 8 trend blocks are 7 days each, oldest first, the newest ending now`` () =
    let now = at "2026-10-16T12:00:00Z"
    let blocks = trendBlocks now

    Assert.Equal(8, blocks.Length)
    Assert.Equal((at "2026-10-09T12:00:00Z", now), List.last blocks)
    Assert.Equal((at "2026-08-21T12:00:00Z", at "2026-08-28T12:00:00Z"), List.head blocks)

    for (from, until) in blocks do
        Assert.Equal(TimeSpan.FromDays 7.0, until - from)

    // contiguous, no gap and no overlap
    for ((_, until), (from, _)) in List.pairwise blocks do
        Assert.Equal(until, from)
