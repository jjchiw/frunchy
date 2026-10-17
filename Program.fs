module FrunchyBackend.Program

open System
open System.Globalization
open System.IO
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.Google
open Microsoft.AspNetCore.Authentication.OAuth
open Microsoft.AspNetCore.Authentication.OpenIdConnect
open Microsoft.IdentityModel.Protocols.OpenIdConnect
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Localization
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open BellaBaxter.AspNet.Configuration
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Workers.ImportProcessor

/// Set to false (the integration tests do) to run without the background import worker
[<Literal>]
let WorkerEnabledKey = "Frunchy:ImportWorkerEnabled"

/// Set to false (the integration tests do) to run without the MyAnimeList enrichment worker
[<Literal>]
let EnrichmentWorkerEnabledKey = "Frunchy:EnrichmentWorkerEnabled"

// One vertical slice per endpoint, see Handlers/
let endpoints =
    [ Handlers.Home.endpoint
      Handlers.Api.CreateImport.endpoint
      Handlers.Api.GetImportStatus.endpoint
      Handlers.Api.WhoAmI.endpoint
      Handlers.Users.Dashboard.endpoint
      Handlers.Users.Dna.endpoint
      yield! Handlers.Users.Year.endpoint
      Handlers.Users.Anime.endpoint
      // Feature 011: taste compatibility with another user (signed in)
      Handlers.Users.Compare.endpoint
      // Feature 012: charts and trends (anyone)
      yield! Handlers.Charts.Charts.endpoint
      // Feature 013: the public series page
      Handlers.Series.Series.endpoint
      // Feature 009: the episode page and its comments
      yield! Handlers.Episodes.Episode.endpoint
      yield! Handlers.Episodes.EditComment.endpoint
      Handlers.Episodes.DeleteComment.endpoint
      yield! Handlers.Episodes.HideComment.endpoint
      // Feature 007: operators review MyAnimeList matches (404 for everyone else)
      yield! Handlers.Review.MalReview.endpoint
      // Feature 008: the root operator manages operators (404 for everyone else)
      yield! Handlers.Review.Operators.endpoint
      // Feature 002: sign-in and the private account pages
      Handlers.Auth.SignIn.endpoint
      Handlers.Auth.Complete.endpoint
      yield! Handlers.Auth.SignUp.endpoint
      Handlers.Auth.SignOut.endpoint
      yield! Handlers.Auth.Claim.endpoint
      Handlers.Auth.Reconfirm.endpoint
      Handlers.Account.AccountPage.endpoint
      yield! Handlers.Account.SyncKey.endpoint
      Handlers.Account.DisplayName.endpoint
      Handlers.Account.ResetBinding.endpoint
      yield! Handlers.Account.Logins.endpoint
      yield! Handlers.Account.DeleteAccount.endpoint ]

let private isApi (ctx: HttpContext) =
    ctx.Request.Path.StartsWithSegments(PathString "/api")

let notFoundHandler (ctx: HttpContext) =
    ctx.GetLogger().LogInformation("404 {Path}", ctx.Request.Path)

    if isApi ctx then
        ApiErrors.notFound ctx
    else
        let t = Texts.current ()
        ctx.SetStatusCode StatusCodes.Status404NotFound
        ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

let errorHandler (ctx: HttpContext) (next: RequestDelegate) =
    task {
        try
            return! next.Invoke(ctx)
        with
        | :? BadHttpRequestException as ex when ex.StatusCode = StatusCodes.Status413PayloadTooLarge ->
            return!
                ApiErrors.writeError ctx StatusCodes.Status413PayloadTooLarge ApiErrors.PayloadTooLarge "Request body is too large."
        | :? AntiforgeryValidationException as ex ->
            // A web form posted without (or with an expired) antiforgery token: nothing was changed
            ctx.GetLogger().LogWarning(ex, "Antiforgery validation failed on {Path}", ctx.Request.Path)
            let t = Texts.current ()
            ctx.SetStatusCode StatusCodes.Status400BadRequest
            return! ctx.WriteHtmlView(Layout.messagePage ctx t t.FormExpiredTitle t.FormExpiredBody)
        | ex ->
            ctx.GetLogger().LogError(ex, "Unhandled error on {Path}", ctx.Request.Path)
            ctx.SetStatusCode StatusCodes.Status500InternalServerError

            if isApi ctx then
                return! ApiErrors.writeError ctx StatusCodes.Status500InternalServerError "server_error" "Unexpected server error."
            else
                let t = Texts.current ()
                return! ctx.WriteHtmlView(Layout.messagePage ctx t t.ErrorTitle t.ErrorBody)
    }
    :> Task

