module FrunchyBackend.Tests.DesignSystemTests

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 004: the shared design system (contracts/design-system.md, research R13, R14)

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
    for s in [ "surface"; "surface-container-low" ] do
        let ratio = contrast (color "outline") (color s)
        Assert.True(ratio >= 3.0, $"outline on {s}: {ratio:F2} < 3")

[<Fact>]
let ``the icon font is subset to exactly the icons the views can use`` () =
    let names = Theme.iconNames |> List.sort |> String.concat ","
    Assert.Contains($"icon_names={names}", Theme.fontStylesheets |> String.concat " ")
    Assert.Equal(Theme.iconNames.Length, Theme.iconNames |> List.distinct |> List.length)

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
        Assert.Contains("family=Plus+Jakarta+Sans", html)
        Assert.Contains("--color-primary: #4cd7f6;", html)
        Assert.Contains("tailwind.config", html)
        Assert.Contains("<main id=\"main\"", html)
        Assert.Contains("href=\"#main\"", html)
    }

// ---------------------------------------------------------------------------
// Every page: on the design system, with its test hooks (T015, contracts/design-system.md)
// ---------------------------------------------------------------------------

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
      if not (html.Contains "--color-primary: #4cd7f6;") then
          yield $"{name}: not on the design system layout"
      for hook in hooks do
          if not (html.Contains hook) then
              yield $"{name}: missing {hook}" ]

[<Fact>]
let ``every page is on the design system and keeps its test hooks`` () =
    task {
        // the owner is also the root operator, so the review pages can be checked
        let! db = newDatabase ()
        use! app = createAppWith db [ "Frunchy:RootOperator", "animenerd" ] ignore
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
        use! owner = AuthHelpers.signedUp app "sub-owner" "animenerd"
        let! key = AuthHelpers.connectExtension owner
        let! _ = TestHelpers.submitAccepted app key (load2026Sample ())
        let! _ = TestHelpers.processAll app.Db
        let! account = owner.Get "/account"
        assertPage "account" account.Html [ "data-account"; "data-key-active"; "data-identity=\"test\""; "data-link-provider=\"test2\""; "data-security-event"; "data-sync"; "data-review-link" ]
        let! keyConfirm = owner.Get "/account/key"
        assertPage "connect extension" keyConfirm.Html [ "action=\"/account/key\"" ]
        let! keyShown = owner.PostForm("/account/key", "/account/key", [])
        let! keyHtml = keyShown.Content.ReadAsStringAsync()
        assertPage "sync key shown" (Net.WebUtility.HtmlDecode keyHtml) [ "data-new-sync-key" ]
        let! delete = owner.Get "/account/delete"
        assertPage "delete account" delete.Html [ "name=\"confirmUsername\"" ]

        let! dashboard = anonymous.Get "/u/animenerd"
        assertPage "dashboard" dashboard.Html [ "data-stat=\"series\""; "data-recent-item"; "data-dna-link"; "data-fr-time" ]
        let! year = anonymous.Get $"/u/animenerd/year/{DateTime.UtcNow.Year}"
        assertPage "year in review" year.Html [ "data-year=\""; "data-year-picker"; "data-rewatch-note"; "data-dashboard-link"; "data-dna-link" ]
        let! seriesId = Db.scalar<string> app.Db "SELECT e.series_id FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id WHERE e.duration_ms IS NOT NULL ORDER BY w.watched_at DESC, e.id LIMIT 1" []
        let! anime = anonymous.Get $"/u/animenerd/anime/{seriesId}"
        assertPage "anime page" anime.Html [ "data-anime=\""; "data-anime-totals"; "data-season="; "data-episode="; "data-dashboard-link"; "data-rewatch-note" ]
        let! dna = anonymous.Get "/u/animenerd/dna"
        assertPage "anime DNA" dna.Html [ "data-dna-totals"; "data-dashboard-link" ]
        // The review pages, as an operator: nothing has been matched here, so the list is empty
        let! review = owner.Get "/review/mal"
        assertPage "review (to review)" review.Html [ "data-review=\"open\""; "data-review-filter"; "data-review-empty" ]
        let! manual = owner.Get "/review/mal/manual"
        assertPage "review (manual)" manual.Html [ "data-review=\"manual\""; "data-review-empty" ]
        let! empty = anonymous.Get "/u/nobody"
        assertPage "user not found" empty.Html []

        Assert.True(problems.Count = 0, String.Join(Environment.NewLine, problems))
    }
