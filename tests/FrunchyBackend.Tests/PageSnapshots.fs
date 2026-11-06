/// Opt-in: saves the HTML of the signed-in pages for a design review (tools/snapshots/README.md). Skipped unless
/// FRUNCHY_SNAPSHOT_DIR is set, so a normal `dotnet test` run only reports it skipped.
module FrunchyBackend.Tests.PageSnapshots

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.MalFixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Where the shooter resolves the page's root-relative URLs: nowhere (it blocks this host), so nothing breaks the layout
let private placeholderHost = "http://snapshot.invalid"

/// Root-relative `src="/…"`, `href="/…"`, `action="/…"` and `url(/…)` point at the placeholder host; `//host/…` stays
let private standalone (html: string) =
    let attr =
        Regex.Replace(html, "\\b(src|href|action|formaction|poster)=\"(/(?!/)[^\"]*)\"", $"$1=\"{placeholderHost}$2\"")

    Regex.Replace(attr, "url\\((['\"]?)(/(?!/)[^)'\"]*)\\1\\)", $"url($1{placeholderHost}$2$1)")

/// The first `pages` pages of the 2026 sample as a submission for another Crunchyroll profile
let private samplePages (profileId: string) (pages: int) =
    let node = JsonNode.Parse(load2026SampleRaw ()) :?> JsonObject
    let all = node["pages"] :?> JsonArray
    let kept = JsonArray()

    for i in 0 .. (min pages all.Count) - 1 do
        kept.Add(all[i].DeepClone())

    let result = JsonObject()
    result["profileId"] <- JsonValue.Create profileId
    result["pages"] <- kept
    result.ToJsonString()


// ---------------------------------------------------------------------------
// Feature 031: the Progress tab's states. Three more members: "mika" (110 unfinished shows over four years, a long
// runner, an airing show without a total, watching and completed ones, started seasons without MyAnimeList details),
// "caughtup" (nothing unfinished) and "nothing" (nothing started). Their shows are matched to MyAnimeList rows
// inserted directly, after the enrichment run, so the worker never touches them.
// ---------------------------------------------------------------------------

type private ProgressShow =
    { Id: string
      Title: string
      Mal: int
      Total: int option
      Airing: bool
      Poster: string option
      Numbers: int list
      /// Watched this many days ago
      WatchedDays: float
      /// Aired this many days ago (None = the day before it was watched)
      AiredDays: float option
      Matched: bool }

let private adjectives = [| "Silent"; "Crimson"; "Last"; "Midnight"; "Paper"; "Iron"; "Glass"; "Wandering"; "Hollow"; "Golden"; "Quiet" |]

let private nouns = [| "Archive"; "Garden"; "Orbit"; "Harbor"; "Lantern"; "Season"; "Kingdom"; "Courier"; "Station"; "Echo"; "Atlas" |]

/// A flat colour as a poster, so the screenshots show rows with and without one (a data: URI, never fetched)
let private colourPoster (i: int) =
    Some
        $"""data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" width="200" height="300"><rect width="200" height="300" fill="hsl({(i * 47) % 360},35%%25,32%%25)"/></svg>"""

let private progressShow (id: string) (title: string) (mal: int) (total: int option) (numbers: int list) (watchedDays: float) =
    { Id = id
      Title = title
      Mal = mal
      Total = total
      Airing = false
      Poster = None
      Numbers = numbers
      WatchedDays = watchedDays
      AiredDays = None
      Matched = true }

