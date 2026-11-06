module FrunchyBackend.Tests.ProgressTests

open System
open System.Net
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.ProfileHelpers
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 031: the Progress tab (specs/031-progress/contracts/hooks.md). Every world is built from synthetic pages
// with air dates, MyAnimeList rows inserted directly, and a fixed clock; the page is read through its data-* hooks.

let private now = DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)

let private ago (days: float) = now.UtcDateTime.AddDays(-days)

type private Ep =
    { Number: int
      Aired: DateTime option
      Watched: DateTime
      Full: bool }

type private Show =
    { Series: string
      /// The Crunchyroll season id (a dub is a season of its own)
      Season: string
      Mal: int
      Matched: bool
      SharedFrom: string option
      /// MyAnimeList's episode count (None = unknown)
      Total: int option
      Airing: bool
      Eps: Ep list }

/// Fully watched episodes `numbers`, each watched `days` ago (a minute apart, so the newest is the last) and aired
/// the day before
let private watchedAgo (days: float) (numbers: int list) =
    [ for i, n in List.indexed numbers ->
          let at = (ago days).AddMinutes(float i)

          { Number = n
            Aired = Some(at.AddDays -1.0)
            Watched = at
            Full = true } ]

let private show (series: string) (mal: int) (total: int option) (eps: Ep list) =
    { Series = series
      Season = series + "S1"
      Mal = mal
      Matched = true
      SharedFrom = None
      Total = total
      Airing = false
      Eps = eps }

/// The feature's world: every state and edge the spec names (see the expectations in the tests)
let private mainShows () =
    [ // unfinished: 4 of 12, 90 days ago
      show "SHOWUNF" 101 (Some 12) (watchedAgo 90.0 [ 1; 2; 3; 4 ])
      // completed with exactly the total, and with one more (a recap)
      show "SHOWCOMP" 102 (Some 12) (watchedAgo 300.0 [ 1..12 ])
      show "SHOWCOMPX" 103 (Some 12) (watchedAgo 250.0 [ 1..13 ])
      // watching: 6 of 12, last watched 10 days ago
      show "SHOWWATCH" 104 (Some 12) (watchedAgo 10.0 [ 1..6 ])
      // airing, no total: the newest episode aired 5 days ago (kept up with) though it was watched 40 days ago
      { show "SHOWAIRNEW" 105 None (watchedAgo 40.0 [ 1..3 ] |> List.map (fun e -> { e with Aired = Some(ago 5.0) })) with
          Airing = true }
      // airing, no total, aired and watched two months ago: unfinished
      { show "SHOWAIROLD" 106 None (watchedAgo 60.0 [ 1..3 ] |> List.map (fun e -> { e with Aired = Some(ago 62.0) })) with
          Airing = true }
      // a few minutes of episode 1 only: not started
      show "SHOWPART" 107 (Some 12) [ { Number = 1; Aired = Some(ago 2.0); Watched = ago 1.0; Full = false } ]
      // a dub and its original, the same three episodes: counted once
      show "SHOWDUB" 108 (Some 12) (watchedAgo 100.0 [ 1..3 ])
      { show "SHOWDUB" 108 (Some 12) (watchedAgo 100.0 [ 1..3 ]) with
          Season = "SHOWDUBD1"
          SharedFrom = Some "SHOWDUBS1" }
      // one entry across two Crunchyroll seasons whose numbers restart: 5 + 4 episodes
      show "SHOWONEP" 109 (Some 12) (watchedAgo 200.0 [ 1..5 ])
      { show "SHOWONEP" 109 (Some 12) (watchedAgo 199.0 [ 1..4 ]) with Season = "SHOWONEPS2" }
      // a long runner: 5 episodes of 1,155
      show "SHOWLONG" 110 (Some 1155) (watchedAgo 400.0 [ 1..5 ])
      // episode 4 is in the catalog (a few minutes of it were watched), so the next link leads to its page
      show "SHOWNEXT" 111 (Some 12) (watchedAgo 120.0 [ 1..3 ] @ [ { Number = 4; Aired = Some(ago 121.0); Watched = ago 120.0; Full = false } ])
      // two started Crunchyroll seasons with no MyAnimeList entry
      { show "SHOWUNMA" 0 None (watchedAgo 5.0 [ 1; 2 ]) with Matched = false }
      { show "SHOWUNMB" 0 None (watchedAgo 6.0 [ 1; 2 ]) with Matched = false } ]

