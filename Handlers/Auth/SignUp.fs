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
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

type FormValues =
    { Invite: string
      Username: string
      DisplayName: string }

/// The field a refusal is about (None: the page-level IdentityAlreadyUsed), so its input is marked invalid
let private refusedField (t: Texts) (error: string option) =
    match error with
    | Some e when e = t.InviteInvalid -> Some "invite"
    | Some e when e = t.UsernameInvalid || e = t.UsernameTaken -> Some "username"
    | Some e when e = t.DisplayNameInvalid -> Some "displayName"
    | _ -> None

/// One of the three fields: required; the refused one is marked invalid and described by the error alert
let private formField (refused: string option) (name: string) (describedBy: string list) (extra: input -> unit) =
    let invalid = refused = Some name

    let setup (i: input) =
        i.attr("required", "").attr("spellcheck", "false") |> ignore

        if invalid then
            i.attr("aria-invalid", "true") |> ignore

        extra i

    fieldWith "text" (describedBy @ (if invalid then [ "signup-error" ] else [])) setup

/// Username field with a live preview of the public address under it (Alpine; the server renders the same text)
let private usernameField (ctx: HttpContext) (t: Texts) (value: string) (refused: string option) =
    let prefix = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/"
    let current = value.Trim().ToLowerInvariant()

    let extra (i: input) =
        i.attr("autocomplete", "off").attr("autocapitalize", "none").attr("maxlength", "30").attr("x-on:input", "username = $el.value")
        |> ignore

    div()
        .attr("x-data", "{ username: '' }")
        .attr("x-init", "username = $el.querySelector('#username').value") {
        formField refused "username" [ "username-preview"; "username-hint" ] extra t.UsernameLabel "username" value None None true

        p(id = "username-preview", class' = "mt-1.5 min-w-0 break-all font-mono text-body-md text-primary") {
            span(class' = "text-on-surface-variant") { prefix }

            span()
                .attr("x-text", "username.trim().toLowerCase() || $el.dataset.placeholder")
                .data("placeholder", t.UsernamePreviewPlaceholder) {
                if current = "" then t.UsernamePreviewPlaceholder else current
            }
        }

        p(id = "username-hint", class' = "fr-hint") { t.UsernameHint }
    }

let view (ctx: HttpContext) (t: Texts) (mode: SignUpMode.SignUpMode) (values: FormValues) (error: string option) =
    let refused = refusedField t error

    Fragment() {
        pageHeading t.SignUpTitle (Some(if mode = SignUpMode.Open then t.SignUpIntroOpen else t.SignUpIntro))

        match error with
        | Some message -> (alert Failure message).attr("id", "signup-error").data("signup-error", "") :> HtmlElement
        | None -> Fragment()

        form(method = "post", action = "/signup", class' = "space-y-5") {
            ctx.GetAntiforgeryInput()

            // Open mode (feature 020): no invite field, nothing about a code
            match mode with
            | SignUpMode.Invite ->
                formField refused "invite" [] (fun i -> i.attr("autocomplete", "off").attr("autocapitalize", "none") |> ignore) t.InviteCodeLabel "invite" values.Invite None None true
                :> HtmlElement
            | SignUpMode.Open -> Fragment()

            usernameField ctx t values.Username refused

            formField refused "displayName" [] (fun i -> i.attr("autocomplete", "nickname").attr("maxlength", "60") |> ignore) t.DisplayNameLabel "displayName" values.DisplayName (Some t.DisplayNameHint) None false
            button(type' = "submit", class' = btn Primary + " w-full") { t.CreateAccountButton }
        }

        div(class' = "border-t border-outline-variant/40 pt-5 text-center") {
            a(href = "/claim", class' = "fr-link text-body-md").data("claim-link", "") { t.HaveAccountLink }
        }
    }
    |> centredCard
    |> Layout.page ctx t t.SignUpTitle

// ---------------------------------------------------------------------------
// Data access: one transaction; in invite mode the invite is consumed only if the account is created (research R4),
// in open mode (feature 020) the same transaction runs without the invite step
// ---------------------------------------------------------------------------

type private SignUpError =
    | InviteNotValid
    | UsernameNotFree
    | IdentityTaken

/// `inviteCode` is Some in invite mode (the code is consumed in this transaction) and None in open mode (the
/// `invites` table is not touched)
let private createAccount (db: DbConfig) (identity: ProviderIdentity) (inviteCode: string option) (username: string) (displayName: string) =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        let userId = Guid.NewGuid()

        try
            // Feature 015: a username equal to a series' fediverse handle is taken. The lock is the one handle
            // creation takes, so a handle can't be created for it between this check and the insert.
            use lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, tx)
            lock.Parameters.AddWithValue("key", Fediverse.HandleLockKey) |> ignore
            let! _ = lock.ExecuteNonQueryAsync()

            use handleTaken = new NpgsqlCommand("SELECT 1 FROM series_handles WHERE handle = @username", connection, tx)
            handleTaken.Parameters.AddWithValue("username", username) |> ignore
            let! seriesHandle = handleTaken.ExecuteScalarAsync()

            if not (isNull seriesHandle) then
                do! tx.RollbackAsync()
                return Error UsernameNotFree
            else

            use insertUser =
                new NpgsqlCommand("INSERT INTO users (id, username, display_name) VALUES (@id, @username, @display_name)", connection, tx)

            insertUser.Parameters.AddWithValue("id", userId) |> ignore
            insertUser.Parameters.AddWithValue("username", username) |> ignore
            insertUser.Parameters.AddWithValue("display_name", displayName) |> ignore
            let! _ = insertUser.ExecuteNonQueryAsync()

            // Invite mode: consume the invite only if it's still usable; rolled back with the user if anything fails.
            // Open mode: there is no invite step.
            let! (inviteUsable, isSetupInvite) =
                task {
                    match inviteCode with
                    | None -> return (true, false)
                    | Some code ->
                        use consume =
                            new NpgsqlCommand(
                                """
                                UPDATE invites SET used_at = now(), used_by_user_id = @user_id
                                WHERE code_hash = @hash AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
                                RETURNING is_setup
                                """,
                                connection,
                                tx
                            )

                        consume.Parameters.AddWithValue("user_id", userId) |> ignore
                        consume.Parameters.AddWithValue("hash", InviteCode.hash code) |> ignore
                        let! consumed = consume.ExecuteScalarAsync()
                        // RETURNING is_setup: null when the invite was not usable, else true for the setup invite (feature 023)
                        let isSetup =
                            match consumed with
                            | :? bool as setup -> setup
                            | _ -> false

                        return (not (isNull consumed), isSetup)
                }

            if not inviteUsable then
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

                // Feature 023: the setup invite makes its account an operator, in this same transaction (what
                // `operator-add` writes: the row with no granting root, and the event as the operator's doing)
                if isSetupInvite then
                    use operatorRow = new NpgsqlCommand("INSERT INTO operators (user_id) VALUES (@user_id)", connection, tx)
                    operatorRow.Parameters.AddWithValue("user_id", userId) |> ignore
                    let! _ = operatorRow.ExecuteNonQueryAsync()
                    do! SecurityEvents.record connection tx userId SecurityEventType.OperatorAdded None Actor.Operator

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
    withPending (fun _ ctx ->
        task {
            let! mode = SignUpMode.current (ctx.GetService<DbConfig>())
            return! ctx.WriteHtmlView(view ctx (Texts.current ()) mode empty None)
        })

let submit: EndpointHandler =
    withPending (fun identity ctx ->
        task {
            let t = Texts.current ()
            let! form = WebAuth.readValidatedForm ctx
            // The mode at submit time decides, not the one the page was rendered with
            let! mode = SignUpMode.current (ctx.GetService<DbConfig>())

            let values =
                { Invite = WebAuth.formValue form "invite"
                  Username = WebAuth.formValue form "username"
                  DisplayName = WebAuth.formValue form "displayName" }

            let refuse (message: string) =
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                ctx.WriteHtmlView(view ctx t mode values (Some message))

            match Username.tryCreateNew values.Username with
            | None -> return! refuse t.UsernameInvalid
            | Some _ when values.DisplayName.Length < 1 || values.DisplayName.Length > 60 -> return! refuse t.DisplayNameInvalid
            | Some _ when mode = SignUpMode.Invite && values.Invite = "" -> return! refuse t.InviteInvalid
            | Some username ->
                let invite = if mode = SignUpMode.Open then None else Some values.Invite

                match! createAccount (ctx.GetService<DbConfig>()) identity invite username values.DisplayName with
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
