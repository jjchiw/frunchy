/// Browser sign-in: session and external cookies, the signed-in user, the 10-minute "fresh" rule,
/// and local-only return URLs (feature 002, research R2, R3, R6)
module FrunchyBackend.Infrastructure.WebAuth

open System
open System.Security.Claims
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// The signed-in browser (30 days, sliding)
[<Literal>]
let SessionScheme = "frunchy.session"

/// Short-lived cookie carrying the provider result to /signin/complete (10 minutes)
[<Literal>]
let ExternalScheme = "frunchy.external"

/// AuthenticationProperties item: what the provider round trip is for (signin | link | reconfirm)
[<Literal>]
let ModeKey = "frunchy.mode"

/// AuthenticationProperties item: prompt= to add to the provider's authorization URL
[<Literal>]
let PromptKey = "frunchy.prompt"

/// AuthenticationProperties item: which Frunchy provider the round trip went to
[<Literal>]
let ProviderKey = "frunchy.provider"

[<Literal>]
let ReturnUrlKey = "frunchy.returnUrl"

[<Literal>]
let private UserIdClaim = "frunchy:uid"

[<Literal>]
let private AuthTimeClaim = "frunchy:auth_time"

[<Literal>]
let private ProviderClaim = "frunchy:provider"

[<Literal>]
let private DisplayNameItem = "frunchy.displayName"

let freshWindow = TimeSpan.FromMinutes 10.0

let utcNow (ctx: HttpContext) =
    match ctx.RequestServices.GetService<TimeProvider>() with
    | null -> DateTimeOffset.UtcNow
    | clock -> clock.GetUtcNow()

/// Only local paths ("/..."), never "//host" or "/\host" (no open redirects)
let localReturnUrl (raw: string) (fallback: string) =
    if String.IsNullOrEmpty raw then fallback
    elif raw.StartsWith "/" && not (raw.StartsWith "//") && not (raw.StartsWith "/\\") then raw
    else fallback

/// Issue the session cookie for a user, recording when they signed in
let signInUser (ctx: HttpContext) (userId: Guid) (provider: string) : Task<unit> =
    task {
        let claims =
            [ Claim(UserIdClaim, string userId)
              Claim(AuthTimeClaim, (utcNow ctx).ToString("O"))
              Claim(ProviderClaim, provider) ]

        let principal = ClaimsPrincipal(ClaimsIdentity(claims, SessionScheme))
        do! ctx.SignInAsync(SessionScheme, principal, AuthenticationProperties(IsPersistent = true))
    }

let signOut (ctx: HttpContext) : Task<unit> =
    task { do! ctx.SignOutAsync SessionScheme }

let private claimValue (ctx: HttpContext) (claimType: string) =
    match ctx.User with
    | null -> None
    | user ->
        match user.FindFirst claimType with
        | null -> None
        | claim -> Some claim.Value

let currentUserId (ctx: HttpContext) : Guid option =
    claimValue ctx UserIdClaim
    |> Option.bind (fun v ->
        match Guid.TryParse v with
        | true, id -> Some id
        | _ -> None)

/// The provider the current session was signed in (or last re-confirmed) with
let currentProvider (ctx: HttpContext) = claimValue ctx ProviderClaim

let authTime (ctx: HttpContext) : DateTimeOffset option =
    claimValue ctx AuthTimeClaim
    |> Option.bind (fun v ->
        match DateTimeOffset.TryParse v with
        | true, t -> Some t
        | _ -> None)

let isFresh (ctx: HttpContext) =
    match authTime ctx with
    | Some t -> utcNow ctx - t <= freshWindow
    | None -> false

let private redirect (ctx: HttpContext) (location: string) =
    ctx.Response.Redirect location
    Task.CompletedTask

let private currentPathAndQuery (ctx: HttpContext) =
    ctx.Request.Path.Value + ctx.Request.QueryString.Value

