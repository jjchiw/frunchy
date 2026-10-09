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
