module FrunchyBackend.Tests.SeasonsUnitTests

open System
open Xunit
open FrunchyBackend.Infrastructure

// Feature 027: the airing season of an instant (the UTC quarter, named as the charts name seasons)

let private at (text: string) = DateTimeOffset.Parse(text, null, Globalization.DateTimeStyles.AssumeUniversal)

[<Theory>]
[<InlineData("2026-01-01T00:00:00Z", "winter", 2026, "2026-01-01T00:00:00Z")>]
[<InlineData("2026-03-31T23:59:59Z", "winter", 2026, "2026-01-01T00:00:00Z")>]
[<InlineData("2026-04-01T00:00:00Z", "spring", 2026, "2026-04-01T00:00:00Z")>]
[<InlineData("2026-08-15T12:00:00Z", "summer", 2026, "2026-07-01T00:00:00Z")>]
[<InlineData("2026-10-04T15:30:00Z", "fall", 2026, "2026-10-01T00:00:00Z")>]
[<InlineData("1999-10-20T00:00:00Z", "fall", 1999, "1999-10-01T00:00:00Z")>]
let ``ofInstant names the UTC quarter and starts it at 00:00 UTC`` (instant: string, name: string, year: int, start: string) =
    let n, y, s = Seasons.ofInstant (at instant)
    Assert.Equal(name, n)
    Assert.Equal(year, y)
    Assert.Equal(at start, s)

[<Fact>]
let ``ofInstant reads the instant in UTC, whatever its offset`` () =
    // 1 October 00:30 at +02:00 is still 30 September in UTC: summer
    let n, y, _ = Seasons.ofInstant (DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.FromHours 2.0))
    Assert.Equal(("summer", 2026), (n, y))

[<Fact>]
let ``the charts name seasons with the same function`` () =
    let instant = at "2026-05-20T08:00:00Z"
    Assert.Equal(Seasons.ofInstant instant, Charts.seasonOf instant)

[<Fact>]
let ``nameOfMonth covers every month`` () =
    let names = [ for m in 1..12 -> Seasons.nameOfMonth m ]

    Assert.Equal<string list>(
        [ "winter"; "winter"; "winter"; "spring"; "spring"; "spring"; "summer"; "summer"; "summer"; "fall"; "fall"; "fall" ],
        names
    )

[<Fact>]
let ``the key is name and year`` () =
    Assert.Equal("fall-2026", Seasons.key "fall" 2026)
    Assert.Equal("winter-1999", Seasons.key "winter" 1999)

[<Fact>]
let ``order puts later seasons after earlier ones across years`` () =
    let seasons =
        [ "fall", 2025; "winter", 2026; "fall", 2026; "spring", 2026; "summer", 2025 ]

    let sorted = seasons |> List.sortBy (fun (n, y) -> Seasons.order n y)

    Assert.Equal<(string * int) list>(
        [ "summer", 2025; "fall", 2025; "winter", 2026; "spring", 2026; "fall", 2026 ],
        sorted
    )

[<Fact>]
let ``ofOrder is the inverse of order`` () =
    for year in [ 1999; 2025; 2026 ] do
        for name in [ "winter"; "spring"; "summer"; "fall" ] do
            Assert.Equal((name, year), Seasons.ofOrder (Seasons.order name year))

[<Fact>]
let ``the named limits are the spec's`` () =
    Assert.Equal(100, Seasons.LongRunnerEpisodes)
    Assert.Equal(24, Seasons.LongRunMinimum)
    Assert.Equal(7, Seasons.WhileAiringDays)
