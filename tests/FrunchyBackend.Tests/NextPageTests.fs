/// Feature 025: GET /next on real Postgres. Histories are synthetic: every user imports episodes `{series}E{n}`
/// (23m40s each, season `{series}S1`), so users share catalog episodes and a series' known length is what the
/// people who watched it brought. Tests read `data-*` hooks only.
module FrunchyBackend.Tests.NextPageTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private start = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Episodes 1..count of a series; the first `full` fully watched, the rest partly
let private series (id: string) (count: int) (full: int) =
    [ for n in 1..count ->
          syntheticEntry $"{id}E{n:D3}" id (start.AddMinutes(float n)) (n <= full) (if n <= full then 1400 else 300) ]

/// The base every match shares with the viewer: B00..B{n-1}, two fully watched episodes each
let private baseSeries (n: int) = [ for i in 0 .. n - 1 do yield! series $"BAS{i:D2}" 2 2 ]

let private importAs (app: TestApp) (browser: Browser) (account: string) (entries: string list) =
    task {
        let! key = connectExtension browser
        let! _ = submitAccepted app key (envelopeForProfile $"profile-{account}" [ historyUrl account, pageOf entries ])
        let! _ = processAll app.Db
        ()
    }

let private user (app: TestApp) (name: string) (entries: string list) =
    task {
        let! browser = signedUp app $"sub-{name}" name
        do! importAs app browser $"00000000-0000-4000-8000-{name.GetHashCode() &&& 0xffffff:x12}" entries
        return browser
    }

/// The data-* attributes of every element carrying data-{hook}, in document order
let private elements (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\bdata-{hook}(?![-\\w])(=\"[^\"]*\")?[^>]*>") ->
          [ for a in Regex.Matches(m.Value, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ]
          |> Map.ofList ]

let private attrs (hook: string) (name: string) (html: string) =
    elements hook html |> List.map (fun m -> m.TryFind name |> Option.defaultValue "")

let private next (browser: Browser) =
    task {
        let! page = browser.Get "/next"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        return page.Html
    }

let private seriesOnPage html = attrs "next-series" "next-series" html

// ---------------------------------------------------------------------------
// The world: me (the viewer), alice (visible by choice), bob (members), carol (private)
// ---------------------------------------------------------------------------

type private World =
    { App: TestApp
      Me: Browser
      Alice: Browser
      Bob: Browser
      Carol: Browser }

    interface IAsyncDisposable with
        member this.DisposeAsync() = (this.App :> IAsyncDisposable).DisposeAsync()

/// Default visibility `members`. me: 10 shared series and an unresolved episode of UNRES01.
/// alice: the base, ALI001 through (6 of 6), ALI002 at 60%, ALI003 at exactly 80%, and UNRES01 through.
/// bob: 6 of the base series, BOB001 through and ALI001 through. carol (private): the base and CAR001 through.
let private world () =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db [ defaultKey, "" ] ignore

        let! me = user app "mia" (baseSeries 10 @ [ unresolvedEntry "UNRES01E001" "UNRES01" (start.AddDays 1.0) ])
        let! alice = user app "alice" (baseSeries 10 @ series "ALI001" 6 6 @ series "ALI002" 5 3 @ series "ALI003" 5 4 @ series "UNRES01" 4 4)
        let! bob = user app "bob" (baseSeries 6 @ series "BOB001" 15 15 @ series "ALI001" 6 6)
        let! carol = user app "carol" (baseSeries 10 @ series "CAR001" 15 15)

        let! _ = setVisibility alice "public"
        let! _ = setVisibility carol "private"

        return { App = app; Me = me; Alice = alice; Bob = bob; Carol = carol }
    }

// ---------------------------------------------------------------------------
// Access and empty states
// ---------------------------------------------------------------------------

[<Fact>]
let ``signed out goes to sign in and back`` () =
    task {
        use! w = world ()
        use stranger = w.App.NewBrowser()
        use! response = stranger.GetRaw "/next"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/signin?returnUrl=%2Fnext", string response.Headers.Location)
    }