let private entriesOf (s: Show) =
    [ for e in s.Eps -> datedEntry $"{s.Season}E{e.Number:D4}" s.Series s.Season e.Number e.Aired e.Watched e.Full ]

/// Give the matched seasons their MyAnimeList entries and matches
let private addMal (db: FrunchyBackend.Infrastructure.Database.DbConfig) (shows: Show list) =
    task {
        for s in shows |> List.filter _.Matched |> List.distinctBy _.Mal do
            let total = s.Total |> Option.map string |> Option.defaultValue "NULL"
            let status = if s.Airing then "currently_airing" else "finished_airing"

            do!
                Db.execute
                    db
                    $"""INSERT INTO mal_anime (mal_id, url, title, type, episodes, status, year, raw, data_hash, fetched_at, checked_at)
                        VALUES ({s.Mal}, 'https://myanimelist.net/anime/{s.Mal}', 'Anime {s.Mal}', 'TV', {total}, '{status}', 2024,
                                '{{}}'::json, 'h', now(), now())"""

        // originals first: a dub's row references its original season
        for s in shows |> List.filter _.Matched |> List.sortBy _.SharedFrom.IsSome do
            match s.SharedFrom with
            | Some original ->
                do!
                    Db.execute
                        db
                        $"INSERT INTO cr_season_matches (season_id, status, mal_id, method, shared_from) VALUES ('{s.Season}', 'matched', {s.Mal}, 'dub', '{original}')"
            | None ->
                do!
                    Db.execute
                        db
                        $"INSERT INTO cr_season_matches (season_id, status, mal_id, method) VALUES ('{s.Season}', 'matched', {s.Mal}, 'search')"
    }

type private World =
    { App: TestApp
      Owner: Browser
      Other: Browser
      Stranger: Browser }

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            (this.Owner :> IDisposable).Dispose()
            (this.Other :> IDisposable).Dispose()
            (this.Stranger :> IDisposable).Dispose()
            (this.App :> IAsyncDisposable).DisposeAsync()

