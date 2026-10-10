/// The MyAnimeList review pages as an operator uses them (feature 007, US1 and US2): the list of
/// seasons nobody has decided, the actions, the manual decisions and the way back to automatic.
/// Uses the 2026 sample, matched against the scripted catalog; Slime's "OVA Season 1" is set ambiguous.
module FrunchyBackend.Tests.MalReviewTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.MalReviewAccessTests
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.FakeMal
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private tamerSeason = "GRK5CNJ14"
let private tamerDub = "GYDQCGK5Q"
let private statuses = [ "ambiguous"; "no_match"; "failed" ]

let private operatorEnv () = newReviewEnv (operatorsOf [ "boss" ]) true

let private seasonsIn (html: string) =
    [ for m in Regex.Matches(html, "data-review-season=\"([^\"]+)\"") -> m.Groups[1].Value ]

/// The Slime row's HTML (up to the next row)
let private rowOf (html: string) (season: string) =
    let start = html.IndexOf $"data-review-season=\"{season}\""
    Assert.True(start >= 0, $"no row for {season}")
    let next = html.IndexOf("data-review-season=\"", start + 10)
    if next < 0 then html.Substring start else html.Substring(start, next - start)

let private post (env: ReviewEnv) path fields = postWithToken env.Boss path fields

let private location (response: HttpResponseMessage) = string response.Headers.Location

/// POST, check the redirect carries `msg`, and follow it to the page showing the notice
let private act (env: ReviewEnv) path fields (msg: string) (page: string) =
    task {
        let! response = post env path fields
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)
        Assert.StartsWith(page, location response)
        Assert.Contains($"msg={msg}", location response)
        let! followed = env.Boss.Follow response
        Assert.Equal(HttpStatusCode.OK, followed.Status)
        Assert.Contains($"data-review-notice=\"{msg}\"", followed.Html)
        return followed
    }

let private match' (env: ReviewEnv) (season: string) = matchOf env.App.Db season

let private expectMatch (env: ReviewEnv) (season: string) (expected: string) =
    task {
        let! row = match' env season
        Assert.Equal(expected, row)
    }

// ---------------------------------------------------------------------------------------------
// The list
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``the list shows the open seasons in the CLI's order, the rest as a count`` () =
    task {
        use! env = operatorEnv ()
        let! cli = MalList.list env.App.Db None 500
        let expected = cli |> List.filter (fun r -> List.contains r.Status statuses) |> List.map _.SeasonId
        Assert.True(expected.Length > 50, "the sample has more open seasons than a page")

        let! page = env.Boss.Get "/review/mal"
        Assert.Equal(HttpStatusCode.OK, page.Status)
        Assert.Contains("data-review=\"open\"", page.Html)
        Assert.Equal<string list>(List.truncate 50 expected, seasonsIn page.Html)
        Assert.Matches($"data-review-more=\"{expected.Length - 50}\"", page.Html)
        Assert.DoesNotContain("data-review-empty", page.Html)

        // a matched season is not in the list
        Assert.DoesNotContain($"data-review-season=\"{tamerSeason}\"", page.Html)
    }

[<Fact>]
let ``the status filter narrows the list, and an empty filter says so`` () =
    task {
        use! env = operatorEnv ()
        let! ambiguous = env.Boss.Get "/review/mal?status=ambiguous"
        Assert.Equal<string list>([ slimeSeason ], seasonsIn ambiguous.Html)
        Assert.Contains("data-status=\"ambiguous\"", ambiguous.Html)
        Assert.DoesNotContain("data-review-more", ambiguous.Html)

        let! noMatch = env.Boss.Get "/review/mal?status=no_match"
        Assert.NotEmpty(seasonsIn noMatch.Html)
        Assert.DoesNotContain("data-status=\"ambiguous\"", noMatch.Html)

        let! failed = env.Boss.Get "/review/mal?status=failed"
        Assert.Empty(seasonsIn failed.Html)
        Assert.Contains("data-review-empty", failed.Html)

        // the filter links carry their status; the shown one is current
        let! all = env.Boss.Get "/review/mal"

        for filter in [ "all"; "ambiguous"; "no_match"; "failed" ] do
            Assert.Contains($"data-review-filter=\"{filter}\"", all.Html)

        // anything else is the default
        let! junk = env.Boss.Get "/review/mal?status=matched"
        Assert.Equal<string list>(seasonsIn all.Html, seasonsIn junk.Html)
    }