/// "mika": 110 unfinished (last watched 35 to 1,016 days ago, every one with its own total or none), one long runner
/// and one airing show with no total, 5 watching (one airing show kept up with), 16 completed, 3 unmatched
let private mikaShows () =
    [ for i in 0..109 ->
          let title =
              if i = 3 then
                  "The Extraordinarily Long Title of a Show That Wraps Onto Several Lines in a Narrow Layout"
              else
                  $"{adjectives[i % 11]} {nouns[i / 11]}"

          let total, airing =
              match i % 5 with
              | 3 -> Some 24, false
              | 4 -> None, (i % 13 = 0)
              | _ -> Some 12, false

          { progressShow $"PRGU{i:D4}" title (900000 + i) total [ 1 .. 1 + i % 9 ] (35.0 + 9.0 * float i) with
              Airing = airing
              // roughly one row in four has no poster
              Poster = if i % 4 = 1 then None else colourPoster i }

      yield
          { progressShow "PRGLONG" "Pirate Voyage" 900200 (Some 1155) [ 1..266 ] 400.0 with
              Poster = colourPoster 5 }

      for j in 0..3 ->
          { progressShow $"PRGW{j:D4}" $"Seven Seas Diner {j + 1}" (900300 + j) (Some 12) [ 1 .. 3 + j ] (1.0 + 3.0 * float j) with
              Poster = colourPoster (20 + j) }

      // airing, no final count: kept up with although the last watch was 40 days ago
      yield
          { progressShow "PRGAIR" "Weekly Spirit Hunters" 900500 None [ 1..3 ] 40.0 with
              Airing = true
              AiredDays = Some 4.0
              Poster = colourPoster 31 }

      for j in 0..15 ->
          { progressShow $"PRGC{j:D4}" $"{nouns[j % 11]} of {adjectives[(j + 4) % 11]} Days" (900400 + j) (Some 12) [ 1..12 ] (60.0 + 20.0 * float j) with
              Poster = if j % 5 = 3 then None else colourPoster (40 + j) }

      for j in 0..2 ->
          { progressShow $"PRGX{j:D4}" $"Unmatched {j + 1}" 0 None [ 1; 2 ] (7.0 + float j) with Matched = false } ]

/// "caughtup": every show completed or in progress
let private caughtUpShows () =
    [ for j in 0..3 ->
          { progressShow $"CUPC{j:D4}" $"Finished {adjectives[j]} Tale" (901000 + j) (Some 12) [ 1..12 ] (45.0 + 30.0 * float j) with
              Poster = colourPoster (60 + j) }
      for j in 0..1 ->
          { progressShow $"CUPW{j:D4}" $"Ongoing {nouns[j]} Run" (901100 + j) (Some 24) [ 1 .. 6 + j ] (2.0 + 5.0 * float j) with
              Poster = colourPoster (70 + j) } ]

let private progressEntries (now: DateTime) (shows: ProgressShow list) =
    [ for s in shows do
          for n in s.Numbers do
              let watched = now.AddDays(-s.WatchedDays).AddMinutes(float n)

              let aired =
                  match s.AiredDays with
                  | Some d -> now.AddDays -d
                  | None -> watched.AddDays -1.0

              // the catalog shows the show's name, as the sample's entries do
              (datedEntry $"{s.Id}E{n:D4}" s.Id $"{s.Id}S1" n (Some aired) watched true)
                  .Replace($"Series {s.Id}", s.Title) ]

/// Import the shows for a Crunchyroll profile (100 entries a page), then match them to MyAnimeList rows
let private importProgress (app: TestApp) (key: string) (account: string) (profile: string) (shows: ProgressShow list) =
    task {
        let pages =
            progressEntries DateTime.UtcNow shows
            |> List.chunkBySize 100
            |> List.map (fun chunk -> historyUrl account, pageOf chunk)

        let! _ = submitAccepted app key (envelopeForProfile profile pages)
        let! _ = processAll app.Db

        for s in shows |> List.filter _.Matched do
            let total = s.Total |> Option.map string |> Option.defaultValue "NULL"
            let image = s.Poster |> Option.map (fun u -> $"'{u}'") |> Option.defaultValue "NULL"
            let status = if s.Airing then "currently_airing" else "finished_airing"

            do!
                Db.execute
                    app.Db
                    $"""INSERT INTO mal_anime (mal_id, url, title, type, episodes, status, image_url, year, raw, data_hash, fetched_at, checked_at)
                        VALUES ({s.Mal}, 'https://myanimelist.net/anime/{s.Mal}', 'Anime {s.Mal}', 'TV', {total}, '{status}', {image}, 2024,
                                '{{}}'::json, 'h', now(), now());
                        INSERT INTO cr_season_matches (season_id, status, mal_id, method) VALUES ('{s.Id}S1', 'matched', {s.Mal}, 'search')"""
    }

