module FrunchyBackend.Tests.HomeTests

open System.Net
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``home is Spanish for a Spanish browser`` () =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client "es-ES,es;q=0.9,en;q=0.8" "/"
        let! html = readHtml response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        Assert.Contains("lang=\"es\"", html)
        Assert.Contains("Cómo sincronizar", html)
    }

[<Theory>]
[<InlineData("en-US")>]
[<InlineData("fr-FR")>]
let ``home is English for English and unsupported languages`` (language: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language "/"
        let! html = readHtml response
        Assert.Contains("lang=\"en\"", html)
        Assert.Contains("How to sync", html)
    }

[<Theory>]
[<InlineData("/dashboard")>]
[<InlineData("/profile")>]
let ``placeholder pages are gone`` (path: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client "es" path
        let! html = readHtml response
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.Contains("Página no encontrada", html)
    }

[<Theory>]
[<InlineData("en-US", "Frunchy is not affiliated with Crunchyroll or MyAnimeList.")>]
[<InlineData("es-ES", "Frunchy no está afiliado a Crunchyroll ni a MyAnimeList.")>]
let ``every page says Frunchy is not affiliated with Crunchyroll`` (language: string, line: string) =
    task {
        use! app = newApp ()
        for path in [ "/"; "/charts"; "/nothing-here" ] do
            let! response = getWithLanguage app.Client language path
            let! html = readHtml response
            Assert.Contains("data-footer-disclaimer", html)
            Assert.Contains(line, html)
    }

[<Theory>]
[<InlineData("en-US", "Get the Chrome extension", "Frunchy is open source:")>]
[<InlineData("es-ES", "Instala la extensión de Chrome", "Frunchy es de código abierto:")>]
let ``home links the Chrome extension and the source code`` (language: string, extension: string, source: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client language "/"
        let! html = readHtml response

        // The opening tag of the link carrying a hook
        let tagOf (hook: string) =
            let m = System.Text.RegularExpressions.Regex.Match(html, $"<a [^>]*data-{hook}[^>]*>")
            Assert.True(m.Success, $"no link with data-{hook}")
            m.Value

        let extensionTag = tagOf "home-extension"
        let sourceTag = tagOf "home-source"
        Assert.Contains("href=\"https://chromewebstore.google.com/detail/frunchy/hfbjnojnefgpdnloekbgekeaakpobjip\"", extensionTag)
        Assert.Contains("href=\"https://github.com/jjchiw/frunchy\"", sourceTag)

        // The store opens in the same tab (a destination); the source link opens a new one from prose
        Assert.DoesNotContain("target=", extensionTag)
        Assert.Contains("target=\"_blank\"", sourceTag)
        Assert.Contains("rel=\"noopener\"", sourceTag)

        Assert.Contains(extension, html)
        Assert.Contains(source, html)
    }

[<Fact>]
let ``unknown api path returns the JSON error shape`` () =
    task {
        use! app = newApp ()
        let! response = app.Client.GetAsync "/api/nothing-here"
        let! body = response.Content.ReadAsStringAsync()
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.Contains("\"error\":\"not_found\"", body)
    }
