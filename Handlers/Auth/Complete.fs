module FrunchyBackend.Handlers.Auth.Complete

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private tryLinkedUser (db: DbConfig) (identity: ProviderIdentity) : Task<Guid option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "SELECT user_id FROM user_identities WHERE provider = @provider AND provider_user_id = @subject",
                connection
            )

        cmd.Parameters.AddWithValue("provider", identity.Provider) |> ignore
        cmd.Parameters.AddWithValue("subject", identity.ProviderUserId) |> ignore
        let! id = cmd.ExecuteScalarAsync()
        return match id with :? Guid as g -> Some g | _ -> None
    }

/// last_used_at + a signed_in security event, together
let private recordSignIn (db: DbConfig) (userId: Guid) (identity: ProviderIdentity) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use touch =
            new NpgsqlCommand(
                "UPDATE user_identities SET last_used_at = now() WHERE provider = @provider AND provider_user_id = @subject",
                connection,
                tx
            )

        touch.Parameters.AddWithValue("provider", identity.Provider) |> ignore
        touch.Parameters.AddWithValue("subject", identity.ProviderUserId) |> ignore
        let! _ = touch.ExecuteNonQueryAsync()
        do! SecurityEvents.record connection tx userId SecurityEventType.SignedIn (Some identity.Provider) Actor.User
        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Handler: back from the provider (research R3)
// ---------------------------------------------------------------------------

let private signInMode (ctx: HttpContext) (db: DbConfig) (ext: WebAuth.ExternalResult) : Task =
    task {
        match! tryLinkedUser db ext.Identity with
        | Some userId ->
            do! recordSignIn db userId ext.Identity
            do! WebAuth.signInUser ctx userId ext.Identity.Provider
            do! WebAuth.clearExternal ctx
            ctx.Response.Redirect(WebAuth.localReturnUrl (ext.ReturnUrl |> Option.defaultValue "") "/account")
        | None ->
            // keep the external cookie: /signup (or /claim) completes it
            ctx.Response.Redirect "/signup"
    }

let handler: EndpointHandler =
    fun ctx ->
        task {
            let db = ctx.GetService<DbConfig>()

            match! WebAuth.tryExternalResult ctx with
            | None -> ctx.Response.Redirect "/signin?error=failed"
            | Some ext -> do! signInMode ctx db ext
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/signin/complete" handler |> configureEndpoint _.RequireRateLimiting("signin") ]