/// Run the handler for the signed-in user, or send the browser to /signin and back afterwards
let requireSignedIn (handler: Guid -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        match currentUserId ctx with
        | Some userId -> handler userId ctx
        | None -> redirect ctx $"/signin?returnUrl={Uri.EscapeDataString(currentPathAndQuery ctx)}"

/// Signed in AND signed in (or re-confirmed) within the last 10 minutes; otherwise re-confirm first
let requireFresh (returnUrl: string) (handler: Guid -> EndpointHandler) : EndpointHandler =
    requireSignedIn (fun userId ctx ->
        if isFresh ctx then
            handler userId ctx
        else
            redirect ctx $"/signin/reconfirm?returnUrl={Uri.EscapeDataString returnUrl}")

/// What the navbar shows
type Nav =
    | SignedOut
    | SignedIn of displayName: string

let currentNav (ctx: HttpContext) =
    match currentUserId ctx, ctx.Items.TryGetValue DisplayNameItem with
    | Some _, (true, (:? string as name)) -> SignedIn name
    | _ -> SignedOut

/// Cookie validation on every request: the user must still exist (deleted elsewhere → signed out).
/// Also stashes the current display name for the navbar.
let validatePrincipal (context: CookieValidatePrincipalContext) : Task =
    task {
        let userId =
            match context.Principal with
            | null -> None
            | p ->
                match p.FindFirst UserIdClaim with
                | null -> None
                | c ->
                    match Guid.TryParse c.Value with
                    | true, id -> Some id
                    | _ -> None

        match userId with
        | None -> context.RejectPrincipal()
        | Some id ->
            let db = context.HttpContext.RequestServices.GetRequiredService<DbConfig>()
            use connection = getConnection db
            do! connection.OpenAsync()
            use cmd = new NpgsqlCommand("SELECT display_name FROM users WHERE id = @id", connection)
            cmd.Parameters.AddWithValue("id", id) |> ignore
            let! name = cmd.ExecuteScalarAsync()

            match name with
            | :? string as displayName -> context.HttpContext.Items[DisplayNameItem] <- displayName
            | _ ->
                context.RejectPrincipal()
                do! context.HttpContext.SignOutAsync SessionScheme
    }
    :> Task

/// The provider result waiting in the external cookie, with the round trip's mode and return URL
type ExternalResult =
    { Identity: ProviderIdentity
      Mode: string
      ReturnUrl: string option }

let tryExternalResult (ctx: HttpContext) : Task<ExternalResult option> =
    task {
        let! result = ctx.AuthenticateAsync ExternalScheme

        if not result.Succeeded || isNull result.Principal then
            return None
        else
            let items = if isNull result.Properties then dict [] else result.Properties.Items

            let item key =
                match items.TryGetValue key with
                | true, v when not (String.IsNullOrEmpty v) -> Some v
                | _ -> None

            match result.Principal.FindFirst ClaimTypes.NameIdentifier, item ProviderKey with
            | null, _
            | _, None -> return None
            | subject, Some provider ->
                return
                    Some
                        { Identity = { Provider = provider; ProviderUserId = subject.Value }
                          Mode = item ModeKey |> Option.defaultValue "signin"
                          ReturnUrl = item ReturnUrlKey }
    }

let clearExternal (ctx: HttpContext) : Task<unit> =
    task { do! ctx.SignOutAsync ExternalScheme }

/// Challenge a provider for a round trip of the given mode (signin | link | reconfirm)
let challenge (ctx: HttpContext) (provider: string) (scheme: string) (mode: string) (returnUrl: string) (prompt: string option) : Task =
    let props = AuthenticationProperties(RedirectUri = "/signin/complete")
    props.Items[ProviderKey] <- provider
    props.Items[ModeKey] <- mode
    props.Items[ReturnUrlKey] <- returnUrl
    prompt |> Option.iter (fun p -> props.Items[PromptKey] <- p)
    ctx.ChallengeAsync(scheme, props)

/// Read a posted form after validating its antiforgery token (throws AntiforgeryValidationException → 400)
let readValidatedForm (ctx: HttpContext) : Task<IFormCollection> =
    task {
        do! ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync ctx
        return! ctx.Request.ReadFormAsync ctx.RequestAborted
    }

let formValue (form: IFormCollection) (name: string) =
    match form.TryGetValue name with
    | true, v -> (string v).Trim()
    | _ -> ""

let redirectTo (ctx: HttpContext) (location: string) : Task =
    ctx.Response.Redirect location
    Task.CompletedTask