[<Fact>]
let ``a row shows its users, fully watched episodes, watcher link and candidates`` () =
    task {
        use! env = operatorEnv ()
        let! fully =
            Db.scalar<int64>
                env.App.Db
                "SELECT count(*) FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.season_id = @s AND w.fully_watched AND e.duration_ms IS NOT NULL"
                [ "s", box slimeSeason ]

        let! page = env.Boss.Get "/review/mal?status=ambiguous"
        let row = rowOf page.Html slimeSeason
        Assert.Contains("data-users=\"1\"", row)
        Assert.Contains($"data-fully=\"{fully}\"", row)
        Assert.Contains($"data-series-link=\"{slimeSeries}\"", row)
        Assert.Contains($"href=\"/u/animenerd/anime/{slimeSeries}\"", row)
        Assert.Contains("data-candidate=\"37430\"", row)
        Assert.Contains("data-candidate=\"59000\"", row)
        Assert.Contains("That Time I Got Reincarnated as a Slime", row)

        // a candidate's "Use this" form posts its id; the row also has the id field, retry and none
        Assert.Matches("name=\"mal\"[^>]*value=\"37430\"", row)
        for action in [ "set"; "retry"; "none" ] do
            Assert.Contains($"data-action=\"{action}\"", row)

        // a second user watching it makes two users, and the most recent watcher gets the link
        let! _ = createUser env.App.Db "zed"
        let! _ =
            Db.scalar<int>
                env.App.Db
                """INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
                   SELECT (SELECT id FROM users WHERE username = 'zed'), w.cr_episode_id, now() + interval '1 day', w.playhead_s, true, w.import_id
                   FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.season_id = @s
                   RETURNING 1"""
                [ "s", box slimeSeason ]

        let! again = env.Boss.Get "/review/mal?status=ambiguous"
        let row = rowOf again.Html slimeSeason
        Assert.Contains("data-users=\"2\"", row)
        Assert.Contains($"href=\"/u/zed/anime/{slimeSeries}\"", row)
    }

// ---------------------------------------------------------------------------------------------
// The actions
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``picking a candidate matches the season by hand, as the CLI does`` () =
    task {
        use! env = operatorEnv ()
        let! page = act env "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ] "set" "/review/mal"
        Assert.DoesNotContain($"data-review-season=\"{slimeSeason}\"", page.Html) // decided: out of the list
        do! expectMatch env slimeSeason ($"{slimeSeason} matched 37430 manual -")
        Assert.Empty env.App.Mal.Requests // the anime was held already
    }

[<Fact>]
let ``an id or a MyAnimeList link can be typed; a new anime is fetched once`` () =
    task {
        use! env = operatorEnv ()
        routeFull env.App.Mal (spec 77777 "Another Tamer" None "TV" "2024-01-01")
        // The Weakest Tamer is open again, its dub still pointing at it
        let! _ = Db.scalar<int> env.App.Db $"UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, reason = 'no-title-match' WHERE season_id = '{tamerSeason}' RETURNING 1" []
        let! _ = Db.scalar<int> env.App.Db $"UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL WHERE season_id = '{tamerDub}' RETURNING 1" []

        let! _ = act env "/review/mal/set" [ "season", tamerSeason; "mal", "77777" ] "set" "/review/mal"
        Assert.Equal(1, env.App.Mal.Count "details:77777")
        do! expectMatch env tamerSeason ($"{tamerSeason} matched 77777 manual -")
        do! expectMatch env tamerDub $"{tamerDub} matched 77777 dub {tamerSeason}" // the dub follows

        // a link, with or without scheme and www, and a trailing slug
        for typed in [ "https://myanimelist.net/anime/15/Eyeshield_21"; "myanimelist.net/anime/15"; "http://www.myanimelist.net/anime/15/" ] do
            let! _ = Db.scalar<int> env.App.Db "UPDATE cr_season_matches SET status = 'failed', mal_id = NULL, method = NULL WHERE season_id = 'GR19CPDWM' RETURNING 1" []
            let! _ = act env "/review/mal/set" [ "season", "GR19CPDWM"; "mal", typed ] "set" "/review/mal"
            do! expectMatch env "GR19CPDWM" ("GR19CPDWM matched 15 manual -")
    }

