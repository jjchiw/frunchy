/// Feature 024: the language preference cookie (`frunchy.lang`), the `/language/{code}` link that sets it, and the
/// footer link to the other language. Tests read `data-*` hooks and attributes, never classes.
module FrunchyBackend.Tests.LanguageSwitchTests

open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// A GET with an optional Accept-Language and an optional raw Cookie header
let private send (app: TestApp) (language: string option) (cookie: string option) (path: string) =
    let request = new HttpRequestMessage(HttpMethod.Get, path)
    language |> Option.iter (fun l -> request.Headers.AcceptLanguage.ParseAdd l)
    cookie |> Option.iter (fun c -> request.Headers.Add("Cookie", c))
    app.Client.SendAsync request

let private location (response: HttpResponseMessage) = response.Headers.Location.OriginalString

let private langCookie (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values -> values |> Seq.tryFind (fun c -> c.StartsWith "frunchy.lang=")
    | _ -> None

let private switchNav (html: string) =
    let nav = Regex.Match(html, "<nav\\b([^>]*\\bdata-language-switch\\b[^>]*)>(.*?)</nav>", RegexOptions.Singleline)
    Assert.True(nav.Success, "the page has the language switch")
    nav

/// The footer's language links, in order: href, hreflang, lang, data-language, aria-current and the text
let private switchLinks (html: string) =
    let nav = switchNav html

    [ for link in Regex.Matches(nav.Groups[2].Value, "<a\\b([^>]*)>(.*?)</a>", RegexOptions.Singleline) do
          let tag = link.Groups[1].Value
          let attr name = Regex.Match(tag, $"(?<![-\\w]){name}=\"([^\"]*)\"").Groups[1].Value
          let text = Regex.Replace(link.Groups[2].Value, "<[^>]+>", "").Trim()
          attr "href", attr "hreflang", attr "lang", attr "data-language", attr "aria-current", text ]

/// The link to one language
let private switchLink (html: string) (code: string) =
    let href, hreflang, lang, dataLanguage, _, text =
        switchLinks html |> List.find (fun (_, _, _, language, _, _) -> language = code)

    href, hreflang, lang, dataLanguage, text

let private switchAriaLabel (html: string) =
    Regex.Match((switchNav html).Groups[1].Value, "aria-label=\"([^\"]*)\"").Groups[1].Value

[<Theory>]
[<InlineData("es", "/charts?x=1")>]
[<InlineData("en", "/")>]
let ``the language link sets the cookie and goes back to a local page`` (code: string, returnUrl: string) =
    task {
        use! app = newApp ()
        let! response = send app None None $"/language/{code}?returnUrl={Uri.EscapeDataString returnUrl}"
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal(returnUrl, location response)

        let cookie = (langCookie response).Value.ToLowerInvariant()
        Assert.StartsWith($"frunchy.lang={code};", cookie)
        Assert.Contains("path=/", cookie)
        Assert.Contains("samesite=lax", cookie)
        Assert.Contains("httponly", cookie)
        Assert.Contains("max-age=31536000", cookie)
        // plain http in the test: no Secure
        Assert.DoesNotContain("secure", cookie)
    }

[<Fact>]
let ``the cookie is Secure on https`` () =
    task {
        use! app = newApp ()
        let request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/language/es")
        let! response = app.Client.SendAsync request
        Assert.Contains("secure", (langCookie response).Value.ToLowerInvariant())
    }

[<Theory>]
[<InlineData("https://evil.example")>]
[<InlineData("//evil.example")>]
[<InlineData("/\\evil.example")>]
[<InlineData("")>]
let ``the language link never goes to another site`` (returnUrl: string) =
    task {
        use! app = newApp ()
        let! response = send app None None $"/language/es?returnUrl={Uri.EscapeDataString returnUrl}"
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/", location response)
        Assert.True((langCookie response).IsSome)
    }

[<Fact>]
let ``no returnUrl goes home`` () =
    task {
        use! app = newApp ()
        let! response = send app None None "/language/en"
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/", location response)
    }

[<Theory>]
[<InlineData("fr")>]
[<InlineData("EN")>]
[<InlineData("english")>]
[<InlineData("es-ES")>]
let ``an unknown language is the plain 404 and sets no cookie`` (code: string) =
    task {
        use! app = newApp ()
        let! response = send app None None $"/language/{code}"
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        Assert.False(response.Headers.Contains "Set-Cookie")
    }

[<Fact>]
let ``the cookie wins over Accept-Language, both ways`` () =
    task {
        use! app = newApp ()
        let! spanish = send app (Some "en-US,en;q=0.9") (Some "frunchy.lang=es") "/"
        let! html = readHtml spanish
        Assert.Contains("lang=\"es\"", html)
        Assert.Contains("Cómo sincronizar", html)

        let! english = send app (Some "es-ES,es;q=0.9") (Some "frunchy.lang=en") "/"
        let! html = readHtml english
        Assert.Contains("lang=\"en\"", html)
        Assert.Contains("How to sync", html)
    }

[<Fact>]
let ``without a cookie Accept-Language decides as before`` () =
    task {
        use! app = newApp ()
        let! spanish = send app (Some "es-ES,es;q=0.9") None "/"
        let! html = readHtml spanish
        Assert.Contains("lang=\"es\"", html)
        let! plain = send app None None "/"
        let! html = readHtml plain
        Assert.Contains("lang=\"en\"", html)
    }

[<Theory>]
[<InlineData("fr")>]
[<InlineData("ES")>]
[<InlineData("")>]
[<InlineData("es-ES")>]
let ``an invalid cookie value is ignored`` (value: string) =
    task {
        use! app = newApp ()
        // Accept-Language still decides
        let! response = send app (Some "es-ES,es;q=0.9") (Some $"frunchy.lang={value}") "/"
        let! html = readHtml response
        Assert.Contains("lang=\"es\"", html)

        let! response = send app (Some "en-US") (Some $"frunchy.lang={value}") "/"
        let! html = readHtml response
        Assert.Contains("lang=\"en\"", html)
    }

[<Fact>]
let ``switching sticks: the cookie from the link keeps the next page in that language`` () =
    task {
        use! app = newApp ()
        let! set = send app (Some "en-US") None "/language/es?returnUrl=%2Fcharts"
        let cookie = (langCookie set).Value.Split(';').[0]
        let! response = send app (Some "en-US") (Some cookie) (location set)
        let! html = readHtml response
        Assert.Contains("lang=\"es\"", html)
    }

[<Theory>]
[<InlineData("/", "en", "/language/es?returnUrl=%2F", "es", "Español", "Language")>]
[<InlineData("/charts?week=1", "en", "/language/es?returnUrl=%2Fcharts%3Fweek%3D1", "es", "Español", "Language")>]
[<InlineData("/nothing-here?a=b&c=d", "en", "/language/es?returnUrl=%2Fnothing-here%3Fa%3Db%26c%3Dd", "es", "Español", "Language")>]
[<InlineData("/", "es", "/language/en?returnUrl=%2F", "en", "English", "Idioma")>]
[<InlineData("/charts?week=1", "es", "/language/en?returnUrl=%2Fcharts%3Fweek%3D1", "en", "English", "Idioma")>]
[<InlineData("/nothing-here", "es", "/language/en?returnUrl=%2Fnothing-here", "en", "English", "Idioma")>]
let ``the footer offers the other language in its own name and comes back to this page``
    (path: string, pageLanguage: string, href: string, target: string, name: string, label: string)
    =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client pageLanguage path
        let! html = readHtml response
        let linkHref, hreflang, lang, dataLanguage, text = switchLink html target
        Assert.Equal(href, linkHref)
        Assert.Equal(target, hreflang)
        Assert.Equal(target, lang)
        Assert.Equal(target, dataLanguage)
        Assert.Equal(name, text)
        Assert.Equal(label, switchAriaLabel html)
    }