[<Fact>]
let ``save the HTML of the signed-in pages for a design review`` () =
    task {
        let dir = Environment.GetEnvironmentVariable "FRUNCHY_SNAPSHOT_DIR"

        Assert.SkipWhen(
            String.IsNullOrWhiteSpace dir,
            "snapshots only: FRUNCHY_SNAPSHOT_DIR=<folder> dotnet test --project tests/FrunchyBackend.Tests --filter-class \"*PageSnapshots*\""
        )

        let htmlDir = Path.Combine(dir, "html")
        Directory.CreateDirectory htmlDir |> ignore

        for old in Directory.EnumerateFiles(htmlDir, "*.html") do
            File.Delete old

        let! db = newDatabase ()

        use! app =
            createAppWith
                db
                [ "Frunchy:RootOperator", "animegolden"
                  // members only: what a person sees when they follow a link to a profile
                  "Frunchy:Profiles:DefaultVisibility", "members" ]
                ignore

        // ---- people: the root operator, a second viewer with history, a third who signed up with an invite
        let! rootBrowser = signedUp app "sub-animegolden" "animegolden"
        let! sakuraBrowser = signedUp app "sub-sakura" "sakura"
        let! thirdBrowser = signedUp app "sub-third" "kenji"
        (thirdBrowser :> IDisposable).Dispose()

        // ---- history: the whole 2026 sample, a shorter one for the second user
        let! rootKey = connectExtension rootBrowser
        let! _ = submitAccepted app rootKey (load2026Sample ())
        let! sakuraKey = connectExtension sakuraBrowser
        let! _ = submitAccepted app sakuraKey (samplePages "profile-sakura" 2)
        let! _ = processAll app.Db

        // feature 027: the sample's One Piece has 13 fully watched episodes, short of the long-run card's 24. Watch 24
        // more, one every three weeks from January 2025, so the card shows and the seasons section has recent seasons,
        // some of its shows watched while airing (within 7 days) and some later.
        let! onePiece =
            Db.scalar<string>
                app.Db
                "SELECT e.series_id || '|' || e.season_id FROM cr_episodes e WHERE e.series_title = 'One Piece' AND e.season_id IS NOT NULL ORDER BY e.id LIMIT 1"
                []

        let seriesOfOnePiece, seasonOfOnePiece = (let p = onePiece.Split '|' in p[0], p[1])

        let moreOnePiece =
            [ for i in 0..23 ->
                  let aired = DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc).AddDays(21.0 * float i)
                  let watched = aired.AddDays(if i % 2 = 0 then 2.0 else 20.0).AddHours 12.0
                  // the catalog keeps the show's real title: the entry names it like the sample does
                  (datedEntry $"SNAPOP{i:D3}" seriesOfOnePiece seasonOfOnePiece (1201 + i) (Some aired) watched true)
                      .Replace($"Series {seriesOfOnePiece}", "One Piece") ]

        let! _ = submitAccepted app rootKey (envelopeForProfile MainProfile [ historyUrl RealAccountId, pageOf moreOnePiece ])
        let! _ = processAll app.Db
        (rootBrowser :> IDisposable).Dispose()
        (sakuraBrowser :> IDisposable).Dispose()

        // genres, scores and posters from the scripted catalog (no real provider)
        routeSampleCatalog app.Mal
        let! _ = enrichAll app

        // ---- feature 031: the Progress tab's states (after the enrichment run, so the worker leaves them alone)
        let! mikaBrowser = signedUp app "sub-mika" "mika"
        let! mikaKey = connectExtension mikaBrowser
        do! importProgress app mikaKey "aaaaaaaa-0000-4000-8000-000000000001" "profile-mika" (mikaShows ())
        (mikaBrowser :> IDisposable).Dispose()

        let! caughtUpBrowser = signedUp app "sub-caughtup" "caughtup"
        let! caughtUpKey = connectExtension caughtUpBrowser
        do! importProgress app caughtUpKey "aaaaaaaa-0000-4000-8000-000000000002" "profile-caughtup" (caughtUpShows ())
        (caughtUpBrowser :> IDisposable).Dispose()

        let! nothingBrowser = signedUp app "sub-nothing" "nothing"
        (nothingBrowser :> IDisposable).Dispose()

        // ---- invites: the three accounts above used theirs; two more stay active
        let! _ = createInvite app
        let! _ = createInvite app

        let! seriesId =
            Db.scalar<string>
                app.Db
                """SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                   JOIN users u ON u.id = w.user_id
                   WHERE u.username = 'animegolden' AND e.duration_ms IS NOT NULL
                   GROUP BY e.series_id ORDER BY count(*) DESC, e.series_id LIMIT 1"""
                []

        let save (name: string) (language: string) (status: HttpStatusCode) (html: string) =
            Assert.True((status = HttpStatusCode.OK), $"{name}-{language}: {int status}")
            File.WriteAllText(Path.Combine(htmlDir, $"{name}-{language}.html"), standalone html)

        // raw body, not Browser.Get: that one decodes entities, which a saved page must keep
        let saveResponse (name: string) (language: string) (response: HttpResponseMessage) =
            task {
                let! html = response.Content.ReadAsStringAsync()
                save name language response.StatusCode html
            }

        let getInto (browser: Browser) (name: string) (language: string) (path: string) =
            task {
                use! response = browser.GetRaw path
                do! saveResponse name language response
            }

        // the Progress states' viewers, signed in once
        use mika = app.NewBrowser "en"
        let! _ = signInWith mika "sub-mika"
        use caughtUp = app.NewBrowser "en"
        let! _ = signInWith caughtUp "sub-caughtup"
        use nothing = app.NewBrowser "en"
        let! _ = signInWith nothing "sub-nothing"
        use sakuraViewer = app.NewBrowser "en"
        let! _ = signInWith sakuraViewer "sub-sakura"

        for language in [ "en"; "es" ] do
            // signed out
            use visitor = app.NewBrowser language
            do! getInto visitor "home" language "/"
            do! getInto visitor "signin" language "/signin"
            do! getInto visitor "guide" language "/guide"

            // back from a provider with no account yet: the sign-up form (the guide's sign-up step)
            use newcomer = app.NewBrowser language
            let! _ = signInWith newcomer $"sub-newcomer-{language}"
            do! getInto newcomer "signup" language "/signup"

            // signed in as the root operator
            use root = app.NewBrowser language
            let! _ = signInWith root "sub-animegolden"

            do! getInto root "dashboard" language "/u/animegolden"
            do! getInto root "dna" language "/u/animegolden/dna"
            do! getInto root "year-2026" language "/u/animegolden/year/2026"
            do! getInto root "anime" language $"/u/animegolden/anime/{seriesId}"
            // feature 031: Progress, as its owner in each state and as a visitor. The browsers signed in once (the
            // sign-in limiter is 30 a minute); the footer's language link switches them.
            for browser in [ mika; caughtUp; nothing; sakuraViewer ] do
                let! _ = browser.Get $"/language/{language}?returnUrl=%%2F"
                ()

            do! getInto mika "progress" language "/u/mika/progress"
            do! getInto caughtUp "progress-caughtup" language "/u/caughtup/progress"
            do! getInto nothing "progress-empty" language "/u/nothing/progress"
            do! getInto sakuraViewer "progress-visitor" language "/u/mika/progress"
            do! getInto root "progress-sample" language "/u/animegolden/progress"

            do! getInto root "compare" language "/u/sakura/compare"
            do! getInto root "account" language "/account"
            do! getInto root "review-invites" language "/review/invites"

            // the pages that show a new code, and a new share link, once (the POST's own response)
            use! created = root.PostForm("/review/invites", "/review/invites/create", [ "days", "7" ])
            do! saveResponse "invite-created" language created

            do! getInto root "review-settings" language "/review/settings"

            use! link = root.PostForm("/account", "/account/share-link", [])
            do! saveResponse "share-link-created" language link
            do! getInto root "account-shared" language "/account"

            // the sync key, shown once (a throwaway key of this test database; the guide's connect step)
            use! key = root.PostForm("/account/key", "/account/key", [])
            do! saveResponse "sync-key" language key

        printfn "page snapshots: %s" htmlDir
    }
