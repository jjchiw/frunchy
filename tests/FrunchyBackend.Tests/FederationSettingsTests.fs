/// Feature 014 (US2): the federation switch. Off needs nothing; on, every setting is required and a
/// missing one stops startup naming the key, never a value.
module FrunchyBackend.Tests.FederationSettingsTests

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Federation
open FrunchyBackend.Tests.Infrastructure

let private secretToken = "token-value-that-must-not-leak"
let private secretWebhook = "webhook-value-that-must-not-leak"

let private allSettings =
    [ "Frunchy:Federation:Enabled", "true"
      "Frunchy:Federation:Domain", "frunchy.test"
      "Frunchy:Megafono:Url", "http://megafono:8081"
      "Frunchy:Megafono:Token", secretToken
      "Frunchy:Megafono:WebhookSecret", secretWebhook ]

let private keysToCheck =
    [ "Frunchy:Federation:Domain"; "Frunchy:Megafono:Url"; "Frunchy:Megafono:Token"; "Frunchy:Megafono:WebhookSecret" ]

let private with' (key: string) (value: string) =
    allSettings |> List.map (fun (k, v) -> if k = key then k, value else k, v)

/// Build (not start) the app, so a startup check shows as an exception
let private build (settings: (string * string) list) =
    Program.buildApp Array.empty (fun builder ->
        builder.WebHost.UseTestServer() |> ignore

        builder.Configuration.AddInMemoryCollection(
            dict (
                [ "ConnectionStrings:Database", "Host=unused"
                  Program.WorkerEnabledKey, "false"
                  Program.EnrichmentWorkerEnabledKey, "false"
                  Program.FediverseWorkerEnabledKey, "false"
                  DataProtectionStore.EncryptionKeySetting, PostgresFixture.TestDataProtectionKey ]
                @ settings
            )
        )
        |> ignore)

[<Fact>]
let ``federation off needs no settings`` () =
    task {
        let! db = PostgresFixture.newDatabase ()
        use! app = PostgresFixture.createApp db
        Assert.Equal(Disabled, app.Services.GetRequiredService<FederationSettings>())
    }

[<Fact>]
let ``federation explicitly off ignores half-set values`` () =
    task {
        let! db = PostgresFixture.newDatabase ()
        use! app = PostgresFixture.createAppWith db [ "Frunchy:Federation:Enabled", "false"; "Frunchy:Megafono:Url", "not a url" ] ignore
        Assert.Equal(Disabled, app.Services.GetRequiredService<FederationSettings>())
    }

[<Fact>]
let ``federation on fails startup naming the one missing key and never a value`` () =
    for missing in keysToCheck do
        let ex = Assert.Throws<InvalidOperationException>(fun () -> build (allSettings |> List.filter (fun (k, _) -> k <> missing)) |> ignore)

        Assert.Contains(missing, ex.Message)

        // the other keys are not blamed, and no value is printed
        for key, value in allSettings |> List.filter (fun (k, _) -> k <> missing && k <> "Frunchy:Federation:Enabled") do
            Assert.DoesNotContain(key, ex.Message)
            Assert.DoesNotContain(value, ex.Message)

[<Fact>]
let ``federation on with a blank key counts as missing`` () =
    let ex = Assert.Throws<InvalidOperationException>(fun () -> build (with' "Frunchy:Megafono:Token" "  ") |> ignore)
    Assert.Contains("Frunchy:Megafono:Token", ex.Message)
    Assert.DoesNotContain(secretWebhook, ex.Message)

[<Fact>]
let ``federation on with nothing else lists every key`` () =
    let ex = Assert.Throws<InvalidOperationException>(fun () -> build [ "Frunchy:Federation:Enabled", "true" ] |> ignore)

    for key in keysToCheck do
        Assert.Contains(key, ex.Message)

[<Fact>]
let ``a domain with a scheme or a slash fails startup`` () =
    for domain in [ "https://frunchy.test"; "frunchy.test/"; "frunchy.test/path"; "frunchy test" ] do
        let ex = Assert.Throws<InvalidOperationException>(fun () -> build (with' "Frunchy:Federation:Domain" domain) |> ignore)
        Assert.Contains("Frunchy:Federation:Domain", ex.Message)
        Assert.DoesNotContain(secretToken, ex.Message)

[<Fact>]
let ``a megafono url that is not an absolute http or https url fails startup`` () =
    for url in [ "megafono:8081"; "ftp://megafono:8081"; "/internal"; "not a url" ] do
        let ex = Assert.Throws<InvalidOperationException>(fun () -> build (with' "Frunchy:Megafono:Url" url) |> ignore)
        Assert.Contains("Frunchy:Megafono:Url", ex.Message)
        Assert.DoesNotContain(secretToken, ex.Message)

[<Fact>]
let ``federation on with every setting starts and the settings resolve from the container`` () =
    task {
        let! db = PostgresFixture.newDatabase ()
        use! app = PostgresFixture.createAppWith db allSettings ignore

        match app.Services.GetRequiredService<FederationSettings>() with
        | Enabled values ->
            Assert.Equal("frunchy.test", values.Domain)
            Assert.Equal(Uri "http://megafono:8081", values.MegafonoUrl)
            Assert.Equal(secretToken, values.Token)
            Assert.Equal(secretWebhook, values.WebhookSecret)
        | Disabled -> failwith "expected federation to be enabled"
    }