/// "animegolden" with `shows` imported as `pages` (each a list of entries), the fixed clock, "other" (a member) and a
/// signed-out browser. `extraConfig` can change the default visibility (the fixture's is public).
let private buildWith (extraConfig: (string * string) list) (shows: Show list) (pages: string list list) : Task<World> =
    task {
        let clock = TestClock(Now = now)
        let! db = newDatabase ()

        let! app =
            createAppWith db (("Frunchy:RootOperator", "boss") :: extraConfig) (fun (b: WebApplicationBuilder) ->
                b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

        let! owner = signedUp app "sub-owner" "animegolden"
        let! key = connectExtension owner
        let! _ = submitAccepted app key (envelopeOf [ for p in pages -> historyUrl RealAccountId, pageOf p ])
        let! _ = processAll app.Db
        do! addMal app.Db shows
        let! other = signedUp app "sub-other" "other"

        return
            { App = app
              Owner = owner
              Other = other
              Stranger = app.NewBrowser() }
    }

let private build (shows: Show list) =
    // an import needs at least one page, even when nothing in it is a show
    buildWith [] shows (match shows |> List.collect entriesOf |> List.chunkBySize 100 with [] -> [ [] ] | pages -> pages)

let private mainWorld () = build (mainShows ())

// ---------------------------------------------------------------------------
// Reading hooks. Exact names only: `data-progress` is not `data-progress-list`.
// ---------------------------------------------------------------------------

let private attrsOf (tag: string) =
    [ for a in Regex.Matches(tag, "data-([a-z-]+)=\"([^\"]*)\"") -> a.Groups[1].Value, a.Groups[2].Value ] |> Map.ofList

/// The attributes of every element carrying exactly `data-{hook}`, in document order, with its position
let private tagsWith (hook: string) (html: string) =
    [ for m in Regex.Matches(html, $"<[a-z0-9]+\\b[^>]*\\sdata-{hook}(=\"[^\"]*\")?(?=[\\s>/])[^>]*>") -> m.Index, attrsOf m.Value ]

let private elements hook html = tagsWith hook html |> List.map snd
let private attr (name: string) (m: Map<string, string>) = m.TryFind name |> Option.defaultValue ""
let private has hook html = not (tagsWith hook html).IsEmpty

let private hookValue (hook: string) (html: string) =
    match elements hook html with
    | m :: _ -> attr hook m
    | [] -> ""

/// One listed entry: the `li`'s hooks and what its row holds
type private Row =
    { List: string
      Id: string
      Watched: string
      Total: string
      LongRunner: bool
      /// (number, href) of the next-episode button
      Next: (string * string) option
      Last: string
      Block: string }

let private rows (html: string) : Row list =
    let lists = tagsWith "progress-list" html |> List.map (fun (i, m) -> i, attr "progress-list" m)

    [ for m in Regex.Matches(html, "<li\\b[^>]*\\sdata-progress-entry=[^>]*>.*?</li>", RegexOptions.Singleline) ->
          let li = attrsOf (Regex.Match(m.Value, "^<li\\b[^>]*>").Value)

          let next =
              let a = Regex.Match(m.Value, "<a\\b[^>]*\\sdata-next-episode=\"(\\d+)\"[^>]*>")
              if a.Success then Some(a.Groups[1].Value, Regex.Match(a.Value, "href=\"([^\"]*)\"").Groups[1].Value) else None

          { List = lists |> List.filter (fun (i, _) -> i < m.Index) |> List.last |> snd
            Id = attr "progress-entry" li
            Watched = attr "entry-watched" li
            Total = attr "entry-total" li
            LongRunner = li.ContainsKey "entry-long-runner"
            Next = next
            Last = Regex.Match(m.Value, "<time\\b[^>]*datetime=\"([^\"]*)\"[^>]*data-entry-last").Groups[1].Value
            Block = m.Value } ]

let private ids (list: string) (html: string) =
    rows html |> List.filter (fun r -> r.List = list) |> List.map _.Id

let private rowOf (id: string) (html: string) = rows html |> List.find (fun r -> r.Id = id)

let private progressPath = "/u/animegolden/progress"

let private getAs (browser: Browser) (path: string) =
    task {
        let! page = browser.Get path
        Assert.Equal(HttpStatusCode.OK, page.Status)
        return page.Html
    }

let private ownerPage (w: World) = getAs w.Owner progressPath

// ---------------------------------------------------------------------------
// User story 1: the owner's page
// ---------------------------------------------------------------------------

[<Fact>]
let ``the counts follow the states: completed, watching, unfinished and the unmatched note`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        Assert.Equal("owner", hookValue "progress" html)
        Assert.Equal("2", hookValue "progress-completed" html)
        Assert.Equal("2", hookValue "progress-watching" html)
        Assert.Equal("6", hookValue "progress-unfinished" html)
        // two started Crunchyroll seasons without MyAnimeList details, and nothing about them is listed
        Assert.Equal("2", hookValue "progress-unmatched" html)
        Assert.DoesNotContain("SHOWUNMA", html)
        Assert.DoesNotContain("SHOWUNMB", html)
        Assert.False(has "progress-empty" html)
    }

[<Fact>]
let ``the lists hold the right entries, newest watch first`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        Assert.Equal<string list>([ "104"; "105" ], ids "watching" html)
        // 106 (60 days ago), 101 (90), 108 (100), 111 (120), 109 (200), 110 (400)
        Assert.Equal<string list>([ "106"; "101"; "108"; "111"; "109"; "110" ], ids "unfinished" html)
        // the newest completion first
        Assert.Equal<string list>([ "103"; "102" ], ids "completed" html)
        // the section order: watching, unfinished, completed
        let at name = html.IndexOf $"data-progress-list=\"{name}\""
        Assert.True(at "watching" < at "unfinished" && at "unfinished" < at "completed")
    }

[<Fact>]
let ``an entry of 12 with 4 watched 3 months ago is unfinished with its figures and the date`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w
        let r = rowOf "101" html

        Assert.Equal("unfinished", r.List)
        Assert.Equal("4", r.Watched)
        Assert.Equal("12", r.Total)
        Assert.False r.LongRunner
        // the newest watch: episode 4, 90 days ago, three minutes after the first
        Assert.Equal((ago 90.0).AddMinutes(3.0).ToString("yyyy-MM-ddTHH:mm:ssZ"), r.Last)
        Assert.Contains("data-fr-time", r.Block)
        Assert.Contains("4 of 12 episodes", r.Block)
        // the title leads to the public series page, never into a history
        Assert.Contains("href=\"/anime/SHOWUNF\"", r.Block)
        Assert.Contains("data-series-page-link=\"SHOWUNF\"", r.Block)
        Assert.Contains("Series SHOWUNF", r.Block)
    }

[<Fact>]
let ``completed is at least the total, also with an extra episode`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        Assert.Equal(("12", "12"), ((rowOf "102" html).Watched, (rowOf "102" html).Total))
        Assert.Equal(("13", "12"), ((rowOf "103" html).Watched, (rowOf "103" html).Total))
        // a completed show has no next-episode button
        Assert.True((rowOf "102" html).Next.IsNone)
    }

