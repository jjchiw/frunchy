module FrunchyBackend.Tests.YearNavigationTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 005, User Story 2: reaching the year page and moving between years

let private now = DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)

let private appWithClock () =
    task {
        let! db = newDatabase ()
        let clock = TestClock(Now = now)
        return! createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)
    }

/// A user with history in 2023 and 2025 (20 fully watched episodes each)
let private withTwoYears () =
    task {
        let! app = appWithClock ()
        let! key = createUser app.Db "animegolden"

        let entries (year: int) =
            [ for i in 1..20 -> syntheticEntry $"N{year}{i:D3}" $"NS{year}{i:D3}" (DateTime(year, 5, 1, 12, 0, 0, DateTimeKind.Utc).AddHours(float i)) true 1400 ]

        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf (entries 2023 @ entries 2025) ])
        let! _ = processAll app.Db
        return app
    }

/// GET without following redirects
let private raw (app: TestApp) (path: string) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        let! response = app.Client.SendAsync request
        let! html = response.Content.ReadAsStringAsync()
        return response, WebUtility.HtmlDecode html
    }

let private location (response: HttpResponseMessage) =
    response.Headers.Location |> Option.ofObj |> Option.map (fun u -> if u.IsAbsoluteUri then u.PathAndQuery else string u)

[<Fact>]
let ``the dashboard and the DNA page link to the year page`` () =
    task {
        use! app = withTwoYears ()

        for path in [ "/u/animegolden"; "/u/animegolden/dna" ] do
            let! _, html = raw app path
            Assert.Matches("<a href=\"/u/animegolden/year\"[^>]*data-year-link", html)
    }

[<Fact>]
let ``the address without a year goes to the newest year with history`` () =
    task {
        use! app = withTwoYears ()
        let! response, _ = raw app "/u/animegolden/year"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal(Some "/u/animegolden/year/2025", location response)
    }

[<Fact>]
let ``a user without history goes to the current year, which shows the empty state`` () =
    task {
        use! app = appWithClock ()
        let! _ = createUser app.Db "newbie"
        let! response, _ = raw app "/u/newbie/year"
        Assert.Equal(Some "/u/newbie/year/2026", location response)

        let! page, html = raw app "/u/newbie/year/2026"
        Assert.Equal(HttpStatusCode.OK, page.StatusCode)
        Assert.Contains("data-year-empty=\"\" data-value=\"0\"", html)
        Assert.DoesNotContain("data-year-option", html)
    }

[<Fact>]
let ``a year without history goes back to the address without a year`` () =
    task {
        use! app = withTwoYears ()
        let! response, _ = raw app "/u/animegolden/year/2024"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal(Some "/u/animegolden/year", location response)
    }

[<Fact>]
let ``the picker lists exactly the years with history, newest first, marking the shown one`` () =
    task {
        use! app = withTwoYears ()
        let! _, html = raw app "/u/animegolden/year/2023"
        let options = [ for m in Regex.Matches(html, "<a [^>]*data-year-option=\"(\\d+)\"[^>]*>") -> m.Groups[1].Value, m.Value ]
        Assert.Equal<string list>([ "2025"; "2023" ], options |> List.map fst)

        for year, tag in options do
            Assert.Contains($"href=\"/u/animegolden/year/{year}\"", tag)
            Assert.Equal((year = "2023"), tag.Contains "aria-current=\"page\"")

        Assert.Matches("<a href=\"/u/animegolden\"[^>]*data-dashboard-link", html)
        Assert.Matches("<a href=\"/u/animegolden/dna\"[^>]*data-dna-link", html)
    }