[<Fact>]
let ``marking a season as not on MyAnimeList is manual and final`` () =
    task {
        use! env = operatorEnv ()
        let! page = act env "/review/mal/none" [ "season", "GR19CPDWM" ] "none" "/review/mal"
        Assert.DoesNotContain("data-review-season=\"GR19CPDWM\"", page.Html)
        do! expectMatch env "GR19CPDWM" ("GR19CPDWM no_match - manual -")
        let! reason = Db.scalar<string> env.App.Db "SELECT reason FROM cr_season_matches WHERE season_id = 'GR19CPDWM'" []
        Assert.Equal("manual-none", reason)
    }

[<Fact>]
let ``asking for a retry queues the season at the front and drops the stored searches`` () =
    task {
        use! env = operatorEnv ()
        let! searches = Db.scalar<int64> env.App.Db "SELECT count(*) FROM mal_searches WHERE query LIKE 'Shikimori%'" []
        Assert.True(searches > 0L, "the matcher searched for this series")

        let! page = act env "/review/mal/retry" [ "season", "GRDQCG2KK" ] "retry" "/review/mal"
        Assert.DoesNotContain("data-review-season=\"GRDQCG2KK\"", page.Html) // pending is not open
        do! expectMatch env "GRDQCG2KK" ("GRDQCG2KK pending - - -")
        let! queued = Db.scalar<int64> env.App.Db "SELECT count(*) FROM cr_season_matches WHERE season_id = 'GRDQCG2KK' AND requested_at IS NOT NULL AND attempts = 0" []
        Assert.Equal(1L, queued)
        let! after = Db.scalar<int64> env.App.Db "SELECT count(*) FROM mal_searches WHERE query LIKE 'Shikimori%'" []
        Assert.Equal(0L, after)
    }

[<Fact>]
let ``the web and the CLI leave the same rows`` () =
    task {
        use! web = operatorEnv ()
        use! cli = operatorEnv ()
        let env: Program.CliEnv =
            { Db = cli.App.Db
              Http = httpFactory cli.App
              Mal = MalClient.readSettings (Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(cli.App.Services))
              Clock = TimeProvider.System }

        let! _ = post web "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ]
        let! _ = post web "/review/mal/none" [ "season", "GR19CPDWM" ]
        let! _ = post web "/review/mal/retry" [ "season", "GRDQCG2KK" ]
        let! _ = Program.cliCommands["mal-set"] env [ slimeSeason; "37430" ]
        let! _ = Program.cliCommands["mal-none"] env [ "GR19CPDWM" ]
        let! _ = Program.cliCommands["mal-retry"] env [ "GRDQCG2KK" ]
        let! a = matches web.App.Db
        let! b = matches cli.App.Db
        Assert.Equal<string list>(b, a)
    }