[<Fact>]
let ``an entry of 12 with 6 watched 10 days ago is watching`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w
        Assert.Equal("watching", (rowOf "104" html).List)
        Assert.Equal("6", (rowOf "104" html).Watched)
    }

[<Fact>]
let ``an airing entry with no total is watching while its newest episode aired within 21 days, else unfinished`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        let kept = rowOf "105" html
        Assert.Equal("watching", kept.List)
        // no final count: the figure has no "of", the total hook is absent
        Assert.Equal("", kept.Total)
        Assert.Contains("3 episodes", kept.Block)
        Assert.DoesNotContain(" of ", kept.Block.Replace("last watched", ""))

        let old = rowOf "106" html
        Assert.Equal("unfinished", old.List)
        Assert.Equal("", old.Total)
    }

[<Fact>]
let ``a dub and its original count once`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w
        Assert.Equal("3", (rowOf "108" html).Watched)
    }

[<Fact>]
let ``an entry split across Crunchyroll seasons is one entry with its episodes summed`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        Assert.Equal(1, ids "unfinished" html |> List.filter ((=) "109") |> List.length)
        Assert.Equal("9", (rowOf "109" html).Watched)
        // numbers restart per season: the next one is in the season of the newest watch (the second one, up to 4)
        Assert.Equal(Some "5", (rowOf "109" html).Next |> Option.map fst)
    }

[<Fact>]
let ``a few minutes of one episode don't start a show`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w
        Assert.DoesNotContain("SHOWPART", html)
        Assert.False(rows html |> List.exists (fun r -> r.Id = "107"))
    }

[<Fact>]
let ``a long runner is marked and never shown against its total`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w
        let r = rowOf "110" html

        Assert.True r.LongRunner
        Assert.Equal("5", r.Watched)
        // the hooks may keep the total; the visible text never says "of 1,155"
        Assert.Equal("1155", r.Total)
        Assert.DoesNotContain("1,155", r.Block)
        Assert.Contains("5 episodes", r.Block)
        // and nobody else is marked
        Assert.Equal(1, rows html |> List.filter _.LongRunner |> List.length)
    }

[<Fact>]
let ``the next episode links to its page when the catalog has it, else to the series on Crunchyroll`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        // 111: episode 4 is in the catalog
        Assert.Equal(Some("4", "/anime/SHOWNEXT/episode/SHOWNEXTS1E0004"), (rowOf "111" html).Next)

        // 101: episode 5 isn't: the series on Crunchyroll, in a new tab
        let n, href = (rowOf "101" html).Next.Value
        Assert.Equal("5", n)
        Assert.Equal("https://www.crunchyroll.com/series/SHOWUNF", href)
        let anchor = Regex.Match((rowOf "101" html).Block, "<a\\b[^>]*data-next-episode=\"5\"[^>]*>").Value
        Assert.Contains("target=\"_blank\"", anchor)
        Assert.Contains("rel=\"noopener\"", anchor)
        Assert.Contains("Next: episode 5", (rowOf "101" html).Block)
        // the screen reader text says whose episode it is
        Assert.Contains("Series SHOWUNF", (rowOf "101" html).Block)
    }

[<Fact>]
let ``the next episode of a show with a slug uses it`` () =
    task {
        use! w = mainWorld ()
        do! Db.execute w.App.Db "UPDATE cr_series SET slug = 'the-slug' WHERE id = 'SHOWUNF'"
        let! html = ownerPage w
        Assert.Equal(Some("5", "https://www.crunchyroll.com/series/SHOWUNF/the-slug"), (rowOf "101" html).Next)
    }

[<Fact>]
let ``the next episode is never past the end of a known total`` () =
    task {
        // 12 of 12 are in the catalog, but episodes 1, 2, 3 and 12 are the ones watched: the highest is the last
        let shows = [ show "SHOWGAP" 201 (Some 12) (watchedAgo 90.0 [ 1; 2; 3; 12 ]) ]
        use! w = build shows
        let! html = ownerPage w
        let r = rowOf "201" html

        Assert.Equal("unfinished", r.List)
        Assert.Equal(None, r.Next)
    }

