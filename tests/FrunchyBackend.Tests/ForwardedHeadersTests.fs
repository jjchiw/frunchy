/// Feature 014 (US2): X-Forwarded-For/-Proto/-Host are honoured only from `Frunchy:TrustedProxies`.
/// The observable is the redirect_uri Google's handler builds from the scheme and host the app sees.
module FrunchyBackend.Tests.ForwardedHeadersTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open System.Web
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend
open FrunchyBackend.Tests.Infrastructure

/// Test only: the TestServer has no network peer, so this first middleware sets the remote address
/// from the `X-Test-Remote` header, the way the real server's connection would
type private RemoteAddressFilter() =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) : Action<IApplicationBuilder> =
            Action<IApplicationBuilder>(fun app ->
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx continuation ->
                        match ctx.Request.Headers["X-Test-Remote"].ToString() with
                        | "" -> ()
                        | address -> ctx.Connection.RemoteIpAddress <- IPAddress.Parse address

                        continuation.Invoke())
                )
                |> ignore

                next.Invoke app)

let private google =
    [ "Frunchy:Auth:Providers:Google:Enabled", "true"
      "Frunchy:Auth:Providers:Google:ClientId", "id"
      "Frunchy:Auth:Providers:Google:ClientSecret", "secret" ]

let private withRemote (trusted: string option) =
    task {
        let! db = PostgresFixture.newDatabase ()

        let settings =
            google @ (trusted |> Option.map (fun t -> [ "Frunchy:TrustedProxies", t ]) |> Option.defaultValue [])

        return!
            PostgresFixture.createAppWith db settings (fun builder ->
                builder.Services.AddTransient<IStartupFilter, RemoteAddressFilter>() |> ignore)
    }

/// The redirect_uri Google's handler sends, for a request from `remote` carrying forwarded headers
let private redirectUriFrom (app: PostgresFixture.TestApp) (remote: string) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, "/signin/google")
        request.Headers.Add("X-Test-Remote", remote)
        request.Headers.Add("X-Forwarded-Proto", "https")
        request.Headers.Add("X-Forwarded-Host", "frunchy.test")
        request.Headers.Add("X-Forwarded-For", "203.0.113.9")
        let! response = app.Client.SendAsync request
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
        return HttpUtility.ParseQueryString(response.Headers.Location.Query)["redirect_uri"]
    }

[<Fact>]
let ``forwarded proto and host from a trusted proxy reach the redirect uri`` () =
    task {
        use! app = withRemote (Some "10.1.2.3")
        let! uri = redirectUriFrom app "10.1.2.3"
        Assert.Equal("https://frunchy.test/signin-google", uri)
    }

[<Fact>]
let ``a trusted CIDR range and a list of proxies both work`` () =
    task {
        use! app = withRemote (Some "192.168.7.7, 10.1.0.0/16")
        let! fromRange = redirectUriFrom app "10.1.200.4"
        Assert.Equal("https://frunchy.test/signin-google", fromRange)
        let! fromAddress = redirectUriFrom app "192.168.7.7"
        Assert.Equal("https://frunchy.test/signin-google", fromAddress)
    }

[<Fact>]
let ``forwarded headers from any other address are ignored`` () =
    task {
        use! app = withRemote (Some "10.1.2.3")
        let! stranger = redirectUriFrom app "10.1.2.4"
        Assert.Equal("http://localhost/signin-google", stranger)
        // loopback is not trusted by default once proxies are configured
        let! loopback = redirectUriFrom app "127.0.0.1"
        Assert.Equal("http://localhost/signin-google", loopback)
    }

[<Fact>]
let ``with no trusted proxies configured forwarded headers are ignored`` () =
    task {
        use! app = withRemote None
        let! uri = redirectUriFrom app "10.1.2.3"
        Assert.Equal("http://localhost/signin-google", uri)
        let! loopback = redirectUriFrom app "127.0.0.1"
        Assert.Equal("http://localhost/signin-google", loopback)
    }

[<Fact>]
let ``an empty trusted proxies setting means none`` () =
    task {
        use! app = withRemote (Some "  ")
        let! uri = redirectUriFrom app "10.1.2.3"
        Assert.Equal("http://localhost/signin-google", uri)
    }

[<Fact>]
let ``a trusted proxy entry that is not an address or range stops startup`` () =
    task {
        let! db = PostgresFixture.newDatabase ()

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                PostgresFixture.createAppWith db [ "Frunchy:TrustedProxies", "10.1.2.3, not-an-ip" ] ignore :> Task)

        Assert.Contains("Frunchy:TrustedProxies", ex.Message)
        Assert.Contains("not-an-ip", ex.Message)
    }
