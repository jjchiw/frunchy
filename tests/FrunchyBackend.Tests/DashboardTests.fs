module FrunchyBackend.Tests.DashboardTests

open System
open System.Diagnostics
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private otherAccount = "11111111-2222-3333-4444-555555555555"

/// Invariant value of a stat card (data-stat="x" data-value="y")
let private stat (html: string) (name: string) =
    let m = Regex.Match(html, $"data-stat=\"{name}\"[^>]*data-value=\"([^\"]+)\"")
    if m.Success then m.Groups[1].Value else failwith $"stat {name} not found"

/// Displayed (culture-formatted) text of a stat card
let private statText (html: string) (name: string) =
    let m = Regex.Match(html, $"data-stat=\"{name}\"[^>]*>([^<]+)<")
    if m.Success then m.Groups[1].Value.Trim() else failwith $"stat text {name} not found"

let private recentItems (html: string) =
    Regex.Split(html, "data-recent-item") |> Array.skip 1 |> List.ofArray

let private dashboard (app: TestApp) (language: string) (username: string) =
    task {
        let! response = getWithLanguage app.Client language $"/u/{username}"
        let! html = readHtml response
        return response.StatusCode, html
    }

let private withHistory (json: string) =
    task {
        let! app = newApp ()
        let! key = createUser app.Db "animegolden"
        let! _ = submitAccepted app key json
        let! _ = processAll app.Db
        return app, key
    }

[<Fact>]
let ``2022 sample figures in English`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! code, html = dashboard app "en-US" "animegolden"
        Assert.Equal(HttpStatusCode.OK, code)
        Assert.Contains("lang=\"en\"", html)
        Assert.Equal("29", stat html "series")
        Assert.Equal("96", stat html "episodes")
        Assert.Equal("39.3", stat html "hours")
        Assert.Equal("1.6", stat html "days")
        Assert.Equal("39.3", statText html "hours")
    }

[<Fact>]
let ``Spanish dashboard uses Spanish text and number format`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! _, html = dashboard app "es-ES,es;q=0.9" "animegolden"
        Assert.Contains("lang=\"es\"", html)
        Assert.Equal("39,3", statText html "hours")
        Assert.Contains("Episodios vistos", html)
        Assert.Contains("En curso", html)
    }

[<Fact>]
let ``recent list is newest first and marks partial watches`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! _, html = dashboard app "en" "animegolden"
        let items = recentItems html
        Assert.Equal(6, items.Length)
        Assert.Contains("The Vampire Dies in No Time", items[0])
        Assert.Contains("Episode 2", items[0])
        Assert.Contains("In progress", items[0])

        let dates =
            [ for item in items -> Regex.Match(item, "datetime=\"([^\"]+)\"").Groups[1].Value |> DateTime.Parse ]

        Assert.Equal<DateTime list>(dates |> List.sortDescending, dates)
        Assert.Matches("datetime=\"2023-01-09T22:14:04Z\"", items[0])
        Assert.Contains("UTC", items[0])
        Assert.Contains("data-fr-time=\"relative\"", items[0])
    }

[<Fact>]
let ``2026 sample figures and newest episode`` () =
    task {
        let! app, _ = withHistory (load2026Sample ())
        use app = app
        let! _, html = dashboard app "en" "animegolden"
        Assert.Equal("67", stat html "series")
        Assert.Equal("298", stat html "episodes")
        Assert.Equal("118.2", stat html "hours")
        Assert.Equal("4.9", stat html "days")
        let first = (recentItems html)[0]
        Assert.Contains("Clevatess", first)
        Assert.Contains("Episode 13", first)
    }

[<Fact>]
let ``specials are labelled`` () =
    task {
        let special =
            (syntheticEntry "SP1" "SER1" (DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)) true 1400)
                .Replace("\"episode_number\":1", "\"episode_number\":null")

        let! app, _ = withHistory (envelopeOf [ historyUrl RealAccountId, pageOf [ special ] ])
        use app = app
        let! _, en = dashboard app "en" "animegolden"
        let! _, es = dashboard app "es" "animegolden"
        Assert.Contains("Special", (recentItems en)[0])
        Assert.Contains("Especial", (recentItems es)[0])
    }

[<Fact>]
let ``user without history sees the empty state and no numbers`` () =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "newbie"
        let! code, html = dashboard app "en" "newbie"
        Assert.Equal(HttpStatusCode.OK, code)
        Assert.DoesNotContain("data-stat", html)
        Assert.Contains("No history yet", html)
        Assert.Contains("This profile is public", html)
    }

[<Fact>]
let ``only unresolved history still shows the empty state`` () =
    task {
        let page = pageOf [ unresolvedEntry "U1" "SER1" (DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) ]
        let! app, _ = withHistory (envelopeOf [ historyUrl RealAccountId, page ])
        use app = app
        let! _, html = dashboard app "en" "animegolden"
        Assert.DoesNotContain("data-stat", html)
    }

[<Fact>]
let ``a pending import shows the sync-in-progress notice`` () =
    task {
        let! app, key = withHistory (load2022Envelope ())
        use app = app
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf [ syntheticEntry "N1" "SER9" DateTime.UtcNow true 1400 ] ])
        let! _, html = dashboard app "en" "animegolden"
        Assert.Contains("data-sync-in-progress", html)
        Assert.Equal("96", stat html "episodes") // figures processed so far
        let! _ = processAll app.Db
        let! _, after = dashboard app "en" "animegolden"
        Assert.DoesNotContain("data-sync-in-progress", after)
    }

[<Fact>]
let ``each dashboard shows only its own user`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! keyB = createUser app.Db "userb"
        let! _ = submitAccepted app keyB (envelopeOf (syntheticPages otherAccount 1 10))
        let! _ = processAll app.Db
        let! _, a = dashboard app "en" "animegolden"
        let! _, b = dashboard app "en" "userb"
        Assert.Equal("96", stat a "episodes")
        Assert.Equal("10", stat b "episodes")
        Assert.Contains("USERB", b)
        Assert.DoesNotContain("The Vampire Dies in No Time", b)
    }