[<Fact>]
let ``the completed list holds the 12 newest and says how many there are`` () =
    task {
        let shows =
            [ for i in 1..15 -> show $"SHOWCMP{i:D2}" (300 + i) (Some 2) (watchedAgo (float (10 * i)) [ 1; 2 ]) ]

        use! w = build shows
        let! html = ownerPage w

        Assert.Equal("15", hookValue "progress-completed" html)
        Assert.Equal<string list>([ for i in 1..12 -> string (300 + i) ], ids "completed" html)
    }

// ---------------------------------------------------------------------------
// Unfinished by year
// ---------------------------------------------------------------------------

[<Fact>]
let ``up to ten unfinished shows are plain rows, no year groups`` () =
    task {
        let shows = [ for i in 1..10 -> show $"SHOWTEN{i:D2}" (400 + i) (Some 12) (watchedAgo (float (60 + i)) [ 1 ]) ]
        use! w = build shows
        let! html = ownerPage w

        Assert.Equal(10, ids "unfinished" html |> List.length)
        Assert.False(has "progress-year" html)
    }

[<Fact>]
let ``more than ten unfinished shows are grouped by the year of the last watch, the newest open and capped`` () =
    task {
        // 14 last watched in 2026 (the clock is 6 November: 40 to 300 days ago), 5 in 2025, 3 in 2024
        let shows =
            [ for i in 1..14 -> show $"SHOWYAA{i:D2}" (500 + i) (Some 12) (watchedAgo (float (40 + i * 15)) [ 1 ])
              for i in 1..5 -> show $"SHOWYAB{i:D2}" (520 + i) (Some 12) (watchedAgo (float (320 + i * 10)) [ 1 ])
              for i in 1..3 -> show $"SHOWYAC{i:D2}" (540 + i) (Some 12) (watchedAgo (float (700 + i * 10)) [ 1 ]) ]

        use! w = build shows
        let! html = ownerPage w

        let groups = tagsWith "progress-year" html
        Assert.Equal<(string * string) list>(
            [ "2026", "14"; "2025", "5"; "2024", "3" ],
            groups |> List.map (fun (_, m) -> attr "progress-year" m, attr "progress-year-shows" m)
        )

        // one list wrapper around all the groups, and every unfinished show is in the page
        Assert.Equal(1, tagsWith "progress-list" html |> List.filter (fun (_, m) -> attr "progress-list" m = "unfinished") |> List.length)
        Assert.Equal(22, ids "unfinished" html |> List.length)

        // only the newest group is open
        let opening (year: string) =
            let tag = Regex.Match(html, $"<details\\b[^>]*data-progress-year=\"{year}\"[^>]*>").Value
            tag.Contains " open"

        Assert.True(opening "2026")
        Assert.False(opening "2025")
        Assert.False(opening "2024")

        // the open group lists 10 rows and keeps the other 4 behind "Show all 14"
        let first = html.IndexOf "data-progress-year=\"2026\""
        let second = html.IndexOf "data-progress-year=\"2025\""
        let block = html.Substring(first, second - first)
        let nested = block.IndexOf "<details"
        Assert.True(nested > 0)
        Assert.Equal(10, Regex.Matches(block.Substring(0, nested), "data-progress-entry").Count)
        Assert.Equal(4, Regex.Matches(block.Substring nested, "data-progress-entry").Count)
        Assert.Contains("Show all 14", block)
    }

// ---------------------------------------------------------------------------
// States: all caught up, nothing started
// ---------------------------------------------------------------------------

[<Fact>]
let ``an owner with nothing unfinished sees the all-caught-up line under the heading`` () =
    task {
        let shows = [ show "SHOWDONE" 601 (Some 2) (watchedAgo 5.0 [ 1; 2 ]); show "SHOWNOW" 602 (Some 12) (watchedAgo 2.0 [ 1; 2 ]) ]
        use! w = build shows
        let! html = ownerPage w

        Assert.Equal("0", hookValue "progress-unfinished" html)
        Assert.Contains("You're all caught up", html)
        Assert.Empty(ids "unfinished" html)
        Assert.False(html.Contains "data-progress-list=\"unfinished\"")
    }