/// English is the default; Spanish when the browser prefers it. Only Accept-Language decides
/// (no query/cookie override until a language switcher exists).
let localizationOptions () =
    let cultures = [| CultureInfo "en"; CultureInfo "es" |]

    let options =
        RequestLocalizationOptions(
            DefaultRequestCulture = RequestCulture "en",
            SupportedCultures = cultures,
            SupportedUICultures = cultures
        )

    options.RequestCultureProviders <-
        ResizeArray<IRequestCultureProvider>([ AcceptLanguageHeaderRequestCultureProvider() :> IRequestCultureProvider ])
    options

let dbConfigFrom (config: IConfiguration) : DbConfig =
    { connectionString = config.GetConnectionString "Database" }

/// Provider round-trip settings shared by every provider (the OAuth and OpenID Connect handlers only
/// share RemoteAuthenticationOptions, so `roundTrip` and `oidcRoundTrip` wire their own events):
/// - `prompt=` on the authorization URL when a round trip asks for it (re-confirmation);
/// - a failed or cancelled round trip (the user pressed Cancel, a reloaded callback, "Correlation
///   failed") is logged and lands on the sign-in page's "try again" message, or on the account page
///   for link and re-confirm, never on an exception page (contracts/web-account.md "Messages");
/// - the correlation cookie is SameSite=Lax and secure only on https. The default (None + Secure)
///   breaks over plain http in some browsers; Lax cookies are still sent on the provider's
///   top-level redirect back.
let private laxCorrelation (options: RemoteAuthenticationOptions) =
    options.CorrelationCookie.SameSite <- SameSiteMode.Lax
    options.CorrelationCookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest

let private promptOf (properties: AuthenticationProperties) =
    match properties.Items.TryGetValue WebAuth.PromptKey with
    | true, prompt when not (String.IsNullOrEmpty prompt) -> Some prompt
    | _ -> None

let private remoteFailed (context: RemoteFailureContext) : Task =
    context.HttpContext
        .GetLogger("Frunchy.SignIn")
        .LogWarning(context.Failure, "Sign-in with {Scheme} failed: {Reason}", context.Scheme.Name, context.Failure.Message)

    let mode =
        match context.Properties with
        | null -> None
        | p ->
            match p.Items.TryGetValue WebAuth.ModeKey with
            | true, m -> Some m
            | _ -> None

    context.Response.Redirect(
        match mode with
        | Some "link" -> "/account?msg=link-failed"
        | Some "reconfirm" -> "/account?msg=reconfirm-failed"
        | _ -> "/signin?error=failed"
    )

    context.HandleResponse()
    Task.CompletedTask

/// Google (OAuth handler)
let private roundTrip (options: OAuthOptions) =
    laxCorrelation options

    options.Events.OnRedirectToAuthorizationEndpoint <-
        fun context ->
            match promptOf context.Properties with
            | Some prompt -> context.Response.Redirect(context.RedirectUri + "&prompt=" + Uri.EscapeDataString prompt)
            | None -> context.Response.Redirect context.RedirectUri

            Task.CompletedTask

    options.Events.OnRemoteFailure <- Func<RemoteFailureContext, Task> remoteFailed

/// Umbral (OpenID Connect handler). Its nonce cookie gets the correlation cookie's settings, for the
/// same reason.
let private oidcRoundTrip (options: OpenIdConnectOptions) =
    laxCorrelation options
    options.NonceCookie.SameSite <- SameSiteMode.Lax
    options.NonceCookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest

    options.Events.OnRedirectToIdentityProvider <-
        fun context ->
            promptOf context.Properties
            |> Option.iter (fun prompt -> context.ProtocolMessage.Prompt <- prompt)

            Task.CompletedTask

    options.Events.OnRemoteFailure <- Func<RemoteFailureContext, Task> remoteFailed

/// A provider's ClientId and ClientSecret are bound from its configuration section instead of
/// copied once, so a secret rotated in Bella Baxter is used on the next sign-in, without a restart
let private bindCredentials<'T when 'T: not struct> (config: IConfiguration) (services: IServiceCollection) (scheme: string) (name: string) =
    services.Configure<'T>(scheme, config.GetSection $"Frunchy:Auth:Providers:{name}") |> ignore

