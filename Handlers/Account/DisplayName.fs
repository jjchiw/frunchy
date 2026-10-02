module FrunchyBackend.Handlers.Account.DisplayName

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private save (db: DbConfig) (userId: Guid) (displayName: string) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        use cmd = new NpgsqlCommand("UPDATE users SET display_name = @name WHERE id = @id", connection, tx)
        cmd.Parameters.AddWithValue("name", displayName) |> ignore
        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        do! SecurityEvents.record connection tx userId SecurityEventType.DisplayNameChanged None Actor.User
        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Handler (FR-010a): 1-60 characters; the username never changes
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx
            let displayName = WebAuth.formValue form "displayName"

            if displayName.Length < 1 || displayName.Length > 60 then
                return! WebAuth.redirectTo ctx "/account?msg=display-name-invalid"
            else
                do! save (ctx.GetService<DbConfig>()) userId displayName
                return! WebAuth.redirectTo ctx "/account?msg=display-name-saved"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/account/display-name" handler |> configureEndpoint _.RequireRateLimiting("account-write") ]
