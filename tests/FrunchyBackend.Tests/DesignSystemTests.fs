module FrunchyBackend.Tests.DesignSystemTests

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 004: the shared design system (contracts/design-system.md, research R13, R14); feature 010 moved the values to Night Broadcast

/// The browser extension's tokens file, next to this repository (null when it isn't checked out)
let private extensionTokensFile () =
    let rec repoRoot (dir: DirectoryInfo) =
        if isNull dir then None
        elif File.Exists(Path.Combine(dir.FullName, "FrunchyBackend.slnx")) then Some dir
        else repoRoot dir.Parent

    repoRoot (DirectoryInfo AppContext.BaseDirectory)
    |> Option.map (fun root -> Path.Combine(root.Parent.FullName, "browser-extension", "src", "styles", "tokens.css"))
    |> Option.filter File.Exists

let private customProperties (css: string) =
    [ for m in Regex.Matches(css, @"(--[a-z0-9-]+)\s*:\s*([^;]+);") -> m.Groups[1].Value, m.Groups[2].Value.Trim() ]
    |> Map.ofList

[<Fact>]
let ``every token shared with the extension has the same value`` () =
    match extensionTokensFile () with
    | None -> Assert.Skip "../browser-extension/src/styles/tokens.css isn't checked out next to this repository"
    | Some file ->
        let extension = customProperties (File.ReadAllText file)
        let website = Map.ofList Theme.tokens

        let shared =
            extension |> Map.toList |> List.filter (fun (name, _) -> name <> "--font-family" && website.ContainsKey name)

        // The extension lists only what it uses, but each of those must exist on the website too
        let missing = extension |> Map.toList |> List.map fst |> List.filter (fun n -> n <> "--font-family" && not (website.ContainsKey n))
        Assert.Empty missing
        Assert.NotEmpty shared

        for name, value in shared do
            Assert.True((value = website[name]), $"{name}: extension {value}, website {website[name]}")

// WCAG 2.1 relative luminance and contrast ratio
let private luminance (hex: string) =
    let channel (i: int) =
        let c = float (Int32.Parse(hex.Substring(i, 2), NumberStyles.HexNumber)) / 255.0
        if c <= 0.03928 then c / 12.92 else Math.Pow((c + 0.055) / 1.055, 2.4)

    0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5

let private contrast (a: string) (b: string) =
    let la, lb = luminance a, luminance b
    (max la lb + 0.05) / (min la lb + 0.05)

let private color name = Theme.colors |> List.find (fst >> (=) name) |> snd

let private surfaces =
    [ "surface"; "surface-container-low"; "surface-container"; "surface-container-high"; "surface-container-highest" ]

[<Fact>]
let ``text and controls meet WCAG AA contrast on every surface they're used on`` () =
    let body =
        [ for s in surfaces do
              for text in [ "on-surface"; "on-surface-variant"; "primary"; "secondary"; "tertiary"; "error" ] -> text, s
          // outline is placeholder text only, on inputs (surface-container) and the page; it fails on
          // surface-container-highest (3.9:1), so it must never be used as text there
          for s in [ "surface"; "surface-container-low"; "surface-container" ] -> "outline", s
          yield "on-primary", "primary"
          yield "on-primary", "primary-fixed"
          yield "on-error", "error"
          yield "on-error-container", "error-container" ]

    for text, background in body do
        let ratio = contrast (color text) (color background)
        Assert.True(ratio >= 4.5, $"{text} on {background}: {ratio:F2} < 4.5")

    // Input borders and other UI boundaries against the surfaces they sit on (WCAG 1.4.11)
    // ... the input itself fills with surface-container, so its border is checked against that too
    for s in [ "surface"; "surface-container-low"; "surface-container" ] do
        let ratio = contrast (color "outline") (color s)
        Assert.True(ratio >= 3.0, $"outline on {s}: {ratio:F2} < 3")

