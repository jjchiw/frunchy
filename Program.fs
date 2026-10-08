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
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Localization
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open AspNet.Security.OAuth.Discord
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

/// Provider round-trip settings shared by Google and Discord:
/// - `prompt=` on the authorization URL when a round trip asks for it (re-confirmation);
/// - a failed or cancelled round trip (the user pressed Cancel, a reloaded callback, "Correlation
///   failed") is logged and lands on the sign-in page's "try again" message, or on the account page
///   for link and re-confirm, never on an exception page (contracts/web-account.md "Messages");
/// - the correlation cookie is SameSite=Lax and secure only on https. The default (None + Secure)
///   breaks over plain http in some browsers; Lax cookies are still sent on the provider's
///   top-level redirect back.
let private roundTrip (options: OAuthOptions) =
    options.CorrelationCookie.SameSite <- SameSiteMode.Lax
    options.CorrelationCookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest

    options.Events.OnRedirectToAuthorizationEndpoint <-
        fun context ->
            match context.Properties.Items.TryGetValue WebAuth.PromptKey with
            | true, prompt when not (String.IsNullOrEmpty prompt) ->
                context.Response.Redirect(context.RedirectUri + "&prompt=" + Uri.EscapeDataString prompt)
            | _ -> context.Response.Redirect context.RedirectUri

            Task.CompletedTask

    options.Events.OnRemoteFailure <-
        fun context ->
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
        | "Discord" ->
            auth.AddDiscord(
                "discord",
                fun o ->
                    o.SignInScheme <- WebAuth.ExternalScheme
                    o.CallbackPath <- PathString "/signin-discord"
                    o.Scope.Clear()
                    o.Scope.Add "identify" // only the stable id is used (FR-021)
                    roundTrip o
            )
            |> ignore

            bindCredentials<DiscordAuthenticationOptions> config services "discord" "Discord"
            services.AddSingleton<AuthConfig.FrunchyProvider>(AuthConfig.provider "discord" "Discord" "discord" (Some "consent"))
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
/// (`Frunchy__Auth__Providers__Discord__ClientSecret`). Bella overrides appsettings and user
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
      Clock: TimeProvider }

let private dbOnly (run: DbConfig -> string list -> Task<int>) = fun (env: CliEnv) args -> run env.Db args

// Operator CLI: `dotnet run -- <command> [args]` (contracts/operator-cli.md in specs 001, 002, 003)
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
          "mal-rebuild", dbOnly Operations.MalRebuild.run ]

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
          Clock = TimeProvider.System }

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