[<Theory>]
[<InlineData("en")>]
[<InlineData("es")>]
let ``the footer shows both languages in their own names, the page's one selected`` (pageLanguage: string) =
    task {
        use! app = newApp ()
        let! response = getWithLanguage app.Client pageLanguage "/charts"
        let! html = readHtml response
        let links = switchLinks html

        // English then Español whatever the page's language: the names never change with it
        Assert.Equal<string list>([ "en"; "es" ], links |> List.map (fun (_, _, _, language, _, _) -> language))
        Assert.Equal<string list>([ "English"; "Español" ], links |> List.map (fun (_, _, _, _, _, text) -> text))

        // exactly one selected, and it's the page's language
        let selected = links |> List.filter (fun (_, _, _, _, current, _) -> current = "true")
        Assert.Equal(1, selected.Length)
        let _, _, _, selectedLanguage, _, _ = selected.Head
        Assert.Equal(pageLanguage, selectedLanguage)
    }

[<Fact>]
let ``the switch link round-trips through the route`` () =
    task {
        use! app = newApp ()
        let! page = getWithLanguage app.Client "en" "/charts?week=1"
        let! html = readHtml page
        let href, _, _, _, _ = switchLink html "es"
        let! response = send app (Some "en") None href
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode)
        Assert.Equal("/charts?week=1", location response)
    }
