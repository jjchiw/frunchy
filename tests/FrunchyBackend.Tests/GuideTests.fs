/// The install guide (/guide): public, in both languages, with the screenshots served from wwwroot/guide
module FrunchyBackend.Tests.GuideTests

open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private sectionIds = [ "install"; "sign-up"; "connect"; "sync"; "dashboard"; "visibility"; "delete" ]

let private imageNames =
    [ "signup"
      "account-extension"
      "sync-key"
      "extension-options"
      "extension-ready"
      "extension-scraping"
      "extension-synced"
      "dashboard"
      "dna"
      "account-visibility" ]

/// The opening tags of the elements carrying a data hook
let private tagsWith (hook: string) (html: string) =
    Regex.Matches(html, $"<\\w+\\b[^>]*\\bdata-{hook}\\b[^>]*>") |> Seq.map _.Value |> List.ofSeq

let private attr (name: string) (tag: string) =
    Regex.Match(tag, $"(?<![-\\w]){name}=\"([^\"]*)\"").Groups[1].Value

let private imgTags (html: string) =
    // Each figure holds one img: the hook is on the figure, the img follows it
    Regex.Matches(html, "<figure\\b[^>]*\\bdata-guide-image=\"([^\"]+)\"[^>]*>\\s*(<img\\b[^>]*>)")
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> List.ofSeq

[<Theory>]
[<InlineData("en-US", "en")>]
[<InlineData("es-ES", "es")>]
let ``the guide is public, has its sections in order and links the Chrome Web Store`` (language: string, lang: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language "/guide"
        let! html = readHtml response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        Assert.Contains($"lang=\"{lang}\"", html)

        let ids = tagsWith "guide-section" html |> List.map (attr "data-guide-section")
        Assert.Equal<string list>(sectionIds, ids)

        // Every section has an anchor the contents link to
        for id in sectionIds do
            Assert.Contains($"id=\"{id}\"", html)
            Assert.Contains($"href=\"#{id}\"", html)

        let extension = tagsWith "guide-extension" html |> List.exactlyOne
        Assert.Contains("href=\"https://chromewebstore.google.com/detail/frunchy/hfbjnojnefgpdnloekbgekeaakpobjip\"", extension)
    }

[<Theory>]
[<InlineData("en-US", "en")>]
[<InlineData("es-ES", "es")>]
let ``every guide screenshot has an alt text and a picture of the page's language`` (language: string, lang: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language "/guide"
        let! html = readHtml response
        let images = imgTags html

        Assert.Equal<string list>(imageNames |> List.sort, images |> List.map fst |> List.sort)

        // Collect what is wrong so a missing picture doesn't hide the others
        let problems = System.Collections.Generic.List<string>()

        for name, tag in images do
            if System.String.IsNullOrWhiteSpace(attr "alt" tag) then
                problems.Add $"{name} has no alt text"

            let src = attr "src" tag

            if src <> $"/guide/{name}-{lang}.png" then
                problems.Add $"{name} has the src {src}"

            let! picture = app.Client.GetAsync src

            if picture.StatusCode <> HttpStatusCode.OK then
                problems.Add $"{src} answered {picture.StatusCode}"
            elif picture.Content.Headers.ContentType.MediaType <> "image/png" then
                problems.Add $"{src} is {picture.Content.Headers.ContentType.MediaType}, not image/png"
            elif not ((string picture.Headers.CacheControl).Contains "max-age=86400") then
                problems.Add $"{src} has no day-long cache header"

        Assert.True(problems.Count = 0, String.concat "; " problems)
    }

[<Theory>]
[<InlineData("en-US", "Getting started", "Primeros pasos")>]
[<InlineData("es-ES", "Primeros pasos", "Getting started")>]
let ``the guide is written in the page's language`` (language: string, expected: string, other: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language "/guide"
        let! html = readHtml response
        Assert.Contains($"<h1", html)
        Assert.Contains(expected, html)
        Assert.DoesNotContain(other, html)
    }

[<Fact>]
let ``the home page links the guide`` () =
    task {
        use! app = newApp ()
        let! response = app.Client.GetAsync "/"
        let! html = readHtml response
        let link = tagsWith "guide-link" html |> List.exactlyOne
        Assert.Equal("/guide", attr "href" link)
    }

[<Fact>]
let ``the sign-up page links the guide`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser()
        let! page = signInWith browser "guide-newcomer"
        Assert.Equal("/signup", page.Path)
        let link = tagsWith "guide-link" page.Html |> List.exactlyOne
        Assert.Equal("/guide", attr "href" link)
    }

[<Fact>]
let ``a file that is not in wwwroot gets the plain 404 page`` () =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client "en" "/guide/nope.png"
        let! html = readHtml response
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.Contains("Page not found", html)
    }
