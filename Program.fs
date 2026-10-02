module FrunchyBackend.Program

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging   
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Workers.ImportProcessor
open System.Threading.Tasks

let errorView errorCode (errorText: string) =
    html() {
        body(style = "width: 800px; margin: 0 auto") {
            h1(style = "text-align: center; color: red") { raw $"Error <i>%d{errorCode}</i>" }
            p() { errorText }
        }
    }
    
let notFoundHandler (ctx: HttpContext) =
    let logger = ctx.GetLogger()
    logger.LogWarning("Unhandled 404 error")
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(errorView 404 "Page not found!")

let errorHandler (ctx: HttpContext) (next: RequestDelegate) =
    task {
        try
            return! next.Invoke(ctx)
        with
        | :? ModelBindException
        | :? RouteParseException as ex ->
            let logger = ctx.GetLogger()
            logger.LogWarning(ex, "Unhandled 400 error")
            ctx.SetStatusCode StatusCodes.Status400BadRequest
            return! ctx.WriteHtmlView(errorView 400 (string ex))
        | ex ->
            let logger = ctx.GetLogger()
            logger.LogError(ex, "Unhandled 500 error")
            ctx.SetStatusCode StatusCodes.Status500InternalServerError
            return! ctx.WriteHtmlView(errorView 500 (string ex))
    }
    :> Task

[<EntryPoint>]
let main args =
    task {
        let builder = WebApplication.CreateBuilder(args)
        
        let config = builder.Configuration
        let dbConnectionString = config.GetConnectionString("Database")
        let dbConfig: DbConfig = { connectionString = dbConnectionString }
        
        builder.Services.AddSingleton<DbConfig>(dbConfig) |> ignore
        builder.Services.AddHostedService<ImportProcessorService>(fun sp ->
            new ImportProcessorService(sp.GetRequiredService<DbConfig>())
        ) |> ignore
        
        builder.Services
            .AddRouting()
            .AddOxpecker()
        |> ignore
        
        let app = builder.Build()
        
        let! _ = initializeSchema dbConfig
        printfn "✓ Database initialized"
        
        // One vertical slice per endpoint, see Handlers/
        let endpoints = [
            Handlers.Home.endpoint
            Handlers.Dashboard.endpoint
            Handlers.Profile.endpoint
            Handlers.Imports.CreateImport.endpoint
            Handlers.Imports.GetImportStatus.endpoint
        ]

        app
            .UseStaticFiles()
            .UseRouting()
            .Use(errorHandler)
            .UseOxpecker(endpoints)
            .Run(notFoundHandler)
        
        do! app.RunAsync()
        return 0
    } |> Async.AwaitTask |> Async.RunSynchronously
