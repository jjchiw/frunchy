module FrunchyBackend.Tests.AuthConfigTests

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.AuthConfig

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
