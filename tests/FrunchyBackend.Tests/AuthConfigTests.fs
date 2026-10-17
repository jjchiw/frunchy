module FrunchyBackend.Tests.AuthConfigTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Web
open System.Text.Json
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.AuthConfig
open FrunchyBackend.Tests.Infrastructure

let private settings name enabled clientId secret =
    { Name = name; Enabled = enabled; ClientId = clientId; ClientSecret = secret }

[<Fact>]
let ``an enabled provider without its secret is rejected with the named setting`` () =
    match validate [ settings "Google" true "id" ""; settings "Umbral" false "" "" ] with
    | Error message ->
        Assert.Equal(
            "Provider 'Google' is enabled but Frunchy:Auth:Providers:Google:ClientSecret is missing (set it with dotnet user-secrets or an environment variable)",
            message
        )
    | Ok() -> failwith "expected an error"

[<Fact>]
let ``an enabled provider without its client id names ClientId`` () =
    match validate [ settings "Umbral" true "" "secret" ] with
    | Error message -> Assert.Contains("Frunchy:Auth:Providers:Umbral:ClientId is missing", message)
    | Ok() -> failwith "expected an error"

[<Fact>]
let ``disabled providers and complete providers are fine`` () =
    Assert.Equal(Ok(), validate [ settings "Google" false "" ""; settings "Umbral" true "id" "secret" ])

