module FrunchyBackend.Handlers.Api.WhoAmI

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract (specs/001-crunchyroll-history-import/contracts/import-api.md)
// ---------------------------------------------------------------------------

type WhoAmIResponse =
    { username: string
      displayName: string
      dashboardUrl: string
      lastSyncAt: Nullable<DateTime>
      historyOwnerBound: bool }

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// Most recent accepted submission
let private lastSync (db: DbConfig) (userId: Guid) : Task<Nullable<DateTime>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT max(received_at) FROM cr_imports WHERE user_id = @id", connection)
        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! value = cmd.ExecuteScalarAsync()

        return
            match value with
            | :? DateTime as d -> Nullable d
            | _ -> Nullable()
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    Auth.withUser (fun (user: User) ctx ->
        task {
            let! last = lastSync (ctx.GetService<DbConfig>()) user.Id
            let request = ctx.Request

            return!
                ctx.WriteJson
                    { username = user.Username
                      displayName = user.DisplayName
                      dashboardUrl = $"{request.Scheme}://{request.Host}/u/{user.Username}"
                      lastSyncAt = last
                      historyOwnerBound = user.CrAccountId.IsSome }
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/api/me" handler ]
