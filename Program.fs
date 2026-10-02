module FrunchyBackend.Program

open System
open System.Globalization
open System.IO
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Authentication.OAuth
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Localization
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Workers.ImportProcessor

/// Set to false (the integration tests do) to run without the background import worker
[<Literal>]
let WorkerEnabledKey = "Frunchy:ImportWorkerEnabled"

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
      yield! Handlers.Account.Logins.endpoint ]

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

/// Adds `prompt=` to the provider's authorization URL when a round trip asks for it (re-confirmation)
let private withPrompt (events: OAuthEvents) =
    events.OnRedirectToAuthorizationEndpoint <-
        fun context ->
            match context.Properties.Items.TryGetValue WebAuth.PromptKey with
            | true, prompt when not (String.IsNullOrEmpty prompt) ->
                context.Response.Redirect(context.RedirectUri + "&prompt=" + Uri.EscapeDataString prompt)
            | _ -> context.Response.Redirect context.RedirectUri

            Task.CompletedTask

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
                    o.ClientId <- provider.ClientId
                    o.ClientSecret <- provider.ClientSecret
                    o.SignInScheme <- WebAuth.ExternalScheme
                    o.CallbackPath <- PathString "/signin-google"
                    o.Scope.Clear()
                    o.Scope.Add "openid" // only the stable id is used (FR-021)
                    withPrompt o.Events
            )
            |> ignore

            services.AddSingleton<AuthConfig.FrunchyProvider>(AuthConfig.provider "google" "Google" "google" (Some "select_account"))
            |> ignore
        | "Discord" ->
            auth.AddDiscord(
                "discord",
                fun o ->
                    o.ClientId <- provider.ClientId
                    o.ClientSecret <- provider.ClientSecret
                    o.SignInScheme <- WebAuth.ExternalScheme
                    o.CallbackPath <- PathString "/signin-discord"
                    o.Scope.Clear()
                    o.Scope.Add "identify" // only the stable id is used (FR-021)
                    withPrompt o.Events
            )
            |> ignore

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

/// The web app, composable so the integration tests can host it on a TestServer
let buildApp (args: string array) (customize: WebApplicationBuilder -> unit) : WebApplication =
    let builder = WebApplication.CreateBuilder(args)
    customize builder
    configureServices builder.Configuration builder.Services
    let app = builder.Build()
    configureApp app
    app

// Operator CLI: `dotnet run -- <command> [args]` (contracts/operator-cli.md)
let private cliCommands: Map<string, DbConfig -> string list -> Task<int>> =
    Map
        [ "user-create", Operations.CreateUser.run
          "user-regenerate-key", Operations.RegenerateKey.run
          "user-reset-binding", Operations.ResetBinding.run
          "user-rebuild", Operations.RebuildUser.run
          "user-delete", Operations.DeleteUser.run
          "user-list", Operations.ListUsers.run
          "invite-create", Operations.InviteCreate.run
          "invite-list", Operations.InviteList.run
          "invite-revoke", Operations.InviteRevoke.run ]

let private runCli (command: string) (rest: string list) : int =
    // Same configuration sources as the web app (appsettings, environment, user secrets)
    let db = dbConfigFrom (WebApplication.CreateBuilder(Array.empty).Configuration)

    task {
        do! initializeSchema db
        return! cliCommands[command] db rest
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
