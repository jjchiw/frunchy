module FrunchyBackend.Handlers.Auth.Claim

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

let view (ctx: HttpContext) (t: Texts) (refused: bool) =
    div(class' = "max-w-md mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.ClaimTitle }
        p(class' = "text-gray-400") { t.ClaimIntro }

        if refused then
            div(class' = "alert alert-error").data("claim-error", "") { span() { t.ClaimRefused } }

        form(method = "post", action = "/claim", class' = "space-y-4") {
            ctx.GetAntiforgeryInput()

            label(class' = "form-control w-full") {
                div(class' = "label") { span(class' = "label-text") { t.SyncKeyLabel } }
                input(type' = "password", name = "syncKey", class' = "input input-bordered w-full", required = true, autocomplete = "off")
            }

            button(type' = "submit", class' = "btn btn-primary w-full") { t.ClaimButton }
        }

        a(href = "/signup", class' = "link") { t.BackToSignUp }
    }
    |> Layout.page ctx t t.ClaimTitle

// ---------------------------------------------------------------------------
// Data access (research R5): only accounts with no linked identity can be claimed
// ---------------------------------------------------------------------------

type private ClaimResult =
    | Claimed of Guid
    | Refused

let private tryClaim (db: DbConfig) (identity: ProviderIdentity) (key: string) : Task<ClaimResult> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use find = new NpgsqlCommand("SELECT id FROM users WHERE sync_key_hash = @hash FOR UPDATE", connection, tx)
        find.Parameters.AddWithValue("hash", SyncKey.hash key) |> ignore
        let! found = find.ExecuteScalarAsync()

        match found with
        | :? Guid as userId ->
            use count = new NpgsqlCommand("SELECT count(*) FROM user_identities WHERE user_id = @id", connection, tx)
            count.Parameters.AddWithValue("id", userId) |> ignore
            let! linked = count.ExecuteScalarAsync()

            if unbox<int64> linked > 0L then
                // Someone holds this account's key: warn the owner (shown in their security events)
                do! SecurityEvents.record connection tx userId SecurityEventType.ClaimRefused (Some identity.Provider) Actor.User
                do! tx.CommitAsync()
                return Refused
            else
                try
                    use link =
                        new NpgsqlCommand(
                            "INSERT INTO user_identities (provider, provider_user_id, user_id) VALUES (@provider, @subject, @id)",
                            connection,
                            tx
                        )

                    link.Parameters.AddWithValue("provider", identity.Provider) |> ignore
                    link.Parameters.AddWithValue("subject", identity.ProviderUserId) |> ignore
                    link.Parameters.AddWithValue("id", userId) |> ignore
                    let! _ = link.ExecuteNonQueryAsync()
                    do! SecurityEvents.record connection tx userId SecurityEventType.Claimed (Some identity.Provider) Actor.User
                    do! tx.CommitAsync()
                    return Claimed userId
                with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
                    return Refused
        | _ -> return Refused
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private withPending (handler: ProviderIdentity -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        task {
            match! WebAuth.tryExternalResult ctx with
            | Some ext when ext.Mode = "signin" -> return! handler ext.Identity ctx
            | _ -> return! WebAuth.redirectTo ctx "/signin"
        }

let page: EndpointHandler =
    withPending (fun _ ctx -> ctx.WriteHtmlView(view ctx (Texts.current ()) false))

let submit: EndpointHandler =
    withPending (fun identity ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx

            match! tryClaim (ctx.GetService<DbConfig>()) identity (WebAuth.formValue form "syncKey") with
            | Claimed userId ->
                do! WebAuth.signInUser ctx userId identity.Provider
                do! WebAuth.clearExternal ctx
                return! WebAuth.redirectTo ctx "/account"
            | Refused ->
                // Same answer for unknown keys and already-linked accounts (no enumeration)
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                return! ctx.WriteHtmlView(view ctx (Texts.current ()) true)
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/claim" page ]
      POST [ route "/claim" submit |> configureEndpoint _.RequireRateLimiting("claim") ] ]