[<Fact>]
let ``an owner with nothing started gets the empty state instead of counts and lists`` () =
    task {
        // the only watch is a few minutes of one episode
        let shows = [ show "SHOWPART" 701 (Some 12) [ { Number = 1; Aired = Some(ago 2.0); Watched = ago 1.0; Full = false } ] ]
        use! w = build shows
        let! html = ownerPage w

        Assert.True(has "progress-empty" html)
        Assert.Contains("Nothing started yet", html)
        Assert.False(has "progress-completed" html)
        Assert.False(has "progress-list" html)
        Assert.DoesNotContain("SHOWPART", html)
    }

[<Fact>]
let ``a user with no history at all gets the empty state`` () =
    task {
        use! w = build []
        let! html = ownerPage w
        Assert.True(has "progress-empty" html)
        Assert.Equal("owner", hookValue "progress" html)
    }

// ---------------------------------------------------------------------------
// User story 2: everyone else sees counts only
// ---------------------------------------------------------------------------

let private titles = [ for s in mainShows () |> List.filter (fun s -> s.Mal <> 0) -> $"Series {s.Series}" ] |> List.distinct

/// What a visitor's page must never hold: any list, entry, next-episode link or the title of a started show, nor
/// a link to one
let private assertCountsOnly (html: string) =
    Assert.Equal("visitor", hookValue "progress" html)
    Assert.Equal("2", hookValue "progress-completed" html)
    Assert.Equal("2", hookValue "progress-watching" html)
    Assert.Equal("6", hookValue "progress-unfinished" html)
    Assert.Equal("2", hookValue "progress-unmatched" html)
    Assert.False(has "progress-list" html)
    Assert.False(has "progress-entry" html)
    Assert.False(has "next-episode" html)
    Assert.False(has "series-page-link" html)
    Assert.False(has "entry-watched" html)
    Assert.False(has "entry-last" html)
    Assert.DoesNotContain("data-progress-list", html)
    Assert.DoesNotContain("data-progress-entry", html)
    Assert.DoesNotContain("data-next-episode", html)
    Assert.DoesNotContain("href=\"/anime/", html)
    Assert.DoesNotContain("crunchyroll.com/series", html)

    for title in titles do
        Assert.DoesNotContain(title, html)

    for series in [ "SHOWUNF"; "SHOWWATCH"; "SHOWLONG"; "SHOWNEXT"; "SHOWUNMA" ] do
        Assert.DoesNotContain(series, html)

    // the line saying whose lists they are
    Assert.Contains("Only ANIMEGOLDEN can see which shows these are.", html)

[<Fact>]
let ``a visitor sees the three counts and no title, list or next episode`` () =
    task {
        use! w = mainWorld ()

        for browser in [ w.Stranger; w.Other ] do
            let! html = getAs browser progressPath
            assertCountsOnly html
    }

[<Fact>]
let ``an operator who isn't the owner, and the root, see counts only like anyone`` () =
    task {
        use! w = mainWorld ()
        let! root = signedUp w.App "sub-boss" "boss"
        use root = root
        let! operator = signedUp w.App "sub-op" "operator"
        use operator = operator
        do! Db.execute w.App.Db "INSERT INTO operators (user_id) SELECT id FROM users WHERE username = 'operator'"

        for browser in [ root; operator ] do
            let! html = getAs browser progressPath
            assertCountsOnly html
    }

[<Fact>]
let ``a visitor with nothing to count gets a plain line`` () =
    task {
        use! w = build []
        let! html = getAs w.Stranger progressPath
        Assert.Equal("visitor", hookValue "progress" html)
        Assert.True(has "progress-empty" html)
        Assert.Contains("ANIMEGOLDEN hasn't started a show yet.", html)
        Assert.False(has "progress-list" html)
    }

[<Fact>]
let ``the owner sees their own lists on a private profile, a share-link holder sees counts only`` () =
    task {
        use! w = buildWith [ defaultKey, "private" ] (mainShows ()) (mainShows () |> List.collect entriesOf |> List.chunkBySize 100)

        let! own = getAs w.Owner progressPath
        Assert.Equal("owner", hookValue "progress" own)
        Assert.Equal<string list>([ "104"; "105" ], ids "watching" own)

        let! path, _ = createShareLink w.Owner
        use holder = w.App.NewBrowser()
        let! landed = holder.Get path
        Assert.Equal(HttpStatusCode.OK, landed.Status)

        let! html = getAs holder progressPath
        assertCountsOnly html
    }

// ---------------------------------------------------------------------------
// The gate: the same answers as the other tabs
// ---------------------------------------------------------------------------