// ---------------------------------------------------------------------------------------------
// Refusals: a notice, and nothing changes
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``bad input is refused before any request`` () =
    task {
        use! env = operatorEnv ()
        let! before = reviewSnapshot env.App

        for typed in [ "abc"; "0"; "-5"; "12345678901"; "https://evil.example/anime/15"; "https://myanimelist.net/manga/15"; "1.5"; "javascript:alert(1)"; "" ] do
            let! page = act env "/review/mal/set" [ "season", slimeSeason; "mal", typed ] "bad-input" "/review/mal"
            Assert.Contains("data-review-notice=\"bad-input\"", page.Html)

        // (the limit is 30 writes a minute, so keep the rounds few)
        for season in [ ""; "abc"; "../etc"; "A1B2C3D4E5F6G7H8I9J0K1" ] do
            for path, fields in postActions season do
                let! response = post env path fields
                Assert.Equal(HttpStatusCode.Found, response.StatusCode)
                Assert.Contains("msg=bad-input", location response)
                Assert.DoesNotContain("season=", location response) // never echoed

        Assert.Empty env.App.Mal.Requests
        let! after = reviewSnapshot env.App
        Assert.Equal(before, after)
    }

[<Fact>]
let ``an unknown season, an unknown anime and a stale form change nothing`` () =
    task {
        use! env = operatorEnv ()
        let! before = reviewSnapshot env.App

        let! _ = act env "/review/mal/none" [ "season", "NOPE0000" ] "unknown-season" "/review/mal"
        let! _ = act env "/review/mal/retry" [ "season", "NOPE0000" ] "unknown-season" "/review/mal"
        let! _ = act env "/review/mal/auto" [ "season", "NOPE0000" ] "unknown-season" "/review/mal/manual"

        // MyAnimeList has no such anime (404): the season keeps its state
        env.App.Mal.Route("details:99999", [ Status 404 ])
        let! _ = act env "/review/mal/set" [ "season", slimeSeason; "mal", "99999" ] "unknown-anime" "/review/mal"

        // decided in the meantime: a matched season, and a manual one, are not open
        let! _ = act env "/review/mal/none" [ "season", tamerSeason ] "not-open" "/review/mal"
        let! _ = act env "/review/mal/retry" [ "season", tamerSeason ] "not-open" "/review/mal"
        let! _ = act env "/review/mal/set" [ "season", tamerSeason; "mal", "15" ] "not-open" "/review/mal"
        let! _ = act env "/review/mal/auto" [ "season", tamerSeason ] "not-open" "/review/mal/manual"

        let! after = reviewSnapshot env.App
        Assert.Equal(before, after)
        let! events = Db.scalar<int64> env.App.Db "SELECT count(*) FROM security_events WHERE event LIKE 'mal_%'" []
        Assert.Equal(0L, events)
    }

[<Fact>]
let ``a form that was open on a season decided meanwhile is refused`` () =
    task {
        use! env = operatorEnv ()
        let! _ = act env "/review/mal/none" [ "season", slimeSeason ] "none" "/review/mal"
        // the same form again, from a stale page
        let! _ = act env "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ] "not-open" "/review/mal"
        do! expectMatch env slimeSeason ($"{slimeSeason} no_match - manual -")
    }

[<Fact>]
let ``without a MyAnimeList client id the action says it is unavailable and changes nothing`` () =
    task {
        use! env = operatorEnv ()
        let config = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(env.App.Services)
        config["Frunchy:Mal:ClientId"] <- ""
        let! before = reviewSnapshot env.App
        let! page = act env "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ] "unavailable" "/review/mal"
        Assert.Contains($"data-review-season=\"{slimeSeason}\"", page.Html) // still there to fix later
        Assert.Empty env.App.Mal.Requests
        let! after = reviewSnapshot env.App
        Assert.Equal(before, after)
    }

