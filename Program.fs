module FrunchyBackend.Program

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Localization
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
      Handlers.Api.WhoAmI.endpoint ]

let private isApi (ctx: HttpContext) =
    ctx.Request.Path.StartsWithSegments(PathString "/api")

let notFoundHandler (ctx: HttpContext) =
    ctx.GetLogger().LogInformation("404 {Path}", ctx.Request.Path)

    if isApi ctx then
        ApiErrors.notFound ctx
    else
        let t = Texts.current ()
        ctx.SetStatusCode StatusCodes.Status404NotFound
        ctx.WriteHtmlView(Layout.messagePage t t.NotFoundTitle t.NotFoundBody)

let errorHandler (ctx: HttpContext) (next: RequestDelegate) =
    task {
        try
            return! next.Invoke(ctx)
        with
        | :? BadHttpRequestException as ex when ex.StatusCode = StatusCodes.Status413PayloadTooLarge ->
            return!
                ApiErrors.writeError ctx StatusCodes.Status413PayloadTooLarge ApiErrors.PayloadTooLarge "Request body is too large."
        | ex ->
            ctx.GetLogger().LogError(ex, "Unhandled error on {Path}", ctx.Request.Path)
            ctx.SetStatusCode StatusCodes.Status500InternalServerError

            if isApi ctx then
                return! ApiErrors.writeError ctx StatusCodes.Status500InternalServerError "server_error" "Unexpected server error."
            else
                let t = Texts.current ()
                return! ctx.WriteHtmlView(Layout.messagePage t t.ErrorTitle t.ErrorBody)
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

let configureServices (config: IConfiguration) (services: IServiceCollection) =
    services.AddSingleton<DbConfig>(dbConfigFrom config) |> ignore

    if config.GetValue<bool>(WorkerEnabledKey, true) then
        services.AddHostedService<ImportProcessorService>() |> ignore

    services.AddRouting().AddOxpecker() |> ignore

let configureApp (app: WebApplication) =
    app
        .UseRequestLocalization(localizationOptions ())
        .UseRouting()
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
    Map [ "user-create", Operations.CreateUser.run ]

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