[<Fact>]
let ``the icon font is subset to exactly the icons the views can use`` () =
    let names = Theme.iconNames |> List.sort |> String.concat ","
    Assert.Contains($"icon_names={names}", Theme.fontStylesheets |> String.concat " ")
    Assert.Equal(Theme.iconNames.Length, Theme.iconNames |> List.distinct |> List.length)

[<Fact>]
let ``the component CSS has no gradient`` () =
    Assert.DoesNotContain("gradient", Theme.componentCss, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``the display font is a token`` () =
    Assert.Contains("--font-display", Theme.tokens |> List.map fst)

[<Theory>]
[<InlineData("/")>]
[<InlineData("/signin")>]
[<InlineData("/nowhere")>]
let ``pages load the design system, not DaisyUI`` (path: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client "en" path
        let! html = readHtml response
        Assert.DoesNotContain("daisyui", html)
        Assert.DoesNotContain("data-theme", html)
        Assert.Contains("family=Figtree", html)
        Assert.Contains("--color-primary: #ffb547;", html)
        Assert.Contains("tailwind.config", html)
        Assert.Contains("<main id=\"main\"", html)
        Assert.Contains("href=\"#main\"", html)
    }

// ---------------------------------------------------------------------------
// Every page: on the design system, with its test hooks (T015, contracts/design-system.md)
// ---------------------------------------------------------------------------

/// Neo-Scrobble values and fonts (feature 004) that Night Broadcast (feature 010) replaced; none may survive
let private retiredValues =
    [ "#4cd7f6"; "#0f131c"; "#06b6d4"; "#c0c1ff"; "Plus+Jakarta"; "Plus Jakarta"; "JetBrains"; "--color-primary: #4cd7f6" ]

/// DaisyUI component classes and the old palette classes; none may survive the redesign
let private daisyClasses =
    set
        [ "btn"; "btn-primary"; "btn-ghost"; "btn-sm"; "btn-error"; "btn-outline"; "card"; "card-body"; "card-title"
          "card-side"; "stat"; "stats"; "stat-value"; "stat-title"; "alert"; "alert-error"; "alert-success"; "alert-info"
          "alert-warning"; "badge"; "badge-secondary"; "navbar"; "hero"; "hero-content"; "footer-center"
          "input-bordered"; "form-control"; "label-text"; "link"; "link-hover"; "progress"; "progress-primary"
          "text-gray-400"; "text-gray-500"; "bg-base-100"; "bg-base-200"; "bg-base-300"; "text-warning" ]

let private leftovers (html: string) =
    [ for m in Regex.Matches(html, "class=\"([^\"]*)\"") do
          for c in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) do
              if daisyClasses.Contains c then yield c ]
    |> List.distinct

/// Problems of one page: DaisyUI leftovers, a missing design system, missing hooks
let private pageProblems (name: string) (html: string) (hooks: string list) =
    [ match leftovers html with
      | [] -> ()
      | found -> yield $"""{name}: DaisyUI classes left: {String.Join(", ", found)}"""
      if not (html.Contains "--color-primary: #ffb547;") then
          yield $"{name}: not on the design system layout"
      for value in retiredValues do
          if html.Contains(value, StringComparison.OrdinalIgnoreCase) then
              yield $"{name}: still has the retired value {value}"
      for hook in hooks do
          if not (html.Contains hook) then
              yield $"{name}: missing {hook}" ]

