module FrunchyBackend.Tests.AuthConfigTests

open System
open System.IO
open System.Net
open System.Web
open System.Text.Json
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
    match validate [ settings "Google" true "id" ""; settings "Discord" false "" "" ] with
    | Error message ->
        Assert.Equal(
            "Provider 'Google' is enabled but Frunchy:Auth:Providers:Google:ClientSecret is missing (set it with dotnet user-secrets or an environment variable)",
            message
        )
    | Ok() -> failwith "expected an error"

[<Fact>]
let ``an enabled provider without its client id names ClientId`` () =
    match validate [ settings "Discord" true "" "secret" ] with
    | Error message -> Assert.Contains("Frunchy:Auth:Providers:Discord:ClientId is missing", message)
    | Ok() -> failwith "expected an error"

[<Fact>]
let ``disabled providers and complete providers are fine`` () =
    Assert.Equal(Ok(), validate [ settings "Google" false "" ""; settings "Discord" true "id" "secret" ])

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
                [ "Frunchy:Auth:Providers:Discord:Enabled", "true"
                  "Frunchy:Auth:Providers:Discord:ClientId", "first-client-id"
                  "Frunchy:Auth:Providers:Discord:ClientSecret", "first-secret" ]
                ignore

        let clientIdSentToDiscord () =
            task {
                let! response = app.Client.GetAsync "/signin/discord"
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
                let location = response.Headers.Location
                Assert.Equal("discord.com", location.Host)
                return HttpUtility.ParseQueryString(location.Query)["client_id"]
            }

        let! before = clientIdSentToDiscord ()
        Assert.Equal("first-client-id", before)

        let config = app.Services.GetRequiredService<IConfiguration>() :?> IConfigurationRoot
        config["Frunchy:Auth:Providers:Discord:ClientId"] <- "rotated-client-id"
        config.Reload()

        let! after = clientIdSentToDiscord ()
        Assert.Equal("rotated-client-id", after)
    }