/// Feature 002: browser sign-in with session + external cookies and the enabled providers (research R1, R2, R11)
let configureAuth (config: IConfiguration) (services: IServiceCollection) =
    let settings = AuthConfig.read config

    match AuthConfig.validate settings with
    | Error message -> raise (InvalidOperationException message)
    | Ok() -> ()

    let auth =
        services
            .AddAuthentication(WebAuth.SessionScheme)
            .AddCookie(
                WebAuth.SessionScheme,
                fun o ->
                    o.Cookie.Name <- WebAuth.SessionScheme
                    o.Cookie.HttpOnly <- true
                    o.Cookie.SameSite <- SameSiteMode.Lax
                    o.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
                    o.ExpireTimeSpan <- TimeSpan.FromDays 30.0
                    o.SlidingExpiration <- true
                    o.LoginPath <- PathString "/signin"
                    o.Events.OnValidatePrincipal <- fun context -> WebAuth.validatePrincipal context
            )
            .AddCookie(
                WebAuth.ExternalScheme,
                fun o ->
                    o.Cookie.Name <- WebAuth.ExternalScheme
                    o.Cookie.HttpOnly <- true
                    o.Cookie.SameSite <- SameSiteMode.Lax
                    o.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
                    o.ExpireTimeSpan <- TimeSpan.FromMinutes 10.0
            )

    for provider in settings |> List.filter _.Enabled do
        match provider.Name with
        | "Google" ->
            auth.AddGoogle(
                "google",
                fun o ->
                    o.SignInScheme <- WebAuth.ExternalScheme
                    o.CallbackPath <- PathString "/signin-google"
                    o.Scope.Clear()
                    o.Scope.Add "openid" // only the stable id is used (FR-021)
                    roundTrip o
            )
            |> ignore

            bindCredentials<GoogleOptions> config services "google" "Google"
            services.AddSingleton<AuthConfig.FrunchyProvider>(AuthConfig.provider "google" "Google" "google" (Some "select_account"))
            |> ignore
        | "Umbral" ->
            match AuthConfig.validateAuthority "Umbral" config["Frunchy:Auth:Providers:Umbral:Authority"] with
            | Error message -> raise (InvalidOperationException message)
            | Ok() -> ()

            auth.AddOpenIdConnect(
                "umbral",
                fun o ->
                    o.SignInScheme <- WebAuth.ExternalScheme
                    o.CallbackPath <- PathString "/signin-umbral"
                    o.ResponseType <- OpenIdConnectResponseType.Code // the only flow Umbral offers
                    o.UsePkce <- true // required by Umbral (S256, the handler's method)
                    // Back on a top-level GET like Google, so the Lax cookies are sent
                    // (the default, form_post, is a cross-site POST in production, which drops them)
                    o.ResponseMode <- OpenIdConnectResponseMode.Query
                    o.MapInboundClaims <- true // sub → ClaimTypes.NameIdentifier, what WebAuth.tryExternalResult reads
                    o.Scope.Clear()
                    o.Scope.Add "openid" // only the stable id is used (FR-021)
                    oidcRoundTrip o
            )
            |> ignore

            // Binds ClientId, ClientSecret and Authority
            bindCredentials<OpenIdConnectOptions> config services "umbral" "Umbral"

            // After the binding, so it judges the bound Authority (a rotated one too)
            services.Configure<OpenIdConnectOptions>(
                "umbral",
                Action<OpenIdConnectOptions>(fun o -> o.RequireHttpsMetadata <- not (AuthConfig.allowsHttpMetadata o.Authority))
            )
            |> ignore

            services.AddSingleton<AuthConfig.FrunchyProvider>(AuthConfig.provider "umbral" "Umbral" "umbral" (Some "login"))
            |> ignore
        | _ -> ()

    services.AddAntiforgery() |> ignore

    match config["Frunchy:DataProtection:KeysPath"] with
    | null
    | "" -> ()
    | path -> services.AddDataProtection().PersistKeysToFileSystem(DirectoryInfo path) |> ignore

    // Built-in rate limiting, fixed windows per client IP (research R8)
    services.AddRateLimiter(fun options ->
        let perIp (name: string) (permits: int) (window: TimeSpan) =
            options.AddPolicy(
                name,
                fun (ctx: HttpContext) ->
                    let ip =
                        match ctx.Connection.RemoteIpAddress with
                        | null -> "unknown"
                        | address -> string address

                    RateLimitPartition.GetFixedWindowLimiter(
                        ip,
                        fun _ -> FixedWindowRateLimiterOptions(PermitLimit = permits, Window = window, QueueLimit = 0)
                    )
            )
            |> ignore

        perIp "signin" 30 (TimeSpan.FromMinutes 1.0)
        perIp "signup" 10 (TimeSpan.FromMinutes 15.0)
        perIp "claim" 5 (TimeSpan.FromMinutes 15.0)
        perIp "account-write" 30 (TimeSpan.FromMinutes 1.0)
        // Writing comments (feature 009): create, edit and delete share it
        perIp "comment-write" 10 (TimeSpan.FromMinutes 1.0)
        // Review actions (feature 007) count operators only: anyone else gets the handler's 404 and is
        // never rate-limited there, so a 429 can't reveal that the routes exist
        options.AddPolicy(
            "review-write",
            fun (ctx: HttpContext) ->
                if WebAuth.isOperator ctx then
                    let ip =
                        match ctx.Connection.RemoteIpAddress with
                        | null -> "unknown"
                        | address -> string address

                    RateLimitPartition.GetFixedWindowLimiter(
                        "review:" + ip,
                        fun _ -> FixedWindowRateLimiterOptions(PermitLimit = 30, Window = TimeSpan.FromMinutes 1.0, QueueLimit = 0)
                    )
                else
                    RateLimitPartition.GetNoLimiter "review:outsider"
        )
        |> ignore

        // Operator changes (feature 008) count the root only, for the same reason
        options.AddPolicy(
            "operators-write",
            fun (ctx: HttpContext) ->
                if WebAuth.isRoot ctx then
                    let ip =
                        match ctx.Connection.RemoteIpAddress with
                        | null -> "unknown"
                        | address -> string address

                    RateLimitPartition.GetFixedWindowLimiter(
                        "operators:" + ip,
                        fun _ -> FixedWindowRateLimiterOptions(PermitLimit = 10, Window = TimeSpan.FromMinutes 1.0, QueueLimit = 0)
                    )
                else
                    RateLimitPartition.GetNoLimiter "operators:outsider"
        )
        |> ignore

        options.RejectionStatusCode <- StatusCodes.Status429TooManyRequests

        options.OnRejected <-
            fun context _ ->
                let t = Texts.current ()
                let ctx = context.HttpContext
                ctx.Response.StatusCode <- StatusCodes.Status429TooManyRequests
                ValueTask(ctx.WriteHtmlView(Layout.messagePage ctx t t.TooManyRequestsTitle t.TooManyRequestsBody)))
    |> ignore