// ---------------------------------------------------------------------------------------------
// Languages
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``the pages and notices follow the browser's language`` () =
    task {
        use! env = operatorEnv ()
        let! en = env.Boss.Get "/review/mal?msg=set&season=GR8D0NQ7R"
        Assert.Contains("lang=\"en\"", en.Html)
        Assert.Contains("Review MyAnimeList matches", en.Html)
        Assert.Contains("Use this", en.Html)
        Assert.Contains("Match saved.", en.Html)

        let es = env.App.NewBrowser "es"
        let! _ = signInWith es "sub-boss"
        let! page = es.Get "/review/mal?msg=not-open"
        Assert.Contains("lang=\"es\"", page.Html)
        Assert.Contains("Revisar coincidencias de MyAnimeList", page.Html)
        Assert.Contains("Usar esta", page.Html)
        Assert.Contains("Esa temporada se decidió mientras tanto", page.Html)
        Assert.DoesNotContain("Use this", page.Html)

        let! manual = es.Get "/review/mal/manual"
        Assert.Contains("Decisiones manuales de MyAnimeList", manual.Html)
        let! unknown = es.Get "/review/mal?msg=nonsense"
        Assert.DoesNotContain("data-review-notice", unknown.Html) // only known keys
    }

// ---------------------------------------------------------------------------------------------
// Manual decisions and the way back (US2)
// ---------------------------------------------------------------------------------------------

[<Fact>]
let ``the manual view lists decisions newest first, and says so when there are none`` () =
    task {
        use! env = operatorEnv ()
        let! empty = env.Boss.Get "/review/mal/manual"
        Assert.Contains("data-review=\"manual\"", empty.Html)
        Assert.Contains("data-review-empty", empty.Html)

        let! _ = post env "/review/mal/none" [ "season", "GR19CPDWM" ]
        do! Task.Delay 20
        let! _ = post env "/review/mal/set" [ "season", slimeSeason; "mal", "37430" ]

        let! page = env.Boss.Get "/review/mal/manual"
        Assert.Equal<string list>([ slimeSeason; "GR19CPDWM" ], seasonsIn page.Html)
        Assert.Contains("data-mal=\"37430\"", rowOf page.Html slimeSeason)
        Assert.Contains("data-mal=\"none\"", rowOf page.Html "GR19CPDWM")
        Assert.Contains("data-action=\"auto\"", rowOf page.Html slimeSeason)
        Assert.DoesNotContain("data-review-empty", page.Html)
        // the tabs lead to each other
        Assert.Contains("href=\"/review/mal\"", page.Html)
        Assert.Contains("href=\"/review/mal/manual\"", page.Html)
    }

[<Fact>]
let ``back to automatic requeues the season and its dubs and drops it from the manual view`` () =
    task {
        use! env = operatorEnv ()
        routeFull env.App.Mal (spec 77777 "Another Tamer" None "TV" "2024-01-01")
        let! _ = Db.scalar<int> env.App.Db $"UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL, method = NULL, reason = 'no-title-match' WHERE season_id = '{tamerSeason}' RETURNING 1" []
        let! _ = Db.scalar<int> env.App.Db $"UPDATE cr_season_matches SET status = 'no_match', mal_id = NULL WHERE season_id = '{tamerDub}' RETURNING 1" []
        let! _ = post env "/review/mal/set" [ "season", tamerSeason; "mal", "77777" ]
        do! expectMatch env tamerDub ($"{tamerDub} matched 77777 dub {tamerSeason}")

        let! page = act env "/review/mal/auto" [ "season", tamerSeason ] "auto" "/review/mal/manual"
        Assert.DoesNotContain($"data-review-season=\"{tamerSeason}\"", page.Html)
        do! expectMatch env tamerSeason ($"{tamerSeason} pending - - -")
        do! expectMatch env tamerDub ($"{tamerDub} pending - - -")
        let! queued = Db.scalar<int64> env.App.Db $"SELECT count(*) FROM cr_season_matches WHERE season_id IN ('{tamerSeason}', '{tamerDub}') AND requested_at IS NOT NULL" []
        Assert.Equal(2L, queued)

        // the same form again, and a season that was never manual, are stale
        let! _ = act env "/review/mal/auto" [ "season", tamerSeason ] "not-open" "/review/mal/manual"
        let! _ = act env "/review/mal/auto" [ "season", "GR19CPDWM" ] "not-open" "/review/mal/manual"

        // the matcher takes it from there
        let! _ = enrichAll env.App
        let! row = match' env tamerSeason
        Assert.DoesNotContain("manual", row)
    }
