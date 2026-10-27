/// Feature 026: what `publishGraph` declares for the Caravana deploy (specs/026-federation-caravana/contracts/
/// publish-graph.md). Every test builds the publish model only, so none needs Docker, Caravana or a network.
module PublishGraphTests

open System
open System.IO
open System.Linq
open Aspire.Hosting
open Aspire.Hosting.ApplicationModel
open Microsoft.Extensions.Configuration
open CosmicChimps.Aspire.Hosting.Caravana
open Xunit
open PublishModel

let private federated () = buildGraph [ "Deploy:Federation", "true" ]
let private notFederated () = buildGraph [ "Deploy:Federation", "false" ]

let private serviceNames (builder: IDistributedApplicationBuilder) =
    services builder |> List.map (fun r -> r.Name) |> List.sort

/// The one call of a kind for a service, or a failure naming both
let private only builder kind serviceName =
    match callsFor builder kind serviceName with
    | [ call ] -> call
    | calls -> failwithf "Expected one %s call for %s, found %d" kind serviceName calls.Length

/// A thrown message must name the parameter and never carry the value that was given
let private refuses (overrides: (string * string) list) (mustSay: string) (mustNotSay: string list) =
    let error = Assert.ThrowsAny<exn>(fun () -> buildGraph overrides |> ignore)
    Assert.Contains(mustSay, error.Message)

    for secret in mustNotSay do
        Assert.DoesNotContain(secret, error.Message)

// Settings and parameters

[<Fact>]
let ``Deploy:Federation defaults to true`` () =
    // No override at all: the graph is the federated one
    let builder = buildGraph []
    Assert.Equal<string list>([ "frunchy"; "gateway"; "megafono"; "megafono-pg"; "pg" ], serviceNames builder)

[<Fact>]
let ``the AppHost settings turn federation on and name Caravana's container network`` () =
    let settings =
        ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repoRoot, "AppHost", "appsettings.json"))
            .Build()

    Assert.True(settings.GetValue<bool>("Deploy:Federation"))
    Assert.Equal("172.26.64.0/20", settings["Caravana:ContainerNetwork"])
    Assert.Equal("0.2.0", settings["Megafono:ImageTag"])
    Assert.False(settings.GetValue<bool>("Megafono:BuildFromSource"))

[<Fact>]
let ``the Megafono image tag refuses an empty value and latest`` () =
    for tag in [ ""; "   "; "latest"; " latest " ] do
        refuses [ "Megafono:ImageTag", tag ] "Megafono:ImageTag" []

[<Fact>]
let ``the Megafono image tag is not read when federation is off`` () =
    buildGraph [ "Deploy:Federation", "false"; "Megafono:ImageTag", "latest" ] |> ignore

[<Fact>]
let ``Megafono:BuildFromSource is refused in publish mode with federation on`` () =
    refuses [ "Megafono:BuildFromSource", "true" ] "Megafono:BuildFromSource" []

[<Fact>]
let ``Megafono:BuildFromSource is ignored when federation is off`` () =
    buildGraph [ "Deploy:Federation", "false"; "Megafono:BuildFromSource", "true" ] |> ignore

[<Fact>]
let ``a missing master key is refused naming the parameter`` () =
    for missing in [ null; "" ] do
        refuses [ "Parameters:megafono-master-key", missing ] "megafono-master-key" []

[<Fact>]
let ``a malformed master key is refused naming the parameter and never its value`` () =
    let notBase64 = "this-is-not-base64-at-all!"
    let tooShort = Convert.ToBase64String(Array.zeroCreate<byte> 16)

    for bad in [ notBase64; tooShort ] do
        refuses [ "Parameters:megafono-master-key", bad ] "megafono-master-key" [ bad ]

[<Fact>]
let ``the four Megafono parameters are declared, and secret, only when federating`` () =
    let names = [ "megafono-master-key"; "megafono-token"; "megafono-webhook-secret"; "megafono-db-password" ]

    let on = federated ()

    for name in names do
        let p = on.Resources.OfType<ParameterResource>() |> Seq.find (fun p -> p.Name = name)
        Assert.True(p.Secret, $"{name} must be a secret parameter")

    let off = parameterNames (notFederated ())

    for name in names do
        Assert.DoesNotContain(name, off)