[<Fact>]
let ``every page is on the design system and keeps its test hooks`` () =
    task {
        // the owner is also the root operator, so the review pages can be checked
        let! db = newDatabase ()
        use! app = createAppWith db [ "Frunchy:RootOperator", "animegolden" ] ignore
        let problems = ResizeArray<string>()
        let assertPage name html hooks = problems.AddRange(pageProblems name html hooks)

        // Public pages
        let anonymous = app.NewBrowser()
        let! home = anonymous.Get "/"
        assertPage "home" home.Html [ "href=\"/signin\"" ]
        let! signin = anonymous.Get "/signin?error=failed"
        assertPage "sign-in (error)" signin.Html [ "data-signin-error"; "data-provider=\"test\"" ]
        let! missing = anonymous.Get "/nowhere"
        assertPage "404" missing.Html [ "href=\"/\"" ]

        // Sign-up and claim, with a pending identity
        let pending = app.NewBrowser()
        let! signup = AuthHelpers.signInWith pending "sub-new"
        assertPage "sign-up" signup.Html [ "name=\"invite\""; "name=\"username\""; "name=\"displayName\""; "data-claim-link" ]
        let! claim = pending.Get "/claim"
        assertPage "claim" claim.Html [ "name=\"syncKey\"" ]

        // A signed-in owner with a history
        use! owner = AuthHelpers.signedUp app "sub-owner" "animegolden"
        let! key = AuthHelpers.connectExtension owner
        let! _ = TestHelpers.submitAccepted app key (load2026Sample ())
        let! _ = TestHelpers.processAll app.Db
        let! account = owner.Get "/account"
        assertPage "account" account.Html [ "data-account"; "data-key-active"; "data-identity=\"test\""; "data-link-provider=\"test2\""; "data-security-event"; "data-sync"; "data-review-link"; "data-operator-status"; "data-operators-link" ]
        let! keyConfirm = owner.Get "/account/key"
        assertPage "connect extension" keyConfirm.Html [ "action=\"/account/key\"" ]
        let! keyShown = owner.PostForm("/account/key", "/account/key", [])
        let! keyHtml = keyShown.Content.ReadAsStringAsync()
        assertPage "sync key shown" (Net.WebUtility.HtmlDecode keyHtml) [ "data-new-sync-key" ]
        let! delete = owner.Get "/account/delete"
        assertPage "delete account" delete.Html [ "name=\"confirmUsername\"" ]

        let! dashboard = anonymous.Get "/u/animegolden"
        assertPage "dashboard" dashboard.Html [ "data-stat=\"series\""; "data-recent-item"; "data-dna-link"; "data-fr-time" ]
        let! year = anonymous.Get $"/u/animegolden/year/{DateTime.UtcNow.Year}"
        assertPage "year in review" year.Html [ "data-year=\""; "data-year-picker"; "data-rewatch-note"; "data-dashboard-link"; "data-dna-link" ]
        let! seriesId = Db.scalar<string> app.Db "SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY w.watched_at DESC, e.id LIMIT 1" []
        let! anime = anonymous.Get $"/u/animegolden/anime/{seriesId}"
        assertPage "anime page" anime.Html [ "data-anime=\""; "data-anime-totals"; "data-season="; "data-episode="; "data-dashboard-link"; "data-rewatch-note" ]
        // The episode page (feature 009): a thread with a spoiler and a comment the root hides, then the edit page
        let! episode = EpisodeHelpers.pickEpisode app.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL"
        let episodePath = EpisodeHelpers.episodeUrl episode.Series episode.Episode
        use! commenter = AuthHelpers.signedUp app "sub-commenter" "commenter"
        let! _ = EpisodeHelpers.postComment commenter episode.Series episode.Episode "a spoiler about the ending" true
        let! _ = EpisodeHelpers.postComment commenter episode.Series episode.Episode "a plain remark" false
        let! hiddenId = Db.scalar<string> app.Db "SELECT id FROM comments WHERE content = 'a plain remark'" []
        let! hidden = owner.PostForm(episodePath, $"/comments/{hiddenId}/hide", [])
        Assert.Equal(Net.HttpStatusCode.Found, hidden.StatusCode)
        let! episodeOut = anonymous.Get episodePath
        assertPage "episode page (signed out)" episodeOut.Html [ "data-episode-page=\""; "data-thread=\"2\""; "data-comment=\""; "data-spoiler"; "data-spoiler-summary"; "data-hidden-placeholder"; "data-signin-prompt" ]
        let! episodeOperator = owner.Get episodePath
        assertPage "episode page (operator)" episodeOperator.Html [ "data-episode-page=\""; "data-thread=\"2\""; "data-comment=\""; "data-spoiler"; "data-spoiler-summary"; "data-hidden"; "data-comment-unhide"; "data-moderation-note"; "data-comment-form"; "data-spoiler-toggle" ]
        let! editPage = commenter.Get $"/comments/{hiddenId}/edit"
        assertPage "edit comment" editPage.Html [ "data-comment-edit-page=\""; "data-comment-form"; "data-spoiler-toggle" ]
        let! dna = anonymous.Get "/u/animegolden/dna"
        assertPage "anime DNA" dna.Html [ "data-dna-totals"; "data-dashboard-link" ]
        // The review pages, as an operator: nothing has been matched here, so the list is empty
        let! review = owner.Get "/review/mal"
        assertPage "review (to review)" review.Html [ "data-review=\"open\""; "data-review-filter"; "data-review-empty" ]
        let! manual = owner.Get "/review/mal/manual"
        assertPage "review (manual)" manual.Html [ "data-review=\"manual\""; "data-review-empty" ]
        // The operators page, as the root: one operator listed, and a refusal notice
        let! _ = TestHelpers.createUser app.Db "animegolden2"
        let! _ = FrunchyBackend.Infrastructure.OperatorActions.add db (Some "animegolden") "animegolden2" FrunchyBackend.Infrastructure.OperatorActions.Server
        let! operators = owner.Get "/review/operators?msg=is-root"
        assertPage "operators" operators.Html [ "data-operators"; "data-operators-root=\"animegolden\""; "data-operator=\"animegolden2\""; "data-action=\"add\""; "data-action=\"remove\""; "data-operators-notice=\"is-root\"" ]
        let! empty = anonymous.Get "/u/nobody"
        assertPage "user not found" empty.Html []

        Assert.True(problems.Count = 0, String.Join(Environment.NewLine, problems))
    }