[<Fact>]
let ``a short history says so`` () =
    task {
        use! w = world ()
        let! newbie = user w.App "newbie" (series "NEW001" 5 5)
        let! html = next newbie
        Assert.Equal<string list>([ "not-enough" ], attrs "next-empty" "next-empty" html)
        Assert.Empty(elements "next-series" html)
    }

[<Fact>]
let ``nobody close enough gives the no-matches state`` () =
    task {
        use! app = newApp ()
        // 10 series for the viewer; the other shares only one of them: level 1
        let! me = user app "lone" (baseSeries 10)
        let! _ = user app "far" (baseSeries 1 @ [ for i in 0..8 do yield! series $"FAR{i:D3}" 2 2 ])
        let! html = next me
        Assert.Equal<string list>([ "no-matches" ], attrs "next-empty" "next-empty" html)
        Assert.Equal<string list>([ "0" ], attrs "next" "match-count" html)
    }

[<Fact>]
let ``matches who watched through nothing new give the no-series state`` () =
    task {
        use! app = newApp ()
        let! me = user app "twin1" (baseSeries 10)
        let! _ = user app "twin2" (baseSeries 10)
        let! html = next me
        Assert.Equal<string list>([ "no-series" ], attrs "next-empty" "next-empty" html)
        Assert.Equal<string list>([ "1" ], attrs "next" "match-count" html)
    }

// ---------------------------------------------------------------------------
// The recommendations
// ---------------------------------------------------------------------------

[<Fact>]
let ``public and members matches recommend what they watched through, ranked, never what the viewer started`` () =
    task {
        use! w = world ()
        let! html = next w.Me

        // alice (level 5) first: ALI001 (two finishers), ALI003 (80% counts), then bob's BOB001.
        // ALI002 (60%) isn't through, UNRES01 was started by the viewer (an unresolved episode), CAR001 is private
        Assert.Equal<string list>([ "ALI001"; "ALI003"; "BOB001" ], seriesOnPage html)
        Assert.Equal<string list>([ "2"; "1"; "1" ], attrs "next-series" "finisher-count" html)
        Assert.Equal<string list>([ "2" ], attrs "next" "match-count" html)

        // the first series names both finishers, best first, with their level
        let people = attrs "next-person" "next-person" html
        Assert.Equal<string list>([ "alice"; "bob"; "alice"; "bob" ], people)
        Assert.Equal<string list>([ "5"; "4"; "5"; "4" ], attrs "next-person" "level" html)
        Assert.Contains("href=\"/u/alice\"", html)

        // series link to the public series page, never into a history
        for id in [ "ALI001"; "ALI003"; "BOB001" ] do
            Assert.Contains($"href=\"/anime/{id}\"", html)

        Assert.DoesNotContain("/anime/ALI002\"", html)
        Assert.DoesNotContain("UNRES01", html)
        Assert.DoesNotContain("CAR001", html)
        Assert.DoesNotContain("/anime/UNRES01", html)
        Assert.DoesNotContain("/anime/BAS", html)
        Assert.DoesNotContain("/anime/CAR", html)
        Assert.DoesNotContain("/u/carol", html)
        Assert.Matches("href=\"/anime/[A-Z0-9]+\"[^>]*data-series-page-link", html)
        Assert.DoesNotMatch("/u/[a-z]+/anime/", html)
    }

[<Fact>]
let ``no score or percentage is printed, not even in an attribute`` () =
    task {
        use! w = world ()
        let! html = next w.Me
        Assert.DoesNotMatch("[0-9]+(\\.[0-9]+)?%", html)
        // no figure like 0.6 or 1.0 hides in any data attribute but the level, the counts and the MyAnimeList score
        for m in Regex.Matches(html, "data-([a-z-]+)=\"([^\"]*)\"") do
            let name = m.Groups[1].Value
            let value = m.Groups[2].Value

            if not (List.contains name [ "mal-score" ]) then
                Assert.DoesNotMatch("^[0-9]+\\.[0-9]+$", value)
    }