[<Fact>]
let ``the 018 parameters are unchanged`` () =
    let expected =
        [ "caravana-url"
          "caravana-token"
          "registry-endpoint"
          "registry-repository"
          "frunchy-domain"
          "db-password"
          "data-protection-key"
          "umbral-authority"
          "umbral-client-id"
          "umbral-client-secret"
          "mal-client-id"
          "root-operator" ]

    let secrets = set [ "caravana-token"; "db-password"; "data-protection-key"; "umbral-client-secret"; "mal-client-id" ]

    for builder in [ federated (); notFederated () ] do
        let parameters = builder.Resources.OfType<ParameterResource>() |> Seq.toList

        for name in expected do
            let p = parameters |> List.find (fun p -> p.Name = name)
            Assert.Equal(secrets.Contains name, p.Secret)

[<Fact>]
let ``a federated deploy declares only the 018 parameters and the four new ones`` () =
    let names = parameterNames (federated ())
    Assert.Equal(16, names.Count)

// Services

[<Fact>]
let ``federation on declares exactly five services and no dashboard`` () =
    let builder = federated ()
    Assert.Equal<string list>([ "frunchy"; "gateway"; "megafono"; "megafono-pg"; "pg" ], serviceNames builder)

    let caravana = builder.Resources |> Seq.find (fun r -> r.GetType().Name = "CaravanaResource")
    let deployDashboard = caravana.GetType().GetProperty("DeployDashboard").GetValue(caravana)
    Assert.Equal(false, unbox<bool> deployDashboard)

    for r in builder.Resources do
        Assert.DoesNotContain("dashboard", r.Name.ToLowerInvariant())

[<Fact>]
let ``only the gateway has a Caravana domain, the frunchy-domain parameter, and no port`` () =
    let builder = federated ()
    let domains = caravanaCalls builder |> List.filter (fun c -> c.Kind = "Domain")

    // Never megafono, never port 8081, never frunchy
    Assert.Equal<string list>([ "gateway" ], domains |> List.map (fun c -> c.Service))

    let domain = domains.Head
    Assert.Null(domain.Properties["Port"])
    // The host is the parameter, not a literal string
    Assert.Equal("<parameter:frunchy-domain>", string domain.Properties["Host"])
    Assert.False(domain.Properties["Host"] :? string, "the domain host must be a parameter reference")

[<Fact>]
let ``health checks, backups and memory match the contract`` () =
    let builder = federated ()

    // frunchy: HTTP /healthz
    let frunchyHealth = only builder "Health" "frunchy"
    Assert.Equal("/healthz", string frunchyHealth.Properties["Path"])
    Assert.Null(frunchyHealth.Properties["Port"])

    // pg and megafono-pg: TCP (no path)
    for name in [ "pg"; "megafono-pg" ] do
        let health = only builder "Health" name
        Assert.Null(health.Properties["Path"])
        Assert.Null(health.Properties["Port"])

    // megafono: HTTP /health on its internal port
    let megafonoHealth = only builder "Health" "megafono"
    Assert.Equal("/health", string megafonoHealth.Properties["Path"])
    Assert.Equal(Nullable 8081, unbox<Nullable<int>> megafonoHealth.Properties["Port"])

    // gateway: HTTP /healthz on 8080 (its own endpoint since Megafono 0.2.0, never proxied)
    let gatewayHealth = only builder "Health" "gateway"
    Assert.Equal("/healthz", string gatewayHealth.Properties["Path"])
    Assert.Equal(Nullable 8080, unbox<Nullable<int>> gatewayHealth.Properties["Port"])

    // Backups: Postgres dumps for the two databases, nothing else
    let backups = caravanaCalls builder |> List.filter (fun c -> c.Kind = "Backup")
    Assert.Equal<string list>([ "megafono-pg"; "pg" ], backups |> List.map (fun c -> c.Service) |> List.sort)

    let pgBackup = only builder "Backup" "pg"
    Assert.Equal(CaravanaBackupEngine.Postgres, unbox<CaravanaBackupEngine> pgBackup.Properties["Engine"])
    Assert.Equal("0 3 * * *", string pgBackup.Properties["Schedule"])
    Assert.Equal(Nullable 14, unbox<Nullable<int>> pgBackup.Properties["Keep"])

    let megafonoBackup = only builder "Backup" "megafono-pg"
    Assert.Equal(CaravanaBackupEngine.Postgres, unbox<CaravanaBackupEngine> megafonoBackup.Properties["Engine"])
    Assert.Equal("30 3 * * *", string megafonoBackup.Properties["Schedule"])
    Assert.Equal(Nullable 14, unbox<Nullable<int>> megafonoBackup.Properties["Keep"])

    // Memory (reserved, ceiling) in MB
    let memory name =
        let call = only builder "Memory" name
        unbox<Nullable<int>> call.Properties["ReservedMB"], unbox<Nullable<int>> call.Properties["MaxMB"]

    Assert.Equal((Nullable 256, Nullable 768), memory "frunchy")
    Assert.Equal((Nullable 256, Nullable 512), memory "pg")
    Assert.Equal((Nullable 128, Nullable 256), memory "megafono-pg")
    Assert.Equal((Nullable 256, Nullable 512), memory "megafono")
    Assert.Equal((Nullable 64, Nullable 128), memory "gateway")