[<Fact>]
let ``dashboard never exposes keys, import ids or issues`` () =
    task {
        let! app, key = withHistory (load2022Envelope ())
        use app = app
        let! importId = Db.scalar<Guid> app.Db "SELECT id FROM cr_imports LIMIT 1" []
        let! _, html = dashboard app "en" "animegolden"
        Assert.DoesNotContain(key, html)
        Assert.DoesNotContain(string importId, html)
        Assert.DoesNotContain("panel missing", html)
        Assert.DoesNotContain("G31UXDEJN", html) // the unresolved entry
    }

[<Theory>]
[<InlineData("en", "User not found", "lang=\"en\"")>]
[<InlineData("es", "Usuario no encontrado", "lang=\"es\"")>]
[<InlineData("fr-FR", "User not found", "lang=\"en\"")>]
let ``unknown username is a localized 404`` (language: string, expected: string, lang: string) =
    task {
        use! app = newApp ()
        let! code, html = dashboard app language "nobody"
        Assert.Equal(HttpStatusCode.NotFound, code)
        Assert.Contains(expected, html)
        Assert.Contains(lang, html)
    }

[<Fact>]
let ``username lookup is case-insensitive`` () =
    task {
        let! app, _ = withHistory (load2022Envelope ())
        use app = app
        let! code, html = dashboard app "en" "AnimeGolden"
        Assert.Equal(HttpStatusCode.OK, code)
        Assert.Equal("96", stat html "episodes")
    }

[<Fact>]
let ``dashboard with 10,000 watch events responds in under 2 seconds`` () =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "bigfan"

        do!
            Db.execute
                app.Db
                """
                INSERT INTO cr_episodes (id, series_id, series_title, title, episode_number, duration_ms, thumbnail_url, source_at)
                SELECT 'E' || g, 'S' || (g / 25), 'Series ' || (g / 25), 'Episode ' || g, g % 25 + 1, 1420000, 'https://img.example/' || g || '.jpg', now()
                FROM generate_series(1, 10000) g;
                INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
                SELECT (SELECT id FROM users WHERE username = 'bigfan'), 'E' || g, now() - (g || ' minutes')::interval, 1420, true, gen_random_uuid()
                FROM generate_series(1, 10000) g;
                """

        let! _ = dashboard app "en" "bigfan" // warm-up
        let watch = Stopwatch.StartNew()
        let! code, html = dashboard app "en" "bigfan"
        watch.Stop()
        Assert.Equal(HttpStatusCode.OK, code)
        Assert.Equal("10000", stat html "episodes")
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 2.0, $"took {watch.Elapsed}")
    }
