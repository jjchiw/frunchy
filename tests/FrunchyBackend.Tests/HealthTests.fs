/// Feature 016 (US2): GET /healthz answers from the app and its database only
module FrunchyBackend.Tests.HealthTests

open System
open System.Diagnostics
open System.Net
open System.Net.Http
open Xunit
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// A port nothing listens on, so a connection is refused at once
let private closedPortConnection =
    "Host=127.0.0.1;Port=1;Username=postgres;Password=not-a-real-password;Database=frunchydb;Timeout=5"

let private assertPlainAnswer (status: HttpStatusCode) (body: string) (response: HttpResponseMessage) =
    task {
        let! text = response.Content.ReadAsStringAsync()
        Assert.Equal(status, response.StatusCode)
        Assert.Equal(body, text)
        Assert.Equal("text/plain; charset=utf-8", string response.Content.Headers.ContentType)
        Assert.Equal("no-store", string response.Headers.CacheControl)
        Assert.False(response.Headers.Contains "Set-Cookie", "the health check must not set a cookie")
        Assert.Null response.Headers.Location
    }

[<Fact>]
let ``healthz answers 200 ok as plain text, never cached and without a cookie`` () =
    task {
        use! app = newApp ()
        let! response = app.Client.GetAsync "/healthz"
        do! assertPlainAnswer HttpStatusCode.OK "ok" response
    }

[<Fact>]
let ``healthz gives the same answer for any host, language and cookies`` () =
    task {
        use! app = newApp ()
        use! browser = signedUp app "sub-1" "animenerd"
        Assert.NotEmpty browser.CookieNames

        for language in [ "en"; "es" ] do
            use request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
            request.Headers.Host <- "anything.example"
            request.Headers.AcceptLanguage.ParseAdd language
            let! response = browser.Send request
            do! assertPlainAnswer HttpStatusCode.OK "ok" response
    }

[<Fact>]
let ``healthz answers 503 unavailable quickly when the database can't be reached`` () =
    task {
        let! db = newDatabase ()
        use! app = createAppWith db [ "ConnectionStrings:Database", closedPortConnection ] ignore
        let watch = Stopwatch.StartNew()
        let! response = app.Client.GetAsync "/healthz"
        watch.Stop()

        do! assertPlainAnswer HttpStatusCode.ServiceUnavailable "unavailable" response
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds 1.5, $"took {watch.Elapsed}")
    }

[<Fact>]
let ``healthz still answers 200 when Megafono is down and never calls it`` () =
    task {
        use! app = newFederatedApp ()
        app.Megafono.FailNext 1000
        let! response = app.Client.GetAsync "/healthz"
        do! assertPlainAnswer HttpStatusCode.OK "ok" response
        Assert.Empty app.Megafono.Requests
    }

[<Fact>]
let ``other verbs on healthz are refused with 405`` () =
    task {
        use! app = newApp ()
        let! post = app.Client.PostAsync("/healthz", new StringContent "")
        // The route exists for GET only, so routing answers 405 (not the 404 of an unknown route)
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode)
        let! delete = app.Client.DeleteAsync "/healthz"
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode)
    }