[<Fact>]
let ``the Megafono and gateway images are the published ones at the pinned tag, never latest`` () =
    let builder = buildGraph [ "Megafono:ImageTag", " 0.2.0 " ]

    Assert.Equal(("ghcr.io/cosmic-chimps/megafono", "0.2.0"), image (service builder "megafono"))
    Assert.Equal(("ghcr.io/cosmic-chimps/megafono-gateway", "0.2.0"), image (service builder "gateway"))

    // The tag follows the setting, for both images
    let next = buildGraph [ "Megafono:ImageTag", "0.3.0" ]
    Assert.Equal("0.3.0", snd (image (service next "megafono")))
    Assert.Equal("0.3.0", snd (image (service next "gateway")))

    for name in [ "megafono"; "gateway"; "pg"; "megafono-pg" ] do
        Assert.NotEqual<string>("latest", snd (image (service builder name)))

[<Fact>]
let ``endpoints: Megafono has federation 8080 and internal 8081, the gateway one http 8080, only the gateway is external`` () =
    let builder = federated ()

    let describe (name: string) =
        endpointsOf (service builder name)
        |> List.map (fun e -> e.Name, Option.ofNullable e.TargetPort, e.IsExternal)
        |> List.sort

    Assert.Equal<(string * int option * bool) list>(
        [ "federation", Some 8080, false; "internal", Some 8081, false ],
        describe "megafono"
    )

    Assert.Equal<(string * int option * bool) list>([ "http", Some 8080, true ], describe "gateway")

    // Frunchy is reached by the gateway and Megafono inside the app, never directly from outside
    for e in endpointsOf (service builder "frunchy") do
        Assert.False(e.IsExternal, $"frunchy endpoint {e.Name} must not be external when federating")

[<Fact>]
let ``the databases: volumes and POSTGRES_DB`` () =
    let builder = federated ()

    let volumes (name: string) =
        (service builder name).Annotations.OfType<ContainerMountAnnotation>()
        |> Seq.map (fun m -> m.Source)
        |> Seq.toList

    Assert.Equal<string list>([ "frunchy-pg" ], volumes "pg")
    Assert.Equal<string list>([ "megafono-pg" ], volumes "megafono-pg")

    Assert.Equal(Literal "frunchydb", (envOf builder "pg")["POSTGRES_DB"])
    Assert.Equal(Literal "megafono", (envOf builder "megafono-pg")["POSTGRES_DB"])
    Assert.Equal(Param "megafono-db-password", (envOf builder "megafono-pg")["POSTGRES_PASSWORD"])
    Assert.Equal(Param "db-password", (envOf builder "pg")["POSTGRES_PASSWORD"])