[<Fact>]
let ``the site refuses to start with an enabled provider missing credentials`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            Program.buildApp Array.empty (fun builder ->
                builder.WebHost.UseTestServer() |> ignore

                builder.Configuration.AddInMemoryCollection(
                    dict [ "ConnectionStrings:Database", "Host=unused"; Program.WorkerEnabledKey, "false"; "Frunchy:Auth:Providers:Google:Enabled", "true" ]
                )
                |> ignore)
            |> ignore)

    Assert.Contains("Provider 'Google' is enabled but Frunchy:Auth:Providers:Google:ClientId is missing", ex.Message)

/// Guardrail: provider credentials must never be committed (FR-016)
[<Fact>]
let ``committed appsettings files contain no provider credentials`` () =
    let rec repoRoot (dir: DirectoryInfo) =
        if isNull dir then failwith "repo root not found"
        elif File.Exists(Path.Combine(dir.FullName, "FrunchyBackend.slnx")) then dir
        else repoRoot dir.Parent

    let root = repoRoot (DirectoryInfo AppContext.BaseDirectory)

    let files =
        Directory.EnumerateFiles(root.FullName, "appsettings*.json", SearchOption.AllDirectories)
        |> Seq.filter (fun f -> not (f.Contains $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}" || f.Contains $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        |> List.ofSeq

    Assert.NotEmpty files

    let rec offending (path: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            [ for p in element.EnumerateObject() do
                  if (p.Name = "ClientId" || p.Name = "ClientSecret") && p.Value.ValueKind = JsonValueKind.String && p.Value.GetString() <> "" then
                      yield $"{path}.{p.Name}"
                  yield! offending $"{path}.{p.Name}" p.Value ]
        | JsonValueKind.Array -> [ for item in element.EnumerateArray() do yield! offending path item ]
        | _ -> []

    for file in files do
        use doc = JsonDocument.Parse(File.ReadAllText file, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Skip))
        Assert.Empty(offending (Path.GetFileName file) doc.RootElement)

/// Credentials are bound, not copied: a rotated secret (e.g. updated in Bella Baxter, which reloads
/// the configuration) is used by the next sign-in without restarting the site
[<Fact>]
let ``a rotated provider credential is used without a restart`` () =
    task {
        let! db = PostgresFixture.newDatabase ()

        use! app =
            PostgresFixture.createAppWith
                db
                [ "Frunchy:Auth:Providers:Google:Enabled", "true"
                  "Frunchy:Auth:Providers:Google:ClientId", "first-client-id"
                  "Frunchy:Auth:Providers:Google:ClientSecret", "first-secret" ]
                ignore

        let clientIdSentToGoogle () =
            task {
                let! response = app.Client.GetAsync "/signin/google"
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
                let location = response.Headers.Location
                Assert.Equal("accounts.google.com", location.Host)
                return HttpUtility.ParseQueryString(location.Query)["client_id"]
            }

        let! before = clientIdSentToGoogle ()
        Assert.Equal("first-client-id", before)

        let config = app.Services.GetRequiredService<IConfiguration>() :?> IConfigurationRoot
        config["Frunchy:Auth:Providers:Google:ClientId"] <- "rotated-client-id"
        config.Reload()

        let! after = clientIdSentToGoogle ()
        Assert.Equal("rotated-client-id", after)
    }

/// A failed provider round trip (cancelled, reloaded callback, "Correlation failed") lands on the
/// sign-in page's "try again" message, and the correlation cookie works over plain http
[<Fact>]
let ``a failed provider round trip shows the sign-in page, not an exception`` () =
    task {
        let! db = PostgresFixture.newDatabase ()

        use! app =
            PostgresFixture.createAppWith
                db
                [ "Frunchy:Auth:Providers:Google:Enabled", "true"
                  "Frunchy:Auth:Providers:Google:ClientId", "id"
                  "Frunchy:Auth:Providers:Google:ClientSecret", "secret" ]
                ignore

        // Starting sign-in sets the correlation cookie: SameSite=Lax, not Secure over http
        let! start = app.Client.GetAsync "/signin/google"
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode)

        let correlation =
            start.Headers.GetValues "Set-Cookie" |> Seq.find (fun c -> c.StartsWith ".AspNetCore.Correlation.")

        Assert.Contains("samesite=lax", correlation.ToLowerInvariant())
        Assert.DoesNotContain("secure", correlation.ToLowerInvariant())

        // A callback the handler can't correlate (here: a state it never issued)
        let! callback = app.Client.GetAsync "/signin-google?code=abc&state=not-ours"
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode)
        Assert.Equal("/signin?error=failed", string callback.Headers.Location)
    }

// ---------------------------------------------------------------------------
// Umbral (OpenID Connect)
// ---------------------------------------------------------------------------

[<Fact>]
let ``an Umbral authority must be https, or http on localhost`` () =
    for authority in [ "https://umbral.example"; "http://localhost:5173"; "http://127.0.0.1:5173" ] do
        Assert.Equal(Ok(), validateAuthority "Umbral" authority)

    for authority in [ "http://umbral.example"; ""; "not a url" ] do
        match validateAuthority "Umbral" authority with
        | Error message -> Assert.Contains("Frunchy:Auth:Providers:Umbral:Authority", message)
        | Ok() -> failwith $"expected an error for '{authority}'"

[<Fact>]
let ``plain http metadata is allowed only on a loopback host`` () =
    Assert.True(allowsHttpMetadata "http://localhost:5173")
    Assert.True(allowsHttpMetadata "http://127.0.0.1:5173")
    Assert.False(allowsHttpMetadata "https://umbral.example")
    Assert.False(allowsHttpMetadata "https://localhost:5173")
    Assert.False(allowsHttpMetadata "http://umbral.example")

[<Fact>]
let ``the site refuses to start with Umbral on a plain http authority`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            Program.buildApp Array.empty (fun builder ->
                builder.WebHost.UseTestServer() |> ignore

                builder.Configuration.AddInMemoryCollection(
                    dict
                        [ "ConnectionStrings:Database", "Host=unused"
                          Program.WorkerEnabledKey, "false"
                          "Frunchy:Auth:Providers:Umbral:Enabled", "true"
                          "Frunchy:Auth:Providers:Umbral:ClientId", "id"
                          "Frunchy:Auth:Providers:Umbral:ClientSecret", "secret"
                          "Frunchy:Auth:Providers:Umbral:Authority", "http://umbral.example" ]
                )
                |> ignore)
            |> ignore)

    Assert.Contains("Frunchy:Auth:Providers:Umbral:Authority", ex.Message)