let configureServices (config: IConfiguration) (services: IServiceCollection) =
    services.AddSingleton<DbConfig>(dbConfigFrom config) |> ignore
    services.AddMemoryCache() |> ignore

    if config.GetValue<bool>(WorkerEnabledKey, true) then
        services.AddHostedService<ImportProcessorService>() |> ignore

    // Feature 003: the paced, retried MyAnimeList client and the enrichment worker
    services.TryAddSingleton<TimeProvider>(TimeProvider.System)
    MalClient.register config services |> ignore

    if config.GetValue<bool>(EnrichmentWorkerEnabledKey, true) then
        services.AddHostedService<Workers.Enrichment.EnrichmentService>() |> ignore

    configureAuth config services
    services.AddRouting().AddOxpecker() |> ignore

let configureApp (app: WebApplication) =
    app
        .UseRequestLocalization(localizationOptions ())
        .UseRouting()
        .UseAuthentication()
        .UseRateLimiter()
        .UseAntiforgery()
        .Use(errorHandler)
        .UseOxpecker(endpoints)
        .Run(notFoundHandler)

/// Bella Baxter (our own secrets manager, dogfooded): when Bella credentials are present, from
/// `bella sdk run` or `BellaBaxter:ApiKey` in user secrets, the environment's secrets join the
/// configuration and are polled for changes. Secret keys use `__` for sections
/// (`Frunchy__Auth__Providers__Google__ClientSecret`). Bella overrides appsettings and user
/// secrets; environment variables and the command line still win. Without credentials (plain
/// `dotnet run`, the tests) nothing is added.
let addBellaSecrets (args: string array) (config: ConfigurationManager) =
    let isSet (value: string) = not (String.IsNullOrEmpty value)

    if isSet (Environment.GetEnvironmentVariable "BELLA_BAXTER_API_KEY")
       || isSet (Environment.GetEnvironmentVariable "BELLA_BAXTER_ACCESS_TOKEN")
       || isSet config["BellaBaxter:ApiKey"] then
        config.AddBellaSecrets(fun o -> o.AppClient <- "frunchy-backend") |> ignore
        config.AddEnvironmentVariables().AddCommandLine(args) |> ignore