// Environment

[<Fact>]
let ``megafono's environment`` () =
    let env = envOf (federated ()) "megafono"

    Assert.Equal(Literal "Production", env["ASPNETCORE_ENVIRONMENT"])
    Assert.Equal(Expression "{megafono-pg.connectionString};Database=megafono", env["ConnectionStrings__Database"])
    Assert.Equal(Param "frunchy-domain", env["Megafono__Domain"])
    Assert.Equal(Param "megafono-master-key", env["Megafono__MasterKey"])
    Assert.Equal(Endpoint("frunchy", "http"), env["Megafono__App__BaseUrl"])
    Assert.Equal(Param "megafono-token", env["Megafono__App__Token"])
    Assert.Equal(Param "megafono-webhook-secret", env["Megafono__App__WebhookSecret"])
    Assert.Equal(Literal "172.26.64.0/20", env["Megafono__TrustedProxies"])

    // Refused in Production by Megafono, and never wanted here
    Assert.False(env.ContainsKey "Megafono__Federation__AllowPrivateAddresses")

[<Fact>]
let ``the gateway's environment: Megafono's federation endpoint, never internal`` () =
    let builder = federated ()
    let env = envOf builder "gateway"

    Assert.Equal(Literal "10.99.0.1", env["Gateway__TrustedProxies"])

    let megafonoAddress = env["ReverseProxy__Clusters__megafono__Destinations__primary__Address"]
    Assert.Equal(Endpoint("megafono", "federation"), megafonoAddress)
    Assert.NotEqual(Endpoint("megafono", "internal"), megafonoAddress)

    Assert.Equal(Endpoint("frunchy", "http"), env["ReverseProxy__Clusters__app__Destinations__primary__Address"])

    // No env value of any service points at Megafono's internal port except Frunchy's own Megafono URL
    for service in services builder do
        if service.Name <> "frunchy" then
            for KeyValue(_, value) in envOf builder service.Name do
                Assert.NotEqual(Endpoint("megafono", "internal"), value)

[<Fact>]
let ``frunchy keeps every 018 entry and adds the federation ones`` () =
    let env = envOf (federated ()) "frunchy"

    // 018, same values (the trusted proxies change, below)
    Assert.Equal(Expression "{pg.connectionString};Database=frunchydb", env["ConnectionStrings__Database"])
    Assert.Equal(Param "data-protection-key", env["Frunchy__DataProtection__EncryptionKey"])
    Assert.Equal(Literal "true", env["Frunchy__Auth__Providers__Umbral__Enabled"])
    Assert.Equal(Param "umbral-authority", env["Frunchy__Auth__Providers__Umbral__Authority"])
    Assert.Equal(Param "umbral-client-id", env["Frunchy__Auth__Providers__Umbral__ClientId"])
    Assert.Equal(Param "umbral-client-secret", env["Frunchy__Auth__Providers__Umbral__ClientSecret"])
    Assert.Equal(Param "mal-client-id", env["Frunchy__Mal__ClientId"])
    Assert.Equal(Param "root-operator", env["Frunchy__RootOperator"])

    // 026
    Assert.Equal(Literal "true", env["Frunchy__Federation__Enabled"])
    Assert.Equal(Param "frunchy-domain", env["Frunchy__Federation__Domain"])
    Assert.Equal(Endpoint("megafono", "internal"), env["Frunchy__Megafono__Url"])
    Assert.Equal(Param "megafono-token", env["Frunchy__Megafono__Token"])
    Assert.Equal(Param "megafono-webhook-secret", env["Frunchy__Megafono__WebhookSecret"])