/// Answers Umbral's discovery document and records every request, so no network is used
type private FakeUmbral() =
    inherit HttpMessageHandler()
    member val Requests = Collections.Concurrent.ConcurrentQueue<string>()

    override this.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> =
        this.Requests.Enqueue(string request.RequestUri)

        let body =
            if request.RequestUri.AbsolutePath = "/.well-known/openid-configuration" then
                """{ "issuer": "http://localhost:5173/",
                     "authorization_endpoint": "http://localhost:5173/connect/authorize",
                     "token_endpoint": "http://localhost:5173/connect/token",
                     "jwks_uri": "http://localhost:5173/.well-known/jwks",
                     "response_types_supported": [ "code" ],
                     "subject_types_supported": [ "public" ],
                     "id_token_signing_alg_values_supported": [ "RS256" ],
                     "code_challenge_methods_supported": [ "S256" ] }"""
            else
                """{ "keys": [] }"""

        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body, Text.Encoding.UTF8, "application/json")))

let private umbralSettings =
    [ "Frunchy:Auth:Providers:Umbral:Enabled", "true"
      "Frunchy:Auth:Providers:Umbral:ClientId", "umbral-client-id"
      "Frunchy:Auth:Providers:Umbral:ClientSecret", "umbral-secret"
      "Frunchy:Auth:Providers:Umbral:Authority", "http://localhost:5173" ]

let private withFakeUmbral (fake: FakeUmbral) (builder: WebApplicationBuilder) =
    builder.Services.Configure<OpenIdConnectOptions>("umbral", Action<OpenIdConnectOptions>(fun o -> o.BackchannelHttpHandler <- fake))
    |> ignore

[<Fact>]
let ``the Umbral challenge sends an authorization code request with PKCE and the openid scope`` () =
    task {
        let! db = PostgresFixture.newDatabase ()
        let fake = new FakeUmbral()
        use! app = PostgresFixture.createAppWith db umbralSettings (withFakeUmbral fake)

        let! response = app.Client.GetAsync "/signin/umbral"
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        let location = response.Headers.Location
        Assert.Equal("localhost", location.Host)
        Assert.Equal(5173, location.Port)
        Assert.Equal("/connect/authorize", location.AbsolutePath)

        let query = HttpUtility.ParseQueryString location.Query
        Assert.Equal("umbral-client-id", query["client_id"])
        Assert.Equal("code", query["response_type"])
        Assert.Equal("openid", query["scope"])
        Assert.Equal("S256", query["code_challenge_method"])
        Assert.False(String.IsNullOrEmpty query["code_challenge"])
        // response_mode=query: the handler omits it, as the default for the code flow (form_post would be sent)
        Assert.Null query["response_mode"]
        Assert.EndsWith("/signin-umbral", query["redirect_uri"])
        Assert.Null query["prompt"]

        // Discovery ran against the configured Authority, over plain http (allowed on localhost)
        Assert.Contains("http://localhost:5173/.well-known/openid-configuration", fake.Requests)

        // The correlation and nonce cookies are SameSite=Lax, not Secure over http
        let cookies = response.Headers.GetValues "Set-Cookie" |> List.ofSeq

        for prefix in [ ".AspNetCore.Correlation."; ".AspNetCore.OpenIdConnect.Nonce." ] do
            let cookie = cookies |> List.find (fun c -> c.StartsWith prefix) |> fun c -> c.ToLowerInvariant()
            Assert.Contains("samesite=lax", cookie)
            Assert.DoesNotContain("secure", cookie)
    }

[<Fact>]
let ``a failed Umbral round trip shows the sign-in page, not an exception`` () =
    task {
        let! db = PostgresFixture.newDatabase ()
        let fake = new FakeUmbral()
        use! app = PostgresFixture.createAppWith db umbralSettings (withFakeUmbral fake)

        // A callback the handler can't correlate (here: a state it never issued)
        let! callback = app.Client.GetAsync "/signin-umbral?code=abc&state=not-ours"
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode)
        Assert.Equal("/signin?error=failed", string callback.Headers.Location)
    }
