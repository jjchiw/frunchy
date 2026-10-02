module FrunchyBackend.Handlers.Auth.SignUp

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

type FormValues =
    { Invite: string
      Username: string
      DisplayName: string }

let private field (labelText: string) (name: string) (value: string) (hint: string option) =
    label(class' = "form-control w-full") {
        div(class' = "label") { span(class' = "label-text") { labelText } }
        input(type' = "text", name = name, value = value, class' = "input input-bordered w-full", required = true)

        match hint with
        | Some h -> div(class' = "label") { span(class' = "label-text-alt text-gray-400") { h } }
        | None -> Fragment()
    }

let view (ctx: HttpContext) (t: Texts) (values: FormValues) (error: string option) =
    div(class' = "max-w-md mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.SignUpTitle }
        p(class' = "text-gray-400") { t.SignUpIntro }

        match error with
        | Some message -> div(class' = "alert alert-error").data("signup-error", "") { span() { message } }
        | None -> Fragment()

        form(method = "post", action = "/signup", class' = "space-y-4") {
            ctx.GetAntiforgeryInput()
            field t.InviteCodeLabel "invite" values.Invite None
            field t.UsernameLabel "username" values.Username (Some t.UsernameHint)
            field t.DisplayNameLabel "displayName" values.DisplayName None
            button(type' = "submit", class' = "btn btn-primary w-full") { t.CreateAccountButton }
        }

        a(href = "/claim", class' = "link").data("claim-link", "") { t.HaveAccountLink }
    }
    |> Layout.page ctx t t.SignUpTitle

// ---------------------------------------------------------------------------
// Data access: one transaction; the invite is consumed only if the account is created (research R4)
// ---------------------------------------------------------------------------

type private SignUpError =
    | InviteNotValid
    | UsernameNotFree
    | IdentityTaken

let private createAccount (db: DbConfig) (identity: ProviderIdentity) (inviteCode: string) (username: string) (displayName: string) =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let userId = Guid.NewGuid()

        try
            use insertUser =
                new NpgsqlCommand("INSERT INTO users (id, username, display_name) VALUES (@id, @username, @display_name)", connection, tx)

            insertUser.Parameters.AddWithValue("id", userId) |> ignore
            insertUser.Parameters.AddWithValue("username", username) |> ignore
            insertUser.Parameters.AddWithValue("display_name", displayName) |> ignore
            let! _ = insertUser.ExecuteNonQueryAsync()

            // Consume the invite only if it's still usable; rolled back with the user if anything fails
            use consume =
                new NpgsqlCommand(
                    """
                    UPDATE invites SET used_at = now(), used_by_user_id = @user_id
                    WHERE code_hash = @hash AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
                    RETURNING id
                    """,
                    connection,
                    tx
                )

            consume.Parameters.AddWithValue("user_id", userId) |> ignore
            consume.Parameters.AddWithValue("hash", InviteCode.hash inviteCode) |> ignore
            let! consumed = consume.ExecuteScalarAsync()

            if isNull consumed then
                do! tx.RollbackAsync()
                return Error InviteNotValid
            else
                use insertIdentity =
                    new NpgsqlCommand(
                        "INSERT INTO user_identities (provider, provider_user_id, user_id) VALUES (@provider, @subject, @user_id)",
                        connection,
                        tx
                    )

                insertIdentity.Parameters.AddWithValue("provider", identity.Provider) |> ignore
                insertIdentity.Parameters.AddWithValue("subject", identity.ProviderUserId) |> ignore
                insertIdentity.Parameters.AddWithValue("user_id", userId) |> ignore
                let! _ = insertIdentity.ExecuteNonQueryAsync()
                do! SecurityEvents.record connection tx userId SecurityEventType.AccountCreated (Some identity.Provider) Actor.User
                do! tx.CommitAsync()
                return Ok userId
        with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
            return Error(if ex.ConstraintName = "user_identities_pkey" then IdentityTaken else UsernameNotFree)
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private empty = { Invite = ""; Username = ""; DisplayName = "" }

/// The pending provider identity (signed in at the provider, no Frunchy account yet)
let private withPending (handler: ProviderIdentity -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        task {
            match! WebAuth.tryExternalResult ctx with
            | Some ext when ext.Mode = "signin" -> return! handler ext.Identity ctx
            | _ -> return! WebAuth.redirectTo ctx "/signin"
        }

let page: EndpointHandler =
    withPending (fun _ ctx -> ctx.WriteHtmlView(view ctx (Texts.current ()) empty None))

let submit: EndpointHandler =
    withPending (fun identity ctx ->
        task {
            let t = Texts.current ()
            let! form = WebAuth.readValidatedForm ctx

            let values =
                { Invite = WebAuth.formValue form "invite"
                  Username = WebAuth.formValue form "username"
                  DisplayName = WebAuth.formValue form "displayName" }

            let refuse (message: string) =
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                ctx.WriteHtmlView(view ctx t values (Some message))

            match Username.tryCreate values.Username with
            | None -> return! refuse t.UsernameInvalid
            | Some _ when values.DisplayName.Length < 1 || values.DisplayName.Length > 60 -> return! refuse t.DisplayNameInvalid
            | Some _ when values.Invite = "" -> return! refuse t.InviteInvalid
            | Some username ->
                match! createAccount (ctx.GetService<DbConfig>()) identity values.Invite username values.DisplayName with
                | Error InviteNotValid -> return! refuse t.InviteInvalid
                | Error UsernameNotFree -> return! refuse t.UsernameTaken
                | Error IdentityTaken -> return! refuse t.IdentityAlreadyUsed
                | Ok userId ->
                    do! WebAuth.signInUser ctx userId identity.Provider
                    do! WebAuth.clearExternal ctx
                    return! WebAuth.redirectTo ctx "/account"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/signup" page ]
      POST [ route "/signup" submit |> configureEndpoint _.RequireRateLimiting("signup") ] ]