[<Fact>]
let ``trusted proxies: the container network for Megafono and Frunchy, Traefik's address for the gateway`` () =
    let builder = federated ()
    let frunchy = (envOf builder "frunchy")["Frunchy__TrustedProxies"]
    Assert.Equal(Literal "172.26.64.0/20", frunchy)
    Assert.NotEqual(Literal "10.99.0.1", frunchy)
    Assert.Equal(Literal "10.99.0.1", (envOf builder "gateway")["Gateway__TrustedProxies"])

    // Caravana:ContainerNetwork moves both
    let custom = buildGraph [ "Caravana:ContainerNetwork", "172.30.0.0/16" ]
    Assert.Equal(Literal "172.30.0.0/16", (envOf custom "frunchy")["Frunchy__TrustedProxies"])
    Assert.Equal(Literal "172.30.0.0/16", (envOf custom "megafono")["Megafono__TrustedProxies"])
    Assert.Equal(Literal "10.99.0.1", (envOf custom "gateway")["Gateway__TrustedProxies"])

[<Fact>]
let ``no secret value is ever a literal in any environment entry, and the secret entries are parameter references`` () =
    let builder = federated ()
    let secretValues = fakeSecrets |> List.map snd |> List.filter (fun v -> v <> "")

    for resource in builder.Resources do
        match resource with
        | :? IResourceWithEnvironment ->
            for KeyValue(key, value) in environmentOf builder resource do
                match value with
                | Literal text ->
                    for secret in secretValues do
                        Assert.False(text.Contains secret, $"{resource.Name} {key} holds a secret as a literal")
                | _ -> ()
        | _ -> ()

    // Each secret travels as a reference to its (secret) parameter
    let expectParam service key parameter =
        Assert.Equal(Param parameter, (envOf builder service)[key])

    expectParam "megafono" "Megafono__MasterKey" "megafono-master-key"
    expectParam "megafono" "Megafono__App__Token" "megafono-token"
    expectParam "megafono" "Megafono__App__WebhookSecret" "megafono-webhook-secret"
    expectParam "megafono-pg" "POSTGRES_PASSWORD" "megafono-db-password"
    expectParam "frunchy" "Frunchy__Megafono__Token" "megafono-token"
    expectParam "frunchy" "Frunchy__Megafono__WebhookSecret" "megafono-webhook-secret"

    // A connection string holds a password through its server's parameter, never as text
    for name in [ "frunchy"; "megafono" ] do
        match (envOf builder name)["ConnectionStrings__Database"] with
        | Expression text -> Assert.DoesNotContain("fake-", text)
        | other -> failwithf "%s: expected a connection string expression, got %A" name other

// Federation off

[<Fact>]
let ``federation off is the 018 graph: frunchy and pg only, the domain on frunchy`` () =
    let builder = notFederated ()
    Assert.Equal<string list>([ "frunchy"; "pg" ], serviceNames builder)

    // The domain is on frunchy and nowhere else, and the app is external
    let domains = caravanaCalls builder |> List.filter (fun c -> c.Kind = "Domain")
    Assert.Equal<string list>([ "frunchy" ], domains |> List.map (fun c -> c.Service))
    Assert.Equal("<parameter:frunchy-domain>", string domains.Head.Properties["Host"])

    for e in endpointsOf (service builder "frunchy") do
        Assert.True(e.IsExternal)

    let env = envOf builder "frunchy"
    Assert.Equal(Literal "10.99.0.1", env["Frunchy__TrustedProxies"])
    Assert.False(env.Keys |> Seq.exists (fun k -> k.StartsWith "Frunchy__Federation__"))
    Assert.False(env.Keys |> Seq.exists (fun k -> k.StartsWith "Frunchy__Megafono__"))

    // Health, memory and backup as in 018
    Assert.Equal("/healthz", string (only builder "Health" "frunchy").Properties["Path"])
    Assert.Equal<string list>([ "pg" ], caravanaCalls builder |> List.filter (fun c -> c.Kind = "Backup") |> List.map (fun c -> c.Service))

    // None of Megafono's parameters exists, so Aspire never prompts for them
    let names = parameterNames builder

    for name in [ "megafono-master-key"; "megafono-token"; "megafono-webhook-secret"; "megafono-db-password" ] do
        Assert.DoesNotContain(name, names)

[<Fact>]
let ``federation off needs no master key`` () =
    buildGraph [ "Deploy:Federation", "false"; "Parameters:megafono-master-key", null ] |> ignore
