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

[<Fact>]
let ``unknown api path returns the JSON error shape`` () =
    task {
        use! app = newApp ()
        let! response = app.Client.GetAsync "/api/nothing-here"
        let! body = response.Content.ReadAsStringAsync()
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.Contains("\"error\":\"not_found\"", body)
    }