/// The web app, composable so the integration tests can host it on a TestServer
let buildApp (args: string array) (customize: WebApplicationBuilder -> unit) : WebApplication =
    let builder = WebApplication.CreateBuilder(args)
    addBellaSecrets args builder.Configuration
    customize builder
    configureServices builder.Configuration builder.Services
    let app = builder.Build()
    configureApp app
    app

/// What an operator command can use: the database and, for mal-set, the paced MyAnimeList client
type CliEnv =
    { Db: DbConfig
      Http: Net.Http.IHttpClientFactory
      Mal: MalClient.MalSettings
      Clock: TimeProvider
      /// `Frunchy:RootOperator`: the commands refuse to add or remove it
      RootOperator: string option }

let private dbOnly (run: DbConfig -> string list -> Task<int>) = fun (env: CliEnv) args -> run env.Db args

// Operator CLI: `dotnet run -- <command> [args]` (contracts/operator-cli.md in specs 001, 002, 003; operators in 008)
let cliCommands: Map<string, CliEnv -> string list -> Task<int>> =
    Map
        [ "user-create", dbOnly Operations.CreateUser.run
          "user-regenerate-key", dbOnly Operations.RegenerateKey.run
          "user-reset-binding", dbOnly Operations.ResetBinding.run
          "user-rebuild", dbOnly Operations.RebuildUser.run
          "user-delete", dbOnly Operations.DeleteUser.run
          "user-list", dbOnly Operations.ListUsers.run
          "invite-create", dbOnly Operations.InviteCreate.run
          "invite-list", dbOnly Operations.InviteList.run
          "invite-revoke", dbOnly Operations.InviteRevoke.run
          "mal-status", dbOnly Operations.MalStatus.run
          "mal-list", dbOnly Operations.MalList.run
          "mal-show", dbOnly Operations.MalShow.run
          "mal-set", (fun env args -> Operations.MalSet.run env.Db env.Http env.Clock env.Mal args)
          "mal-none", dbOnly Operations.MalNone.run
          "mal-retry", dbOnly Operations.MalRetry.run
          "mal-rebuild", dbOnly Operations.MalRebuild.run
          "mal-auto", dbOnly Operations.MalAuto.run
          "operator-add", (fun env args -> Operations.OperatorAdd.run env.Db env.RootOperator args)
          "operator-remove", (fun env args -> Operations.OperatorRemove.run env.Db env.RootOperator args)
          "operator-list", (fun env args -> Operations.OperatorList.run env.Db env.RootOperator args) ]

let private runCli (command: string) (rest: string list) : int =
    // Same configuration sources as the web app (appsettings, environment, user secrets, Bella)
    let config = WebApplication.CreateBuilder(Array.empty).Configuration
    addBellaSecrets Array.empty config
    let db = dbConfigFrom config
    let services = ServiceCollection()
    MalClient.register config services |> ignore
    use provider = services.BuildServiceProvider()

    let env =
        { Db = db
          Http = provider.GetRequiredService<Net.Http.IHttpClientFactory>()
          Mal = MalClient.readSettings config
          Clock = TimeProvider.System
          RootOperator = WebAuth.rootOperator config }

    task {
        do! initializeSchema db
        return! cliCommands[command] env rest
    }
    |> fun t -> t.GetAwaiter().GetResult()

[<EntryPoint>]
let main args =
    match List.ofArray args with
    | command :: rest when cliCommands.ContainsKey command -> runCli command rest
    | _ ->
        let app = buildApp args ignore

        task {
            do! initializeSchema (app.Services.GetRequiredService<DbConfig>())
            do! app.RunAsync()
        }
        |> fun t -> t.GetAwaiter().GetResult()

        0