/// Feature 011: the compatibility card (a signed-in non-owner viewing a dashboard) and the compare page
[<Fact>]
let ``the compatibility card and the compare page are on the design system and keep their test hooks`` () =
    task {
        use! app = newApp ()
        use! viewer = AuthHelpers.signedUp app "sub-viewer" "viewer"
        use! owner = AuthHelpers.signedUp app "sub-owner2" "ownerb"
        let! viewerKey = AuthHelpers.connectExtension viewer
        let! ownerKey = AuthHelpers.connectExtension owner
        // the same 2026 capture for both (two profiles), so they share every series, genre and studio
        let! _ = TestHelpers.submitAccepted app viewerKey (load2026Sample ())
        let! _ = TestHelpers.submitAccepted app ownerKey (load2026SampleRaw () |> withProfile "profile-second")
        let! _ = TestHelpers.processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app

        let problems = ResizeArray<string>()
        let! card = viewer.Get "/u/ownerb"
        problems.AddRange(
            pageProblems
                "dashboard with the compatibility card"
                card.Html
                [ "data-compat-card=\"ownerb\""; "data-level-meter"; "data-level-name"; "data-shared-count"; "data-compare-link"; "data-shared-series" ]
        )

        let! compare = viewer.Get "/u/ownerb/compare"
        Assert.Equal(Net.HttpStatusCode.OK, compare.Status)
        problems.AddRange(
            pageProblems
                "compare page"
                compare.Html
                [ "data-compare=\"ownerb\""; "data-level-meter"; "data-level-name"; "data-compare-summary"; "data-genre-compare"
                  "data-viewer-coverage"; "data-owner-coverage"; "data-shared-series"; "data-shared-studio" ]
        )

        Assert.True(problems.Count = 0, String.Join(Environment.NewLine, problems))
    }

