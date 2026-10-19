module FrunchyBackend.Handlers.Health

open System
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access: the one query this slice runs
// ---------------------------------------------------------------------------

/// Can the database answer? One second covers opening the connection too
let private databaseAnswers (db: DbConfig) =
    task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.0)
        use connection = getConnection db
        do! connection.OpenAsync timeout.Token
        use cmd = new NpgsqlCommand("SELECT 1", connection)
        let! _ = cmd.ExecuteScalarAsync timeout.Token
        ()
    }

// ---------------------------------------------------------------------------
// Handler: feature 016. For Caravana and any orchestrator, on the copy's own address (any Host header). It
// answers from the app and its database only: never Megáfono, MyAnimeList or a sign-in provider, which being
// down must not take the site off. Plain text, never cached, no cookie, no localization.
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        task {
            let! healthy =
                task {
                    try
                        do! databaseAnswers (ctx.GetService<DbConfig>())
                        return true
                    with ex ->
                        // Npgsql's messages carry no password
                        ctx.GetLogger("Frunchy.Health").LogWarning("Health check failed: {Type}: {Message}", ex.GetType().Name, ex.Message)
                        return false
                }

            ctx.Response.Headers["Cache-Control"] <- "no-store"
            ctx.SetContentType "text/plain; charset=utf-8"

            if healthy then
                return! ctx.WriteText "ok"
            else
                ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
                return! ctx.WriteText "unavailable"
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/healthz" handler ]
