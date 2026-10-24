/// Feature 014: the local platform. One command starts Postgres (frunchydb and megafono), Megafono, its
/// gateway and Frunchy, with the shared secrets wired in. See specs/014-aspire-platform/ (research R1-R3,
/// contracts/settings.md).
///
/// Features 018 and 026: the same AppHost is also the Caravana deploy. `aspire deploy` runs in publish mode, which
/// builds a different graph and publishes it with CosmicChimps.Aspire.Hosting.Caravana: Frunchy and Postgres
/// (018), plus Megafono, its gateway and its database when `Deploy:Federation` is on, which is the default (026).
/// `aspire run` keeps the local platform. deploy/compose.yaml stays the self-hosted alternative.
///
/// Aspire's F# support is partial: no Projects.* classes, so Frunchy is added by path, and
/// FSharp.Aspire.Hosting.AppHost supplies the WithReference/WaitFor overloads F# lacks (no covariance).
/// A connection string with a custom name has no F# overload either, so both apps get
/// ConnectionStrings__Database from the database's ConnectionStringExpression.
module Frunchy.AppHost

open System
open System.IO
open Aspire.Hosting
open Aspire.Hosting.ApplicationModel
open Microsoft.Extensions.Configuration
open CosmicChimps.Aspire.Hosting.Caravana

/// What the developer runs when the master key is missing or malformed. The value is never printed.
let private masterKeyHelp =
    "Set it once with: dotnet user-secrets set \"Parameters:megafono-master-key\" \"$(openssl rand -base64 32)\" "
    + "--project AppHost   (the key encrypts every actor's private key in Megafono: keep a copy of it)"

/// The master key must be base64 of exactly 32 bytes (Megafono refuses anything else at startup).
/// Fails before the app is built, naming the setting and the fix, never the value.
let private checkMasterKey (value: string) =
    match value with
    | null
    | "" -> failwithf "Parameter megafono-master-key is not set. %s" masterKeyHelp
    | text ->
        let ok =
            let buffer = Array.zeroCreate<byte> 64
            match Convert.TryFromBase64String(text.Trim(), Span<byte>(buffer)) with
            | true, written -> written = 32
            | _ -> false

        if not ok then
            failwithf "Parameter megafono-master-key must be the base64 of exactly 32 bytes. %s" masterKeyHelp

/// What the developer does to turn Umbral sign-in on. Never contains a secret value.
let private umbralHint (umbralPort: int) (gatewayPort: int) =
    String.concat
        Environment.NewLine
        [ "Umbral is running at http://localhost:" + string umbralPort + " but Frunchy's Umbral sign-in is off (no client credentials yet). To turn it on, once per umbral-data volume:"
          "  1. Claim it: docker exec <umbral container> /app/Umbral reset-credential   (or use the setup token from its log at /admin/setup)"
          "  2. In the admin, Connectors: enable the fake connector (dev only)."
          "  3. Apps > Create app > Web application, redirect URI exactly http://localhost:" + string gatewayPort + "/signin-umbral"
          "  4. dotnet user-secrets set \"Parameters:umbral-client-id\" \"<client id>\" --project AppHost"
          "     dotnet user-secrets set \"Parameters:umbral-client-secret\" \"<client secret>\" --project AppHost"
          "  5. Restart the AppHost." ]

/// Private ranges: how the gateway and the containers reach Megafono and Frunchy in dev
let private privateRanges = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"

let private generated () =
    // 32 letters and digits, no symbols: the values travel in headers and environment variables
    GenerateParameterDefault(MinLength = 32, Special = false) :> ParameterDefault

/// The pinned Megafono release both graphs use for the Megafono and gateway images (`Megafono:ImageTag`). Never
/// empty and never "latest".
let private megafonoImageTag (configuration: IConfiguration) =
    let tag =
        match configuration["Megafono:ImageTag"] with
        | null -> ""
        | tag -> tag.Trim()

    if tag = "" || tag = "latest" then
        failwith "Set Megafono:ImageTag to a pinned Megafono release (e.g. 0.1.0, never latest), or Megafono:BuildFromSource to true."

    tag

