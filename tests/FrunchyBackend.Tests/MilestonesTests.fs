/// Feature 029 (T005): the milestones section of the dashboard, end to end on Postgres.
module FrunchyBackend.Tests.MilestonesTests

open System
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private withHistory (json: string) =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key json
        let! _ = processAll app.Db
        return app, key
    }

let private page (app: TestApp) (language: string) (path: string) =
    task {
        let! response = getWithLanguage app.Client language path
        let! html = readHtml response
        return response.StatusCode, html
    }

/// The milestones section, or None
let private section (html: string) =
    let m = Regex.Match(html, "(?s)<section[^>]*data-milestones=\"\".*?</section>")
    if m.Success then Some m.Value else None

/// The card of one track
let private card (html: string) (key: string) =
    let m = Regex.Match(html, $"(?s)<article[^>]*data-milestone=\"{key}\".*?</article>")
    if m.Success then m.Value else failwith $"card {key} not found"

let private attr (element: string) (name: string) =
    let m = Regex.Match(element, $"data-{name}=\"([^\"]*)\"")
    if m.Success then m.Groups[1].Value else failwith $"data-{name} not found"

let private stat (html: string) (name: string) =
    let m = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"")
    if m.Success then m.Groups[1].Value else failwith $"stat {name} not found"

let private dashboard (app: TestApp) = page app "en" "/u/animegolden"

[<Fact>]
let ``the 2022 sample shows six cards in order, equal to the dashboard figures`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! code, html = dashboard app
        Assert.Equal(HttpStatusCode.OK, code)
        let section = (section html).Value

        let keys = [ for m in Regex.Matches(section, "<article[^>]*data-milestone=\"([^\"]+)\"") -> m.Groups[1].Value ]
        Assert.Equal<string list>([ "episodes"; "hours"; "series"; "streak"; "marathon"; "long-run" ], keys)

        // a badge never disagrees with the tile above it
        for name in [ "episodes"; "hours"; "series" ] do
            Assert.Equal(stat html name, attr (card html name) "value")

        Assert.Equal("36", attr section "milestones-total")

        // the section sits between the figure tiles and the recent list
        let tiles = html.IndexOf "data-stat=\"days\""
        let milestones = html.IndexOf "data-milestones=\"\""
        let recent = html.IndexOf "data-recent-item"
        Assert.True(tiles < milestones && milestones < recent)
    }

[<Fact>]
let ``the 2022 sample reaches the expected tiers`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! _, html = dashboard app
        let tier key = attr (card html key) "tier", attr (card html key) "next"

        // 96 episodes, 39.3 hours, 29 series
        Assert.Equal(("50", "100"), tier "episodes")
        Assert.Equal(("24", "100"), tier "hours")
        Assert.Equal(("25", "50"), tier "series")
        Assert.Contains("50 episodes", card html "episodes")
        Assert.Contains("4 to go to 100 episodes", card html "episodes")
        Assert.Contains("data-milestone-reached", card html "episodes")
        Assert.Contains("data-milestone-progress", card html "episodes")
        Assert.DoesNotContain("data-milestone-top", card html "episodes")
        Assert.Equal(("14", "30"), tier "streak")
        Assert.Equal(("12", "24"), tier "marathon")
        Assert.Equal(("24", "50"), tier "long-run")
        Assert.Equal("16", attr (card html "streak") "value")
        Assert.Equal("22", attr (card html "marathon") "value")
        Assert.Equal("31", attr (card html "long-run") "value")
        Assert.Contains("31 episodes of", card html "long-run")
        Assert.Contains("Bungo Stray Dogs", card html "long-run")
        Assert.Equal("12", attr (section html |> Option.get) "milestones-reached")
        Assert.Contains("12 of 36 reached", section html |> Option.get)
        // the reached date of a tier is a localized time element
        Assert.Matches("<time datetime=\"2022-12-30T21:01:24Z\" data-fr-time=\"date\" data-milestone-reached", card html "episodes")
    }

[<Fact>]
let ``long-run names the most watched series and links it`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! _, html = dashboard app
        let longRun = card html "long-run"
        let seriesId = attr longRun "long-run-series"
        Assert.Matches("^[A-Z0-9]{6,20}$", seriesId)
        Assert.Contains($"data-series-link=\"{seriesId}\"", longRun)
        Assert.Contains($"href=\"/u/animegolden/anime/{seriesId}\"", longRun)
    }

[<Fact>]
let ``the section is in Spanish with Spanish number format`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! _, html = page app "es-ES,es;q=0.9" "/u/animegolden"
        let section = (section html).Value
        Assert.Contains("Hitos", section)
        Assert.Contains("conseguidos", section)
        Assert.Contains("50 episodios", card html "episodes")
        Assert.Contains("Faltan 4 para 100 episodios", card html "episodes")
        Assert.Contains("Racha", section)
        Assert.DoesNotContain("Milestones", section)
    }

[<Fact>]
let ``an empty history shows no section`` () =
    task {
        let! app = newApp ()
        use app = app
        let! _ = createUser app.Db "animegolden"
        let! code, html = dashboard app
        Assert.Equal(HttpStatusCode.OK, code)
        Assert.True((section html).IsNone)
        Assert.DoesNotContain("data-milestone", html)
    }

[<Fact>]
let ``the section is identical after re-sending, rebuilding and reordering the pages`` () =
    task {
        let! app, key = withHistory (load2026Sample ())
        use app = app
        let! _, before = dashboard app
        let reference = (section before).Value
        Assert.Contains("data-milestone=\"episodes\"", reference)

        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! _, resent = dashboard app
        Assert.Equal(reference, (section resent).Value)

        let! queued = RebuildUser.rebuildUser app.Db "animegolden"
        Assert.True(queued.IsSome)
        let! _ = processAll app.Db
        let! _, rebuilt = dashboard app
        Assert.Equal(reference, (section rebuilt).Value)

        // the same pages in the opposite order, into a fresh database
        let envelope = JsonNode.Parse(load2026Sample ())
        let pages = envelope["pages"].AsArray()
        let reversed = JsonArray()

        for node in pages |> Seq.rev |> Seq.map (fun n -> n.DeepClone()) |> Array.ofSeq do
            reversed.Add node

        envelope["pages"] <- reversed
        let! other, _ = withHistory (envelope.ToJsonString())
        use other = other
        let! _, reordered = dashboard other
        Assert.Equal(reference, (section reordered).Value)
    }

[<Fact>]
let ``the dashboard of a private profile is the unknown-user 404 and has no milestones`` () =
    task {
        let! db = newDatabase ()
        let! app = createAppWith db [ "Frunchy:Profiles:DefaultVisibility", "private" ] ignore
        use app = app
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db
        let! code, html = dashboard app
        let! unknownCode, unknown = page app "en" "/u/nobodyhere"
        Assert.Equal(HttpStatusCode.NotFound, code)
        Assert.Equal(unknownCode, code)
        Assert.DoesNotContain("data-milestone", html)

        let strip (s: string) = Regex.Replace(s, "(__RequestVerificationToken\"[^>]*value=\")[^\"]*(\")|(/language/(en|es)\\?returnUrl=)[^\"]*", "")
        Assert.Equal(strip unknown, strip html)
    }
