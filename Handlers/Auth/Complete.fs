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

/// Account page "Link Google/Discord": attach the identity to the signed-in user (FR-012)
let private linkMode (ctx: HttpContext) (db: DbConfig) (userId: Guid) (ext: WebAuth.ExternalResult) : Task =
    task {
        let! owner = tryLinkedUser db ext.Identity

        let! outcome =
            task {
                match owner with
                | Some o when o = userId -> return "linked"
                | Some _ -> return "link-taken"
                | None ->
                    use connection = getConnection db
                    do! connection.OpenAsync()
                    use tx = connection.BeginTransaction()

                    use sameProvider =
                        new NpgsqlCommand(
                            "SELECT EXISTS (SELECT 1 FROM user_identities WHERE user_id = @id AND provider = @provider)",
                            connection,
                            tx
                        )

                    sameProvider.Parameters.AddWithValue("id", userId) |> ignore
                    sameProvider.Parameters.AddWithValue("provider", ext.Identity.Provider) |> ignore
                    let! duplicate = sameProvider.ExecuteScalarAsync()

                    if unbox<bool> duplicate then
                        return "link-duplicate"
                    else
                        try
                            use link =
                                new NpgsqlCommand(
                                    "INSERT INTO user_identities (provider, provider_user_id, user_id) VALUES (@provider, @subject, @id)",
                                    connection,
                                    tx
                                )

                            link.Parameters.AddWithValue("provider", ext.Identity.Provider) |> ignore
                            link.Parameters.AddWithValue("subject", ext.Identity.ProviderUserId) |> ignore
                            link.Parameters.AddWithValue("id", userId) |> ignore
                            let! _ = link.ExecuteNonQueryAsync()
                            do! SecurityEvents.record connection tx userId SecurityEventType.IdentityLinked (Some ext.Identity.Provider) Actor.User
                            do! tx.CommitAsync()
                            return "linked"
                        with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
                            return "link-taken"
            }

        do! WebAuth.clearExternal ctx
        ctx.Response.Redirect $"/account?msg={outcome}"
    }

/// Re-confirmation for sensitive actions: only the current user's own identity refreshes auth_time (research R6)
let private reconfirmMode (ctx: HttpContext) (db: DbConfig) (userId: Guid) (ext: WebAuth.ExternalResult) : Task =
    task {
        let! owner = tryLinkedUser db ext.Identity
        do! WebAuth.clearExternal ctx

        if owner = Some userId then
            use connection = getConnection db
            do! connection.OpenAsync()
            use tx = connection.BeginTransaction()
            do! SecurityEvents.record connection tx userId SecurityEventType.Reconfirmed (Some ext.Identity.Provider) Actor.User
            do! tx.CommitAsync()
            do! WebAuth.signInUser ctx userId ext.Identity.Provider
            ctx.Response.Redirect(WebAuth.localReturnUrl (ext.ReturnUrl |> Option.defaultValue "") "/account")
        else
            ctx.Response.Redirect "/account?msg=reconfirm-failed"
    }

let handler: EndpointHandler =
    fun ctx ->
        task {
            let db = ctx.GetService<DbConfig>()

            match! WebAuth.tryExternalResult ctx with
            | None -> ctx.Response.Redirect "/signin?error=failed"
            | Some ext ->
                match ext.Mode, WebAuth.currentUserId ctx with
                | "link", Some userId -> do! linkMode ctx db userId ext
                | "reconfirm", Some userId -> do! reconfirmMode ctx db userId ext
                | ("link" | "reconfirm"), None ->
                    do! WebAuth.clearExternal ctx
                    ctx.Response.Redirect "/signin"
                | _ -> do! signInMode ctx db ext
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/signin/complete" handler |> configureEndpoint _.RequireRateLimiting("signin") ]