/// Production (features 018 and 026): `aspire deploy` publishes one Caravana app. Umbral is its own Caravana app.
/// With `Deploy:Federation` on (the default) the app has five services: "frunchy" and "pg" (018), plus
/// "megafono-pg", "megafono" and "gateway" (026). The gateway is the only service with a domain: it sends the
/// federation routes to Megafono and everything else to Frunchy. With it off the app is the 018 graph, exactly:
/// "frunchy" (with the domain) and "pg", and none of Megafono's four parameters is declared. Every runtime value
/// is an Aspire parameter (prompted when missing, secret where marked): no Bella credentials reach the container.
/// Public so the AppHost tests can build the model without running anything.
let publishGraph (builder: IDistributedApplicationBuilder) (repoRoot: string) =
    let federating = builder.Configuration.GetValue<bool>("Deploy:Federation", true)

    // Publish mode always uses Megafono's published images: refuse the run mode's build-from-source switch before
    // anything is declared, so a misconfigured deploy fails early and names the setting.
    if federating && builder.Configuration.GetValue<bool>("Megafono:BuildFromSource", false) then
        failwith "Megafono:BuildFromSource is not supported by aspire deploy: the deploy uses the published Megafono images at Megafono:ImageTag. Set it to false, or set Deploy:Federation to false."

    let megafonoTag = if federating then megafonoImageTag builder.Configuration else ""

    // Where to deploy: the Caravana console, a deploy token and the registry `aspire deploy` pushes to.
    // The push uses the ambient `docker login`; Caravana pulls from the same registry.
    let caravanaUrl = builder.AddParameter("caravana-url")
    let caravanaToken = builder.AddParameter("caravana-token", secret = true)
    let registryEndpoint = builder.AddParameter("registry-endpoint")
    let registryRepository = builder.AddParameter("registry-repository")

    // What the app needs at runtime. The database password is not the dev "pg-password", and the encryption key
    // (base64 of 32 bytes) encrypts the data protection keys stored in Postgres: losing it signs everyone out.
    let frunchyDomain = builder.AddParameter("frunchy-domain")
    let dbPassword = builder.AddParameter("db-password", secret = true)
    let dataProtectionKey = builder.AddParameter("data-protection-key", secret = true)
    let umbralAuthority = builder.AddParameter("umbral-authority")
    let umbralClientId = builder.AddParameter("umbral-client-id")
    let umbralClientSecret = builder.AddParameter("umbral-client-secret", secret = true)
    let malClientId = builder.AddParameter("mal-client-id", secret = true)
    let rootOperator = builder.AddParameter("root-operator")

    // Megafono's four secrets, declared only when federating so a deploy without it is never prompted for them.
    // Run mode generates the token and webhook secret and keeps them in user secrets; a release runs on a fresh
    // runner, so here all four come from Bella like db-password. The master key is checked before the graph is
    // built (the error names the parameter, never the value).
    let federation =
        if federating then
            let masterKey = builder.AddParameter("megafono-master-key", secret = true)
            let token = builder.AddParameter("megafono-token", secret = true)
            let webhookSecret = builder.AddParameter("megafono-webhook-secret", secret = true)
            let megafonoDbPassword = builder.AddParameter("megafono-db-password", secret = true)
            checkMasterKey builder.Configuration["Parameters:megafono-master-key"]
            Some(masterKey, token, webhookSecret, megafonoDbPassword)
        else
            None

    // The project and environment are plain settings, not parameters: they are not secret and rarely change.
    let setting (key: string) (fallback: string) =
        match builder.Configuration[key] with
        | null
        | "" -> fallback
        | value -> value.Trim()

    let project = setting "Caravana:Project" "frunchy"
    let environment = setting "Caravana:Environment" "production"

    // The tag `aspire deploy` pushes the Frunchy image with: the release version (release.yml sets Deploy__ImageTag
    // from the v* tag, without its "v"). A local deploy has none, so it gets local-<UTC timestamp>: a unique,
    // traceable tag that is never "latest" (Aspire's own default when no tag is set).
    let imageTag =
        match setting "Deploy:ImageTag" "" with
        | "" -> "local-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")
        | "latest" -> failwith "Deploy:ImageTag must be a release version or a unique tag, never latest."
        | tag -> tag

    builder.AddContainerRegistry("registry", registryEndpoint, registryRepository) |> ignore

    // The resource is named "caravana" because "frunchy" is the project's name (resource names are unique); the
    // app's name in Caravana is still "frunchy".
    let caravana =
        builder.PublishToCaravana(
            "caravana",
            fun s ->
                s.AppName <- CaravanaValue.FromLiteral "frunchy"
                s.Url <- caravanaUrl.AsCaravanaValue()
                s.Token <- caravanaToken.AsCaravanaValue()
                s.Project <- CaravanaValue.FromLiteral project
                s.Environment <- CaravanaValue.FromLiteral environment
        )

    // Postgres: in publish mode Aspire does not create databases from AddDatabase, so the image creates
    // POSTGRES_DB on its first start (an existing volume keeps its databases), and Caravana's backup dumps
    // exactly that database. AddDatabase stays for the connection string.
    let pg =
        builder
            .AddPostgres("pg", password = dbPassword)
            .WithDataVolume("frunchy-pg")
            .WithEnvironment("POSTGRES_DB", "frunchydb")
            .WithCaravanaHealthCheck(caravana)
            .WithCaravanaBackup(caravana, CaravanaBackupEngine.Postgres, schedule = "0 3 * * *", keep = 14)
            .WithCaravanaMemory(caravana, 256, 512)

    let db = pg.AddDatabase("frunchydb")

    // Frunchy is declared before Megafono and the gateway so they can point at its endpoint; it is wired to them
    // below. Behind Caravana's Traefik (10.99.0.1 on installer-made hosts, the address its forwarded headers come
    // from) when it has the domain itself, or behind the gateway when federating.
    let frunchyBase = builder.AddProject("frunchy", Path.Combine(repoRoot, "FrunchyBackend.fsproj"))

    // Federation (feature 026): Megafono's database, Megafono and the gateway. Megafono's second port (8081, the
    // internal API Frunchy calls) has no domain; on Caravana its bearer token is its only lock.
    let federated =
        federation
        |> Option.map (fun (masterKey, token, webhookSecret, megafonoDbPassword) ->
            let containerNetwork = setting "Caravana:ContainerNetwork" "172.26.64.0/20"

            // Its own Postgres and its own nightly dump, half an hour after Frunchy's
            let megafonoPg =
                builder
                    .AddPostgres("megafono-pg", password = megafonoDbPassword)
                    .WithDataVolume("megafono-pg")
                    .WithEnvironment("POSTGRES_DB", "megafono")
                    .WithCaravanaHealthCheck(caravana)
                    .WithCaravanaBackup(caravana, CaravanaBackupEngine.Postgres, schedule = "30 3 * * *", keep = 14)
                    .WithCaravanaMemory(caravana, 128, 256)

            let megafonoDb = megafonoPg.AddDatabase("megafono-db", "megafono")

            // 8080 federation (the gateway routes to it), 8081 internal API (never external, never a gateway
            // destination, no domain). The gateway is its trusted proxy: it reaches Megafono from the Caravana
            // container network, so that range is trusted.
            let megafono =
                builder
                    .AddContainer("megafono", "ghcr.io/cosmic-chimps/megafono", megafonoTag)
                    .WithHttpEndpoint(targetPort = 8080, name = "federation")
                    .WithHttpEndpoint(targetPort = 8081, name = "internal")
                    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
                    .WithEnvironment("ConnectionStrings__Database", megafonoDb.Resource.ConnectionStringExpression)
                    .WithEnvironment("Megafono__Domain", frunchyDomain)
                    .WithEnvironment("Megafono__MasterKey", masterKey)
                    .WithEnvironment("Megafono__App__BaseUrl", frunchyBase.GetEndpoint("http"))
                    .WithEnvironment("Megafono__App__Token", token)
                    .WithEnvironment("Megafono__App__WebhookSecret", webhookSecret)
                    .WithEnvironment("Megafono__TrustedProxies", containerNetwork)
                    .WithCaravanaHealthCheck(caravana, "/health", 8081)
                    .WithCaravanaMemory(caravana, 256, 512)
                    .WaitFor(megafonoDb)

            // The single public entrance: Megafono's routes go to Megafono, everything else to Frunchy. Traefik
            // reaches it from 10.99.0.1. It has no health endpoint of its own (any path is proxied), so the check is TCP.
            builder
                .AddContainer("gateway", "ghcr.io/cosmic-chimps/megafono-gateway", megafonoTag)
                .WithHttpEndpoint(targetPort = 8080, name = "http")
                .WithExternalHttpEndpoints()
                .WithEnvironment("Gateway__TrustedProxies", "10.99.0.1")
                .WithEnvironment(
                    "ReverseProxy__Clusters__megafono__Destinations__primary__Address",
                    megafono.GetEndpoint("federation")
                )
                .WithEnvironment(
                    "ReverseProxy__Clusters__app__Destinations__primary__Address",
                    frunchyBase.GetEndpoint("http")
                )
                .WithCaravanaDomain(caravana, frunchyDomain)
                .WithCaravanaHealthCheck(caravana, port = 8080)
                .WithCaravanaMemory(caravana, 64, 128)
                .WaitFor(megafono)
                .WaitFor(frunchyBase)
            |> ignore

            containerNetwork, megafono, token, webhookSecret)

    // Trusted forwarded headers: Traefik's address when Frunchy is the entrance, the container network when the
    // gateway (a sibling container) is
    let trustedProxies =
        match federated with
        | Some(containerNetwork, _, _, _) -> containerNetwork
        | None -> "10.99.0.1"

    // Only an entrance is external; with federation on that is the gateway
    let frunchyEntrance = if federated.IsNone then frunchyBase.WithExternalHttpEndpoints() else frunchyBase

    // The connection string holds the database password: the publisher moves it into Caravana secrets.
    let frunchy =
        frunchyEntrance
            .WithEnvironment("ConnectionStrings__Database", db.Resource.ConnectionStringExpression)
            .WithEnvironment("Frunchy__DataProtection__EncryptionKey", dataProtectionKey)
            .WithEnvironment("Frunchy__TrustedProxies", trustedProxies)
            .WithEnvironment("Frunchy__Auth__Providers__Umbral__Enabled", "true")
            .WithEnvironment("Frunchy__Auth__Providers__Umbral__Authority", umbralAuthority)
            .WithEnvironment("Frunchy__Auth__Providers__Umbral__ClientId", umbralClientId)
            .WithEnvironment("Frunchy__Auth__Providers__Umbral__ClientSecret", umbralClientSecret)
            .WithEnvironment("Frunchy__Mal__ClientId", malClientId)
            .WithEnvironment("Frunchy__RootOperator", rootOperator)

    match federated with
    | Some(_, megafono, token, webhookSecret) ->
        // Federation on: Frunchy has no domain of its own (the gateway has it) and is not external
        frunchy
            .WithEnvironment("Frunchy__Federation__Enabled", "true")
            .WithEnvironment("Frunchy__Federation__Domain", frunchyDomain)
            .WithEnvironment("Frunchy__Megafono__Url", megafono.GetEndpoint("internal"))
            .WithEnvironment("Frunchy__Megafono__Token", token)
            .WithEnvironment("Frunchy__Megafono__WebhookSecret", webhookSecret)
            .WithCaravanaHealthCheck(caravana, "/healthz")
            .WithCaravanaMemory(caravana, 256, 768)
            .WithRemoteImageTag(imageTag)
            .WaitFor(db)
        |> ignore
    | None ->
        // Federation off (its default in the app): the 018 graph
        frunchy
            .WithCaravanaDomain(caravana, frunchyDomain)
            .WithCaravanaHealthCheck(caravana, "/healthz")
            .WithCaravanaMemory(caravana, 256, 768)
            .WithRemoteImageTag(imageTag)
            .WaitFor(db)
        |> ignore

