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
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (design/frunchy_attach_existing_account_claim, per design/REVIEW.md)
// ---------------------------------------------------------------------------

/// The sync key, as a password so it isn't left readable on screen; mono, since keys are data
let private syncKeyField (t: Texts) (refused: bool) =
    let setup (i: input) =
        i.attr("required", "").attr("autocomplete", "off").attr("spellcheck", "false").attr("autocapitalize", "none") |> ignore

        if refused then
            i.attr("aria-invalid", "true") |> ignore

    fieldWith "password" (if refused then [ "claim-error" ] else []) setup t.SyncKeyLabel "syncKey" "" None None true

let view (ctx: HttpContext) (t: Texts) (refused: bool) =
    Fragment() {
        pageHeading t.ClaimTitle (Some t.ClaimIntro)

        if refused then
            (alert Failure t.ClaimRefused).attr("id", "claim-error").data("claim-error", "")

        form(method = "post", action = "/claim", class' = "space-y-5") {
            ctx.GetAntiforgeryInput()
            syncKeyField t refused
            button(type' = "submit", class' = btn Primary + " w-full") { t.ClaimButton }
        }

        div(class' = "border-t border-outline-variant/40 pt-5 text-center") {
            a(href = "/signup", class' = "fr-link text-body-md") { t.BackToSignUp }
        }
    }
    |> centredCard
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
