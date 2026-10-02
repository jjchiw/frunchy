module FrunchyBackend.Handlers.Account.Logins

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access: never remove the last identity (FR-012)
// ---------------------------------------------------------------------------

let private unlink (db: DbConfig) (userId: Guid) (provider: string) : Task<bool> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand(
                """
                DELETE FROM user_identities
                WHERE user_id = @id AND provider = @provider
                  AND (SELECT count(*) FROM user_identities WHERE user_id = @id) > 1
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        cmd.Parameters.AddWithValue("provider", provider) |> ignore
        let! removed = cmd.ExecuteNonQueryAsync()

        if removed > 0 then
            do! SecurityEvents.record connection tx userId SecurityEventType.IdentityUnlinked (Some provider) Actor.User

        do! tx.CommitAsync()
        return removed > 0
    }

// ---------------------------------------------------------------------------
// Handlers (linking completes in Handlers/Auth/Complete.fs, link mode)
// ---------------------------------------------------------------------------

let startLink (provider: string) : EndpointHandler =
    WebAuth.requireSignedIn (fun _ ctx ->
        match ctx.RequestServices.GetServices<AuthConfig.FrunchyProvider>() |> Seq.tryFind (fun p -> p.Name = provider) with
        | Some p -> WebAuth.challenge ctx p.Name p.Scheme "link" "/account" None
        | None -> WebAuth.redirectTo ctx "/account")

let removeLink (provider: string) : EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx

            match! unlink (ctx.GetService<DbConfig>()) userId provider with
            | true -> return! WebAuth.redirectTo ctx "/account?msg=unlinked"
            | false -> return! WebAuth.redirectTo ctx "/account?msg=unlink-last"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ routef "/account/link/{%s}" startLink ]
      POST [ routef "/account/unlink/{%s}" removeLink |> configureEndpoint _.RequireRateLimiting("account-write") ] ]
