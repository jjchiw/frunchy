/// Feature 032: the theme preference cookie (`frunchy.theme`), the `/theme/{mode}` link that sets it, `<html
/// data-theme>` and the footer switch. Tests read `data-*` hooks and attributes, never classes.
module FrunchyBackend.Tests.ThemeSwitchTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Views
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private send (app: TestApp) (cookie: string option) (path: string) =
    let request = new HttpRequestMessage(HttpMethod.Get, path)
    cookie |> Option.iter (fun c -> request.Headers.Add("Cookie", c))
    app.Client.SendAsync request

let private location (response: HttpResponseMessage) = response.Headers.Location.OriginalString

let private themeCookie (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values -> values |> Seq.tryFind (fun c -> c.StartsWith "frunchy.theme=")
    | _ -> None

let private htmlTheme (html: string) =
    Regex.Match(html, "<html\\b[^>]*\\bdata-theme=\"([^\"]*)\"").Groups[1].Value

let private switchNav (html: string) =
    let nav = Regex.Match(html, "<nav\\b([^>]*\\bdata-theme-switch\\b[^>]*)>(.*?)</nav>", RegexOptions.Singleline)
    Assert.True(nav.Success, "the page has the theme switch")
    nav

/// (mode, href, aria-current present, text) of every option of the footer switch
let private options (html: string) =
    [ for m in Regex.Matches((switchNav html).Groups[2].Value, "<a\\b([^>]*)>(.*?)</a>", RegexOptions.Singleline) ->
          let tag = m.Groups[1].Value
          let attr name = Regex.Match(tag, $"(?<![-\\w]){name}=\"([^\"]*)\"").Groups[1].Value
          let text = Regex.Replace(Regex.Replace(m.Groups[2].Value, "<span\\b[^>]*material-symbols-outlined[^>]*>.*?</span>", ""), "<[^>]+>", "").Trim()
          attr "data-theme-option", attr "href", Regex.IsMatch(tag, "aria-current=\"true\""), text ]

[<Theory>]
[<InlineData("light", "/charts?x=1")>]
[<InlineData("dark", "/")>]
[<InlineData("system", "/charts")>]
let ``the theme link sets the cookie and goes back to a local page`` (mode: string, returnUrl: string) =
    task {
        use! app = newApp ()
        let! response = send app None $"/theme/{mode}?returnUrl={Uri.EscapeDataString returnUrl}"
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal(returnUrl, location response)

        let cookie = (themeCookie response).Value.ToLowerInvariant()
        Assert.StartsWith($"frunchy.theme={mode};", cookie)
        Assert.Contains("path=/", cookie)
        Assert.Contains("samesite=lax", cookie)
        Assert.Contains("httponly", cookie)
        Assert.Contains("max-age=31536000", cookie)
        Assert.DoesNotContain("secure", cookie)
    }

[<Fact>]
let ``the cookie is Secure on https`` () =
    task {
        use! app = newApp ()
        let! response = app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://localhost/theme/light"))
        Assert.Contains("secure", (themeCookie response).Value.ToLowerInvariant())
    }

[<Theory>]
[<InlineData("https://evil.example")>]
[<InlineData("//evil.example")>]
[<InlineData("/\\evil.example")>]
[<InlineData("")>]
let ``the theme link never goes to another site`` (returnUrl: string) =
    task {
        use! app = newApp ()
        let! response = send app None $"/theme/dark?returnUrl={Uri.EscapeDataString returnUrl}"
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/", location response)
        Assert.True((themeCookie response).IsSome)
    }

[<Theory>]
[<InlineData("blue")>]
[<InlineData("Light")>]
[<InlineData("DARK")>]
[<InlineData("auto")>]
let ``an unknown mode is the plain 404 and sets no cookie`` (mode: string) =
    task {
        use! app = newApp ()
        let! response = send app None $"/theme/{mode}"
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.False(response.Headers.Contains "Set-Cookie")
    }

[<Theory>]
[<InlineData("light", "light")>]
[<InlineData("dark", "dark")>]
[<InlineData("system", "system")>]
[<InlineData("Light", "system")>]
[<InlineData("blue", "system")>]
[<InlineData("", "system")>]
let ``a page renders data-theme from the cookie, system when missing or invalid`` (value: string, expected: string) =
    task {
        use! app = newApp ()
        let! response = send app (Some $"frunchy.theme={value}") "/"
        let! html = readHtml response
        Assert.Equal(expected, htmlTheme html)
    }

[<Fact>]
let ``without a cookie the page follows the system`` () =
    task {
        use! app = newApp ()
        let! response = send app None "/"
        let! html = readHtml response
        Assert.Equal("system", htmlTheme html)
        Assert.Contains("<meta name=\"color-scheme\" content=\"dark light\"", html)
        // two theme-color metas, one per scheme, with the surface colour of each palette
        Assert.Matches("<meta name=\"theme-color\" content=\"#efe8dd\" media=\"\\(prefers-color-scheme: light\\)\"", html)
        Assert.Matches("<meta name=\"theme-color\" content=\"#13111a\" media=\"\\(prefers-color-scheme: dark\\)\"", html)
    }

[<Theory>]
[<InlineData("light", "light", "#efe8dd")>]
[<InlineData("dark", "dark", "#13111a")>]
let ``a forced theme has one color-scheme and one theme-color`` (mode: string, scheme: string, surface: string) =
    task {
        use! app = newApp ()
        let! response = send app (Some $"frunchy.theme={mode}") "/"
        let! html = readHtml response
        Assert.Contains($"<meta name=\"color-scheme\" content=\"{scheme}\"", html)
        Assert.Equal(1, Regex.Matches(html, "<meta name=\"theme-color\"").Count)
        Assert.Contains($"<meta name=\"theme-color\" content=\"{surface}\"", html)
        Assert.DoesNotContain("name=\"theme-color\" content=\"" + surface + "\" media", html)
    }

[<Theory>]
[<InlineData("/", "en", "Theme", "Light,Dark,System")>]
[<InlineData("/charts?week=1", "en", "Theme", "Light,Dark,System")>]
[<InlineData("/", "es", "Tema", "Claro,Oscuro,Sistema")>]
[<InlineData("/nothing-here?a=b&c=d", "es", "Tema", "Claro,Oscuro,Sistema")>]
let ``the footer offers the three modes and comes back to this page`` (path: string, language: string, label: string, words: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language path
        let! html = readHtml response
        let listed = options html
        Assert.Equal<string list>([ "light"; "dark"; "system" ], listed |> List.map (fun (mode, _, _, _) -> mode))
        Assert.Equal<string list>(words.Split ',' |> List.ofArray, listed |> List.map (fun (_, _, _, text) -> text))

        for mode, href, _, _ in listed do
            Assert.Equal($"/theme/{mode}?returnUrl={Uri.EscapeDataString path}", href)

        Assert.Equal(label, Regex.Match((switchNav html).Groups[1].Value, "aria-label=\"([^\"]*)\"").Groups[1].Value)
        // without a cookie the current mode is System
        Assert.Equal<string list>([ "system" ], listed |> List.filter (fun (_, _, current, _) -> current) |> List.map (fun (m, _, _, _) -> m))
    }

[<Theory>]
[<InlineData("light")>]
[<InlineData("dark")>]
[<InlineData("system")>]
let ``exactly the current mode is marked aria-current`` (mode: string) =
    task {
        use! app = newApp ()
        let! response = send app (Some $"frunchy.theme={mode}") "/"
        let! html = readHtml response
        let current = options html |> List.filter (fun (_, _, current, _) -> current)
        Assert.Equal(1, current.Length)
        Assert.Equal(mode, (let m, _, _, _ = current.Head in m))
    }

[<Fact>]
let ``the switch is labels only, no icons`` () =
    task {
        use! app = newApp ()
        let! response = send app None "/"
        let! html = readHtml response
        Assert.DoesNotContain("material-symbols-outlined", (switchNav html).Groups[2].Value)
        Assert.DoesNotContain("light_mode", html)
    }

[<Fact>]
let ``the switch sits next to the language switch in the footer`` () =
    task {
        use! app = newApp ()
        let! response = send app None "/"
        let! html = readHtml response
        let footer = html.Substring(html.IndexOf "<footer")
        Assert.Contains("data-theme-switch", footer)
        Assert.Contains("data-language-switch", footer)
    }

[<Fact>]
let ``switching sticks: the cookie from the link keeps the next page in that theme`` () =
    task {
        use! app = newApp ()
        let! set = send app None "/theme/light?returnUrl=%2Fcharts"
        let cookie = (themeCookie set).Value.Split(';').[0]
        let! response = send app (Some cookie) (location set)
        let! html = readHtml response
        Assert.Equal("light", htmlTheme html)
    }

[<Fact>]
let ``the theme CSS has dark on root, light for data-theme light and for system while the system is light`` () =
    let css = Theme.tokensCss
    Assert.StartsWith(":root { color-scheme: dark;", css)
    Assert.Contains(":root[data-theme=\"light\"] { color-scheme: light;", css)
    Assert.Contains("@media (prefers-color-scheme: light) { :root[data-theme=\"system\"] { color-scheme: light;", css)

    // both light blocks carry every light colour and effect
    // (a token whose value is the same in both themes, such as on-primary, also appears once in the dark block)
    let dark = Theme.tokens @ Theme.darkExtraTokens

    for name, value in Theme.lightTokens do
        let inDark = if List.contains (name, value) dark then 1 else 0
        Assert.Equal(2 + inDark, Regex.Matches(css, Regex.Escape $"{name}: {value};").Count)

    // the dark block is untouched: every token of the extension-shared list is in it
    for name, value in Theme.tokens @ Theme.darkExtraTokens do
        Assert.Contains($"{name}: {value};", css)

[<Fact>]
let ``the Tailwind colours resolve to the custom properties, never a hex colour`` () =
    let config = Theme.tailwindConfig
    let colors = Regex.Match(config, "colors: \\{(.*?)\\},\\s*\\n\\s*fontSize", RegexOptions.Singleline).Groups[1].Value
    Assert.NotEmpty colors
    Assert.DoesNotContain("#", colors)

    // the three Frunchy-only fill tokens too (bg-primary-fill, bg-primary-fill/[0.14], ...)
    for name, _ in Theme.colors @ Theme.fillColors do
        Assert.Contains($"'{name}': 'color-mix(in srgb, var(--color-{name}) calc(<alpha-value> * 100%%), transparent)'", colors)

[<Fact>]
let ``the page body, mark and selection carry no hex colour`` () =
    Assert.DoesNotContain("#", Theme.markSvg)
    // the frame follows `--mark-frame`, the bars the fills, the slats the surface
    Assert.Contains("var(--mark-frame)", Theme.markSvg)
    Assert.Contains("var(--color-primary-fill)", Theme.markSvg)
    Assert.Contains("var(--color-secondary-fill)", Theme.markSvg)
    Assert.Contains("var(--color-surface)", Theme.markSvg)
    Assert.DoesNotContain("rgb(255 181 71", Theme.componentCss)
    // a solid area and its tints take the fill, never the text role
    Assert.Contains(".fr-btn-primary { @apply bg-primary-fill text-on-primary; }", Theme.componentCss)
    Assert.DoesNotContain("bg-primary ", Theme.componentCss)
    Assert.DoesNotContain("bg-primary/", Theme.componentCss)
    // the favicon is a data URI that cannot see the page's custom properties: it keeps the dark literals
    let favicon = Text.Encoding.UTF8.GetString(Convert.FromBase64String(Theme.faviconHref.Substring("data:image/svg+xml;base64,".Length)))
    Assert.Contains("#ffb547", favicon)
    Assert.DoesNotContain("var(", favicon)