[<Fact>]
let ``a private match contributes nothing until the viewer holds the grant of its current link, and nothing after it is replaced`` () =
    task {
        use! w = world ()

        let! before = next w.Me
        Assert.DoesNotContain("CAR001", before)
        Assert.Equal<string list>([ "2" ], attrs "next" "match-count" before)

        let! linkPath, _ = createShareLink w.Carol
        let! opened = w.Me.Get linkPath
        Assert.Equal(HttpStatusCode.OK, opened.Status)

        let! granted = next w.Me
        Assert.Contains("CAR001", granted)
        Assert.Equal<string list>([ "3" ], attrs "next" "match-count" granted)
        Assert.Contains("carol", attrs "next-person" "next-person" granted)

        // a replaced link ends the grant
        let! _ = createShareLink w.Carol
        let! after = next w.Me
        Assert.DoesNotContain("CAR001", after)
        Assert.Equal<string list>([ "2" ], attrs "next" "match-count" after)

        // so does turning it off
        let! newLink, _ = createShareLink w.Carol
        let! _ = w.Me.Get newLink
        let! again = next w.Me
        Assert.Contains("CAR001", again)
        let! _ = revokeShareLink w.Carol
        let! gone = next w.Me
        Assert.DoesNotContain("CAR001", gone)
    }

[<Fact>]
let ``making a match private removes them and the series only they recommended`` () =
    task {
        use! w = world ()
        let! before = next w.Me
        Assert.Contains("BOB001", seriesOnPage before)

        let! _ = setVisibility w.Bob "private"
        let! after = next w.Me
        // bob's BOB001 is gone; ALI001 stays through alice, with one finisher
        Assert.Equal<string list>([ "ALI001"; "ALI003" ], seriesOnPage after)
        Assert.Equal<string list>([ "1"; "1" ], attrs "next-series" "finisher-count" after)
        Assert.DoesNotContain("href=\"/u/bob\"", after)
    }

[<Fact>]
let ``more than three finishers show three and then the rest as a count`` () =
    task {
        use! app = newApp ()
        let! me = user app "viewer" (baseSeries 10)

        for name in [ "fan1"; "fan2"; "fan3"; "fan4"; "fan5" ] do
            let! _ = user app name (baseSeries 10 @ series "HIT001" 4 4)
            ()

        let! html = next me
        Assert.Equal<string list>([ "HIT001" ], seriesOnPage html)
        Assert.Equal<string list>([ "5" ], attrs "next-series" "finisher-count" html)
        Assert.Equal(3, (attrs "next-person" "next-person" html).Length)
        Assert.Equal<string list>([ "2" ], attrs "next-more" "next-more" html)
    }

// ---------------------------------------------------------------------------
// The dashboard link and the language
// ---------------------------------------------------------------------------

[<Fact>]
let ``the dashboard links to the page for its owner only`` () =
    task {
        use! w = world ()
        let! own = w.Me.Get "/u/mia"
        Assert.Contains("href=\"/next\"", own.Html)
        Assert.Equal(1, (elements "next-link" own.Html).Length)

        let! visit = w.Alice.Get "/u/mia"
        Assert.Empty(elements "next-link" visit.Html)

        use stranger = w.App.NewBrowser()
        let! publicPage = stranger.Get "/u/alice"
        Assert.Empty(elements "next-link" publicPage.Html)
    }

[<Fact>]
let ``the page speaks Spanish`` () =
    task {
        use! w = world ()
        use es = w.App.NewBrowser "es"
        let! _ = signInWith es "sub-mia"
        let! page = es.Get "/next"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("Qué ver después", page.Html)
        Assert.Contains("Vista entera por", page.Html)
        Assert.Contains("Según 2 personas con gustos parecidos", page.Html)
        Assert.DoesNotContain("Watched through by", page.Html)
    }