/// Feature 012: the three chart pages (week, season, all time) and the empty state
[<Fact>]
let ``the chart pages are on the design system and keep their test hooks`` () =
    task {
        let now = DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)

        let appAt db =
            let clock = FreshAuthTests.TestClock(Now = now)

            createAppWith db [ "Frunchy:Charts:MinPeople", "2" ] (fun b ->
                b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

        let problems = ResizeArray<string>()
        let tabs = [ "data-chart-tab=\"week\""; "data-chart-tab=\"season\""; "data-chart-tab=\"all\"" ]

        // No history at all: every chart shows its empty state
        let! emptyDb = newDatabase ()
        use! emptyApp = appAt emptyDb
        let anonymousEmpty = emptyApp.NewBrowser()

        for name, path, kind in [ "week", "/charts", "week"; "season", "/charts/season", "season"; "all time", "/charts/all", "all" ] do
            let! page = anonymousEmpty.Get path
            problems.AddRange(
                pageProblems $"charts ({name}, empty)" page.Html ([ $"data-charts=\"{kind}\""; "data-min-people=\"2\""; "data-chart-empty" ] @ tabs)
            )

        // Three people over two series, this week and this season; the signed-in viewer is one of them
        let! db = newDatabase ()
        use! app = appAt db
        use! viewer = AuthHelpers.signedUp app "sub-charts" "viewer"
        let! viewerId = Db.scalar<Guid> db "SELECT id FROM users WHERE username = 'viewer'" []
        let others = [ for n in 1..2 -> Guid.Parse $"00000000-0000-0000-0000-00000000000{n}" ]

        for n, id in List.indexed others do
            do! Db.execute db $"INSERT INTO users (id, username, display_name) VALUES ('{id}', 'other{n + 1}', 'Other {n + 1}')"

        do!
            Db.execute
                db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES
                     ('DSA00001', 'Alpha', 'alpha', now()), ('DSA00002', 'Beta', 'beta', now());
                   INSERT INTO cr_episodes (id, series_id, series_title, duration_ms) VALUES
                     ('DSAE1', 'DSA00001', 'Alpha', 1440000), ('DSAE2', 'DSA00001', 'Alpha', 1440000),
                     ('DSBE1', 'DSA00002', 'Beta', 1440000)"""

        let people = viewerId :: others

        // (person, episode, days before now): Alpha has 3 people and 4 episodes, Beta 2 people and 2 episodes
        for p, episode, daysAgo in [ 0, "DSAE1", 1.0; 0, "DSAE2", 2.0; 1, "DSAE1", 2.0; 2, "DSAE2", 3.0; 0, "DSBE1", 2.0; 1, "DSBE1", 3.0 ] do
            do!
                Db.execute
                    db
                    $"""INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id)
                        VALUES ('{people[p]}', '{episode}', '{now.AddDays(-daysAgo).UtcDateTime:O}', 1400, true, gen_random_uuid())"""

        let anonymous = app.NewBrowser()
        let rowHooks = [ "data-chart-row=\"DSA00001\""; "data-rank=\"1\""; "data-people=\"3\""; "data-episodes=\"4\"" ]
        let common kind = [ $"data-charts=\"{kind}\""; "data-min-people=\"2\"" ] @ tabs @ rowHooks

        let! week = anonymous.Get "/charts"
        problems.AddRange(pageProblems "charts (week)" week.Html (common "week" @ [ "data-move="; "data-trend=" ]))
        let! weekViewer = viewer.Get "/charts"
        problems.AddRange(pageProblems "charts (week, signed in)" weekViewer.Html (common "week" @ [ "data-move="; "data-trend="; "data-chart-mine" ]))
        let! season = anonymous.Get "/charts/season"
        problems.AddRange(pageProblems "charts (season)" season.Html (common "season" @ [ "data-season=\"fall-2026\"" ]))
        let! all = anonymous.Get "/charts/all"
        problems.AddRange(pageProblems "charts (all time)" all.Html (common "all"))

        // Feature 013: the row's title links to the series page, "Your history" only for a viewer who watched it
        problems.AddRange(pageProblems "charts (row links)" week.Html [ "data-series-page-link"; "href=\"/anime/DSA00001\"" ])

        Assert.True(problems.Count = 0, String.Join(Environment.NewLine, problems))
    }

/// Feature 013: the public series page (a sample series with a dub and MyAnimeList entries, a season over 30
/// known episodes, the 404)
[<Fact>]
let ``the series page is on the design system and keeps its test hooks`` () =
    task {
        use! app = newApp ()
        let! key = TestHelpers.createUser app.Db "animegolden"
        let! _ = TestHelpers.submitAccepted app key (load2026Sample ())
        let! _ = TestHelpers.processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app

        // a long season of 31 synthetic episodes in a series of its own
        do!
            Db.execute
                app.Db
                """INSERT INTO cr_series (id, title, slug, source_at) VALUES ('DSLONG01', 'Long Show', 'long-show', now());
                   INSERT INTO cr_seasons (id, series_id, season_number, title, audio_locale, source_at)
                     VALUES ('DSLONGS1', 'DSLONG01', 1, 'Long Show', 'ja-JP', now());
                   INSERT INTO cr_episodes (id, series_id, season_id, series_title, title, episode_number, duration_ms)
                     SELECT 'DSLONGE' || n, 'DSLONG01', 'DSLONGS1', 'Long Show', 'Episode ' || n, n, 1440000
                     FROM generate_series(1, 31) AS n"""

        // the series of the dubbed season: several seasons, a dub and matched entries, one episode with a comment
        let! dubbed = Db.scalar<string> app.Db "SELECT series_id FROM cr_seasons WHERE id = 'GRK5CNJ14'" []
        let! episode = Db.scalar<string> app.Db $"SELECT id FROM cr_episodes WHERE series_id = '{dubbed}' AND duration_ms IS NOT NULL ORDER BY id LIMIT 1" []
        use! commenter = AuthHelpers.signedUp app "sub-series-commenter" "seriescommenter"
        let! _ = EpisodeHelpers.postComment commenter dubbed episode "worth a look" false

        let problems = ResizeArray<string>()
        let anonymous = app.NewBrowser()
        let seriesHooks = [ "data-series-page=\""; "data-series-title"; "data-season="; "data-known="; "data-episode=\""; "data-comments=\""; "data-cr-series-link" ]

        let! page = anonymous.Get $"/anime/{dubbed}"
        Assert.Equal(Net.HttpStatusCode.OK, page.Status)
        problems.AddRange(pageProblems "series page" page.Html (seriesHooks @ [ "data-audio="; "data-dub"; "data-mal=\""; "data-mal-link"; "data-mal-compact" ]))

        let! long = anonymous.Get "/anime/DSLONG01"
        Assert.Equal(Net.HttpStatusCode.OK, long.Status)
        problems.AddRange(pageProblems "series page (long season)" long.Html (seriesHooks @ [ "data-season-collapsed" ]))

        let! missing = anonymous.Get "/anime/NOTHING1"
        Assert.Equal(Net.HttpStatusCode.NotFound, missing.Status)
        problems.AddRange(pageProblems "series page (404)" missing.Html [ "href=\"/\"" ])

        Assert.True(problems.Count = 0, String.Join(Environment.NewLine, problems))
    }

// ---------------------------------------------------------------------------
// The shared poster: a placeholder box, with the image on top only when there is a URL
// ---------------------------------------------------------------------------

[<Fact>]
let ``a poster without a URL is the placeholder box alone`` () =
    let html = Oxpecker.ViewEngine.Render.toString (Components.poster None "aspect-[2/3] w-32 rounded-md")
    Assert.DoesNotContain("<img", html)
    Assert.Contains("bg-surface-container-high", html)
    Assert.Contains("aspect-[2/3] w-32 rounded-md", html)

[<Fact>]
let ``a poster with a URL is decorative, lazy, and removes itself when it fails to load`` () =
    let html = Oxpecker.ViewEngine.Render.toString (Components.poster (Some "https://img.example/p.jpg") "aspect-video w-12")
    Assert.Contains("src=\"https://img.example/p.jpg\"", html)
    Assert.Contains("alt=\"\"", html)
    Assert.Contains("loading=\"lazy\"", html)
    Assert.Contains("onerror=\"this.remove()\"", html)
    Assert.Contains("bg-surface-container-high", html)
