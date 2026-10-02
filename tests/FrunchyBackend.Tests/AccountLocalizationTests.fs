module FrunchyBackend.Tests.AccountLocalizationTests

open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private pagesFor (app: TestApp) (language: string) =
    task {
        // signed-out pages
        use anonymous = app.NewBrowser language
        let! signin = anonymous.Get "/signin"
        let! signup = signInWith anonymous "visitor"
        let! claim = anonymous.Get "/claim"

        // signed-in pages
        let! invite = createInvite app
        use member' = app.NewBrowser language
        let! _ = submitSignUp member' "member-identity" invite "member" "M"
        let! account = member'.Get "/account"
        let! key = member'.Get "/account/key"
        let! delete = member'.Get "/account/delete"
        return [ signin; signup; claim; account; key; delete ]
    }

[<Fact>]
let ``every account page renders in Spanish`` () =
    task {
        use! app = newApp ()
        let! pages = pagesFor app "es-ES,es;q=0.9"

        let expected =
            [ "Iniciar sesión con Test"; "Crea tu cuenta"; "Ya tengo una cuenta"; "Tu cuenta"; "Conecta la extensión"; "Elimina tu cuenta" ]

        for page, text in List.zip pages expected do
            Assert.Contains("lang=\"es\"", page.Html)
            Assert.Contains(text, page.Html)
    }

[<Fact>]
let ``unsupported languages get the English pages`` () =
    task {
        use! app = newApp ()
        let! pages = pagesFor app "fr-FR"

        let expected =
            [ "Sign in with Test"; "Create your account"; "I already have an account"; "Your account"; "Connect the extension"; "Delete your account" ]

        for page, text in List.zip pages expected do
            Assert.Contains("lang=\"en\"", page.Html)
            Assert.Contains(text, page.Html)
    }

[<Fact>]
let ``the rate-limit page is localized`` () =
    task {
        use! app = newApp ()
        use browser = app.NewBrowser "es"
        let! _ = signInWith browser "guesser"
        let mutable last = ""

        for i in 1..6 do
            let! response = browser.PostForm("/claim", "/claim", [ "syncKey", $"frk_{i}" ])
            let! html = response.Content.ReadAsStringAsync()
            last <- System.Net.WebUtility.HtmlDecode html

        Assert.Contains("Demasiados intentos", last)
    }