[<Fact>]
let ``a private profile's Progress page is exactly the unknown-user 404 for everyone but the owner`` () =
    task {
        use! w = buildWith [ defaultKey, "private" ] (mainShows ()) (mainShows () |> List.collect entriesOf |> List.chunkBySize 100)

        // each viewer's own unknown-user page is the reference: a signed-in page differs in its header only
        for browser in [ w.Stranger; w.Other ] do
            let! unknown = browser.GetRaw "/u/nobody-here/progress"
            let! unknownBody = unknown.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode)

            let! hidden = browser.GetRaw progressPath
            let! body = hidden.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode)
            // the antiforgery token of a signed-in page's sign-out form is new on every response
            let same (html: string) = Regex.Replace(withoutLanguageReturn html, "value=\"[A-Za-z0-9_-]{60,}\"", "value=\"\"")
            Assert.Equal(same unknownBody, same body)
            Assert.False(setsCookie hidden)
    }

[<Fact>]
let ``a Members profile sends a signed-out visitor to sign in and back here`` () =
    task {
        // "" is the app's own default: members
        use! w = buildWith [ defaultKey, "" ] (mainShows ()) (mainShows () |> List.collect entriesOf |> List.chunkBySize 100)

        use! response = w.Stranger.GetRaw progressPath
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        Assert.Equal("/signin?returnUrl=%2Fu%2Fanimegolden%2Fprogress", string response.Headers.Location)

        // a signed-in member opens it, with counts only
        let! html = getAs w.Other progressPath
        assertCountsOnly html
    }

[<Fact>]
let ``an unknown username and a bad one are the plain 404`` () =
    task {
        use! w = mainWorld ()

        for path in [ "/u/nobody-here/progress"; "/u/bad%20name/progress" ] do
            use! response = w.Stranger.GetRaw path
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
    }

// ---------------------------------------------------------------------------
// The tab and the languages
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Progress tab is current on the page and has one h1`` () =
    task {
        use! w = mainWorld ()
        let! html = ownerPage w

        let tab = Regex.Match(html, "<a\\b[^>]*data-progress-link[^>]*>").Value
        Assert.Contains("data-tab=\"progress\"", tab)
        Assert.Contains("aria-current=\"page\"", tab)
        Assert.Contains("href=\"/u/animegolden/progress\"", tab)
        Assert.Equal(1, Regex.Matches(html, "<h1\\b").Count)
    }

[<Fact>]
let ``Spanish owners and visitors get Spanish texts`` () =
    task {
        use! w = mainWorld ()
        use es = w.App.NewBrowser "es"
        let! _ = signInWith es "sub-owner"
        let! own = getAs es progressPath

        Assert.Contains("Sin terminar", own)
        Assert.Contains("Completadas", own)
        Assert.Contains("Viendo", own)
        Assert.Contains("Siguiente: episodio 5", own)
        Assert.Contains("4 de 12 episodios", own)
        Assert.Contains("visto por última vez", own)
        Assert.Contains("Larga duración", own)
        Assert.Contains("temporadas de Crunchyroll empezadas", own)

        use esVisitor = w.App.NewBrowser "es"
        let! visitor = getAs esVisitor progressPath
        Assert.Contains("Solo ANIMEGOLDEN puede ver qué series son.", visitor)
        Assert.DoesNotContain("Series SHOWUNF", visitor)
    }

// ---------------------------------------------------------------------------
// Order independence
// ---------------------------------------------------------------------------

/// Every hook the page renders, to compare two renderings
let private hooks (html: string) =
    [ for m in Regex.Matches(html, "data-(?!address)[a-z-]+=\"[^\"]*\"") -> m.Value ]

[<Fact>]
let ``re-sending and reordering the history gives the same page`` () =
    task {
        let shows = mainShows ()
        let entries = shows |> List.collect entriesOf

        let ordered = entries |> List.chunkBySize 100
        // newest page first, entries reversed inside it, and the first page sent twice
        let reversed = entries |> List.rev |> List.chunkBySize 7
        let resent = reversed @ [ List.head reversed ]

        use! a = buildWith [] shows ordered
        use! b = buildWith [] shows resent

        let! first = ownerPage a
        let! second = ownerPage b
        Assert.Equal<string list>(hooks first, hooks second)
        Assert.Equal<string list>(ids "unfinished" first, ids "unfinished" second)
    }
