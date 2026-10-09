module FrunchyBackend.Tests.DnaPaceTests

open System
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 004: viewing pace over the last 90 days (FR-006, research R8), on a fixed clock

let private now = DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)

/// A user whose history has `recent` fully watched episodes, placed by `daysAgo`, plus 20 older than
/// 90 days (so the page passes its 15-episode minimum without touching the window)
let private pacePage (recent: int list) =
    task {
        let clock = TestClock(Now = now)
        let! db = newDatabase ()
        let! app = createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
        let! key = createUser app.Db "animenerd"

        // each entry in its own series: one episode per series never merges as a dub
        let entry (i: int) (daysAgo: float) =
            syntheticEntry $"P{i:D3}" $"PS{i:D3}" (now.UtcDateTime.AddDays(-daysAgo)) true 1400

        let old = [ for i in 1..20 -> entry (900 + i) (100.0 + float i) ]
        let window = recent |> List.mapi (fun i daysAgo -> entry i (float daysAgo + 0.5))
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (old @ window) ])
        let! _ = processAll app.Db

        let! response = getWithLanguage app.Client "en" "/u/animenerd/dna"
        let! html = readHtml response
        do! (app :> IAsyncDisposable).DisposeAsync()
        return Regex.Match(html, "<section[^>]*data-dna-pace=\"\"[^>]*>").Value
    }

let private attr (name: string) (tag: string) =
    Regex.Match(tag, $"data-{name}=\"([^\"]*)\"").Groups[1].Value

/// `count` episodes spread over the first `weeks` weeks of the window (week w = days 7w..7w+6 ago)
let private spread (count: int) (weeks: int) =
    [ for i in 0 .. count - 1 -> (i % weeks) * 7 + (i / weeks) % 7 ]

[<Fact>]
let ``active in 9 of 13 weeks is steady`` () =
    task {
        let! pace = pacePage (spread 30 9)
        Assert.Equal("steady", attr "label" pace)
        Assert.Equal("9", attr "weeks" pace)
        Assert.Equal("30", attr "episodes" pace)
        Assert.Equal("0.3", attr "value" pace) // 30 ÷ 90 = 0.33
    }

[<Fact>]
let ``active in 8 of 13 weeks is in bursts`` () =
    task {
        let! pace = pacePage (spread 30 8)
        Assert.Equal("bursts", attr "label" pace)
        Assert.Equal("8", attr "weeks" pace)
    }

[<Fact>]
let ``fewer than 10 episodes in the window is not enough recent data`` () =
    task {
        let! pace = pacePage (spread 9 9)
        Assert.Contains("data-not-enough", pace)
        Assert.Equal("", attr "value" pace)
    }

[<Fact>]
let ``only the last 90 days count, and the rate is episodes over 90 days`` () =
    task {
        // 45 episodes inside the window, the 20 older ones outside: 45 ÷ 90 = 0.5
        let! pace = pacePage (spread 45 13)
        Assert.Equal("45", attr "episodes" pace)
        Assert.Equal("0.5", attr "value" pace)
        Assert.Equal("13", attr "weeks" pace)
    }