/// The local platform (feature 014), unchanged: what `aspire run` and `dotnet run --project AppHost` start.
let private runGraph (builder: IDistributedApplicationBuilder) (repoRoot: string) =
    // Parameters (user secrets "Parameters:<name>"); the two shared secrets are generated once and persisted
    let token = builder.AddParameter("megafono-token", generated (), secret = true, persist = true)
    let webhookSecret = builder.AddParameter("megafono-webhook-secret", generated (), secret = true, persist = true)
    let masterKey = builder.AddParameter("megafono-master-key", secret = true)
    // "domain" has no generated default: read the setting here and fall back to frunchy.test
    let domainValue =
        match builder.Configuration["Parameters:domain"] with
        | null
        | "" -> "frunchy.test"
        | value -> value.Trim()

    let domain = builder.AddParameter("domain", domainValue)

    checkMasterKey builder.Configuration["Parameters:megafono-master-key"]

    // Megafono: the published images at a pinned tag (Megafono:ImageTag, never "latest"), or built from a local
    // checkout when Megafono:BuildFromSource is true (to try an unreleased Megafono)
    let buildFromSource = builder.Configuration.GetValue<bool>("Megafono:BuildFromSource", false)

    let imageTag = if buildFromSource then "" else megafonoImageTag builder.Configuration

    let useImage = not buildFromSource

    let sourcePath =
        match builder.Configuration["Megafono:SourcePath"] with
        | null
        | "" -> "../../Megafono"
        | path -> path
        |> fun path -> Path.GetFullPath(path, repoRoot)

    if not useImage && not (File.Exists(Path.Combine(sourcePath, "deploy", "Dockerfile.megafono"))) then
        failwithf
            "Megafono's source was not found at %s. Check it out there, or set Megafono:SourcePath (AppHost/appsettings.json or user secrets), or turn Megafono:BuildFromSource off to use the published images."
            sourcePath

    // Umbral (optional): our own OpenID Connect provider. Like Megafono: the published image at a pinned tag
    // (Umbral:ImageTag, never "latest"), or built from a local checkout when Umbral:BuildFromSource is true.
    // In source mode a missing checkout skips Umbral with a warning: Frunchy still runs (Google may be set up through Bella).
    let umbralEnabled = builder.Configuration.GetValue<bool>("Umbral:Enabled", false)
    let umbralBuildFromSource = builder.Configuration.GetValue<bool>("Umbral:BuildFromSource", false)

    let umbralImageTag =
        match builder.Configuration["Umbral:ImageTag"] with
        | null -> ""
        | tag -> tag.Trim()

    if umbralEnabled && not umbralBuildFromSource && (umbralImageTag = "" || umbralImageTag = "latest") then
        failwith "Set Umbral:ImageTag to a pinned Umbral release (e.g. 0.1.1, never latest), or Umbral:BuildFromSource to true, or Umbral:Enabled to false."

    let umbralPort =
        match builder.Configuration.GetValue<int>("Umbral:Port", 5380) with
        | 0 -> 5380
        | port -> port

    let gatewayPort =
        match builder.Configuration.GetValue<int>("Gateway:Port", 5300) with
        | 0 -> 5300
        | port -> port

    let umbralSourcePath =
        match builder.Configuration["Umbral:SourcePath"] with
        | null
        | "" -> "../../Umbral"
        | path -> path
        |> fun path -> Path.GetFullPath(path, repoRoot)

    let umbralAvailable =
        if not umbralEnabled then
            false
        elif not umbralBuildFromSource then
            true
        elif File.Exists(Path.Combine(umbralSourcePath, "Dockerfile")) then
            true
        else
            eprintfn
                "WARNING: Umbral is skipped: no Dockerfile at %s. Check Umbral out there, or set Umbral:SourcePath (AppHost/appsettings.json or user secrets), or turn Umbral:BuildFromSource off to use the published image, or set Umbral:Enabled to false to silence this."
                umbralSourcePath

            false

    // Postgres: one server, one database per app
    let pg = builder.AddPostgres("pg").WithDataVolume("frunchy-pg")
    let frunchyDb = pg.AddDatabase("frunchydb")
    let megafonoDb = pg.AddDatabase("megafono-db", "megafono")

    // Frunchy is declared first so Megafono and the gateway can point at its endpoint; it is wired to them below
    let frunchy = builder.AddProject("frunchy", Path.Combine(repoRoot, "FrunchyBackend.fsproj"))

    let megafonoBase =
        if useImage then
            builder.AddContainer("megafono", "ghcr.io/cosmic-chimps/megafono", imageTag)
        else
            builder.AddDockerfile("megafono", sourcePath, "deploy/Dockerfile.megafono")

    let megafono =
        megafonoBase
            // 8080 federation (the gateway routes to it), 8081 internal API (never external, never a gateway destination)
            .WithHttpEndpoint(targetPort = 8080, name = "federation")
            .WithHttpEndpoint(targetPort = 8081, name = "internal")
            .WithEnvironment("ConnectionStrings__Database", megafonoDb.Resource.ConnectionStringExpression)
            .WithEnvironment("Megafono__Domain", domain)
            .WithEnvironment("Megafono__MasterKey", masterKey)
            .WithEnvironment("Megafono__App__BaseUrl", frunchy.GetEndpoint("http"))
            .WithEnvironment("Megafono__App__Token", token)
            .WithEnvironment("Megafono__App__WebhookSecret", webhookSecret)
            // Container-network addresses of the gateway in dev: any private range
            .WithEnvironment("Megafono__TrustedProxies", privateRanges)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            // Dev only (refused in Production): lets Megafono reach frunchy.test-style private hosts over http
            .WithEnvironment("Megafono__Federation__AllowPrivateAddresses", "true")
            .WithHttpHealthCheck(path = "/health", endpointName = "internal")
            .WaitFor(megafonoDb)

    // The gateway is the single public entrance: Megafono's routes go to Megafono, everything else to Frunchy
    let gatewayBuilder =
        if useImage then
            builder.AddContainer("gateway", "ghcr.io/cosmic-chimps/megafono-gateway", imageTag)
        else
            builder.AddDockerfile("gateway", sourcePath, "deploy/Dockerfile.gateway")

    let gateway =
        gatewayBuilder
            // The host port is pinned so the redirect URI registered once in Umbral (/signin-umbral) stays valid
            .WithHttpEndpoint(port = gatewayPort, targetPort = 8080, name = "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment(
                "ReverseProxy__Clusters__megafono__Destinations__primary__Address",
                megafono.GetEndpoint("federation")
            )
            .WithEnvironment("ReverseProxy__Clusters__app__Destinations__primary__Address", frunchy.GetEndpoint("http"))
            .WaitFor(megafono)
            .WaitFor(frunchy)

    gateway |> ignore

    // Frunchy: a host process. Loopback and the private ranges are trusted, since the gateway reaches it from
    // a container (through the host's docker address) and the Aspire proxy connects from loopback.
    frunchy
        .WithEnvironment("ConnectionStrings__Database", frunchyDb.Resource.ConnectionStringExpression)
        .WithEnvironment("Frunchy__Federation__Enabled", "true")
        .WithEnvironment("Frunchy__Federation__Domain", domain)
        .WithEnvironment("Frunchy__Megafono__Url", megafono.GetEndpoint("internal"))
        .WithEnvironment("Frunchy__Megafono__Token", token)
        .WithEnvironment("Frunchy__Megafono__WebhookSecret", webhookSecret)
        .WithEnvironment("Frunchy__TrustedProxies", $"127.0.0.1,::1,{privateRanges}")
        .WaitFor(frunchyDb)
    |> ignore

    // Umbral: the container (dev settings: http on localhost, the fake connector) and, once the app is registered
    // in its admin and the client credentials are in user secrets, Frunchy's Umbral provider. No secret is printed.
    if umbralAvailable then
        let umbralBase =
            if umbralBuildFromSource then
                builder.AddDockerfile("umbral", umbralSourcePath)
            else
                builder.AddContainer("umbral", "ghcr.io/cosmic-chimps/umbral", umbralImageTag)

        let umbral =
            umbralBase
                // The port is pinned and the origin is localhost (not 127.0.0.1): it is the issuer in every token
                .WithHttpEndpoint(port = umbralPort, targetPort = 8080, name = "http")
                .WithEnvironment("UMBRAL__PUBLIC_ORIGIN", $"http://localhost:{umbralPort}")
                .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                .WithEnvironment("UMBRAL__INSECURE_ALLOW_HTTP", "true")
                // Dev only: offers a "fake" connector in the admin. Umbral refuses a stored fake connector without it.
                .WithEnvironment("UMBRAL__ALLOW_FAKE_CONNECTOR", "true")
                // Operator credential, apps, connectors and signing keys: wiping it means claiming and registering again
                .WithVolume("umbral-data", "/data")
                .WithHttpHealthCheck(path = "/healthz")

        let isSet (name: string) =
            not (String.IsNullOrWhiteSpace builder.Configuration[$"Parameters:{name}"])

        if isSet "umbral-client-id" && isSet "umbral-client-secret" then
            // Added only now: an unresolved parameter would make Aspire prompt for it
            let clientId = builder.AddParameter("umbral-client-id")
            let clientSecret = builder.AddParameter("umbral-client-secret", secret = true)

            frunchy
                .WithEnvironment("Frunchy__Auth__Providers__Umbral__Enabled", "true")
                .WithEnvironment("Frunchy__Auth__Providers__Umbral__Authority", $"http://localhost:{umbralPort}")
                .WithEnvironment("Frunchy__Auth__Providers__Umbral__ClientId", clientId)
                .WithEnvironment("Frunchy__Auth__Providers__Umbral__ClientSecret", clientSecret)
                .WaitFor(umbral)
            |> ignore
        else
            printfn "%s" (umbralHint umbralPort gatewayPort)

[<EntryPoint>]
let main args =
    let builder = DistributedApplication.CreateBuilder(args)

    // Frunchy's repository root: AppHost/ sits directly under it
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

    // `aspire deploy` and `aspire publish` run in publish mode; everything else is the local platform
    if builder.ExecutionContext.IsPublishMode then
        publishGraph builder repoRoot
    else
        runGraph builder repoRoot

    builder.Build().Run()
    0
