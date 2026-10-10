module FrunchyBackend.Handlers.Account.AccountPage

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View (contracts/web-account.md)
// ---------------------------------------------------------------------------

type Identity =
    { Provider: string
      LinkedAt: DateTime }

type Sync =
    { ReceivedAt: DateTime
      Status: string
      Processed: int
      Unresolved: int
      Unsupported: int
      Failed: int }

type AccountModel =
    { Username: string
      DisplayName: string
      KeyActive: bool
      Bound: bool
      Identities: Identity list
      Syncs: Sync list
      Events: SecurityEvents.SecurityEvent list }

/// ?msg= notices after an action (error = true for refusals)
let private notice (t: Texts) (msg: string) =
    match msg with
    | "display-name-saved" -> Some(false, t.DisplayNameSaved)
    | "display-name-invalid" -> Some(true, t.DisplayNameInvalid)
    | "binding-reset" -> Some(false, t.BindingResetDone)
    | "linked" -> Some(false, t.IdentityLinkedNotice)
    | "unlinked" -> Some(false, t.IdentityUnlinkedNotice)
    | "link-taken" -> Some(true, t.LinkTaken)
    | "link-duplicate" -> Some(true, t.LinkDuplicate)
    | "unlink-last" -> Some(true, t.UnlinkLast)
    | "reconfirm-failed" -> Some(true, t.ReconfirmFailed)
    | "link-failed" -> Some(true, t.SignInFailed)
    | _ -> None

/// One titled section of the stack; `hook` is its data-account value
let private accountSection (hook: string) (i: Theme.Icon) (title: string) (content: HtmlElement) =
    section(class' = "fr-card space-y-5").data("account", hook) {
        h2(class' = "flex items-center gap-2.5 text-headline-md text-on-surface") {
            span(class' = "text-primary") { icon i }
            title
        }

        content
    }

/// A labelled line of the profile section: label beside the value from md up, above it below
let private profileRow (hook: string) (labelFor: string option) (label': string) (content: HtmlElement) =
    div(class' = "grid gap-2 py-4 first:pt-0 last:pb-0 md:grid-cols-[11rem_1fr] md:items-center md:gap-6").data("account", hook) {
        match labelFor with
        | Some id -> label(for' = id, class' = "text-label-md text-on-surface-variant") { label' } :> HtmlElement
        | None -> span(class' = "text-label-md text-on-surface-variant") { label' }

        content
    }

/// A line of state (a check when it's in place) next to the action that changes it
let private stateLine (ok: bool) (text: HtmlElement) (action: HtmlElement) =
    div(class' = "flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between") {
        p(class' = "flex items-start gap-2.5 text-body-md text-on-surface") {
            span(class' = (if ok then "text-primary" else "text-on-surface-variant")) { icon (if ok then Theme.CheckCircle else Theme.Info) }
            text
        }

        action
    }

let private postButton (ctx: HttpContext) (action: string) (cssClass: string) (i: Theme.Icon) (text: string) =
    form(method = "post", action = action, class' = "shrink-0") {
        ctx.GetAntiforgeryInput()

        button(type' = "submit", class' = cssClass) {
            icon i
            text
        }
    }

let private syncTone (status: string) =
    match status with
    | "completed" -> PrimaryTone
    | "failed" -> TertiaryTone
    | "pending"
    | "processing" -> SecondaryTone
    | _ -> Muted

let private profile (ctx: HttpContext) (t: Texts) (msg: string) (m: AccountModel) =
    let address = $"{ctx.Request.Host.Value}/u/{m.Username}"

    let displayNameInput =
        input(type' = "text", id = "displayName", name = "displayName", value = m.DisplayName, class' = "fr-input sm:flex-1", required = true)

    if msg = "display-name-invalid" then
        displayNameInput.attr("aria-invalid", "true") |> ignore

    div(class' = "divide-y divide-outline-variant/40") {
        profileRow "username" None t.AccountUsername (
            div(class' = "flex flex-wrap items-center gap-x-4 gap-y-1") {
                span(class' = "font-mono text-body-lg text-on-surface") { m.Username }

                span(class' = "inline-flex items-center gap-1.5 text-label-md font-normal text-on-surface-variant") {
                    icon Theme.Lock
                    t.AccountUsernameLocked
                }
            }
        )

        profileRow "dashboard" None t.AccountDashboard (
            a(href = $"/u/{m.Username}", class' = "fr-link inline-flex w-fit items-center gap-1.5 font-mono text-body-md break-all") {
                address
                icon Theme.OpenInNew
            }
        )

        profileRow "display-name" (Some "displayName") t.AccountDisplayName (
            form(method = "post", action = "/account/display-name", class' = "flex flex-col gap-2 sm:flex-row") {
                ctx.GetAntiforgeryInput()
                displayNameInput
                button(type' = "submit", class' = btn Secondary) { t.SaveButton }
            }
        )
    }
    |> accountSection "profile" Theme.Fingerprint t.AccountProfileTitle

let private extension (t: Texts) (m: AccountModel) =
    stateLine
        m.KeyActive
        (span().data("key-active", (if m.KeyActive then "true" else "false")) {
            if m.KeyActive then t.AccountKeyActive else t.AccountKeyMissing
        })
        (a(href = "/account/key", class' = btn (if m.KeyActive then Secondary else Primary) + " shrink-0") {
            t.ConnectExtension
            icon Theme.ArrowForward
        })
    |> accountSection "key-active" Theme.Extension t.AccountSyncKey

let private binding (ctx: HttpContext) (t: Texts) (m: AccountModel) =
    stateLine
        m.Bound
        (span() { if m.Bound then t.AccountBound else t.AccountUnbound })
        (if m.Bound then
             postButton ctx "/account/binding/reset" (btnSm Secondary) Theme.LinkOff t.BindingResetButton :> HtmlElement
         else
             Fragment())
    |> accountSection "binding" Theme.Link t.AccountBinding

let private identities
    (ctx: HttpContext)
    (t: Texts)
    (providerName: string -> string)
    (linkable: AuthConfig.FrunchyProvider list)
    (m: AccountModel)
    =
    // The last sign-in method can't go: its Remove stays visible but disabled, with the reason
    let last = m.Identities.Length <= 1

    div(class' = "space-y-4") {
        ul(class' = "divide-y divide-outline-variant/40") {
            for identity in m.Identities do
                li(class' = "flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0").data("identity", identity.Provider) {
                    span(class' = "text-body-lg text-on-surface") { providerName identity.Provider }

                    if last then
                        button(type' = "button", class' = btnSm Ghost, disabled = true).attr("aria-describedby", "unlink-last") {
                            icon Theme.LinkOff
                            t.UnlinkButton
                        }
                        :> HtmlElement
                    else
                        postButton ctx $"/account/unlink/{identity.Provider}" (btnSm Ghost) Theme.LinkOff t.UnlinkButton
                }
        }

        if last then
            p(id = "unlink-last", class' = "text-label-md font-normal text-on-surface-variant") { t.UnlinkLast }

        match linkable with
        | [] -> Fragment()
        | providers ->
            div(class' = "flex flex-wrap gap-2") {
                for p in providers do
                    a(href = $"/account/link/{p.Name}", class' = btnSm Secondary).data("link-provider", p.Name) {
                        icon Theme.Link
                        t.LinkProvider p.DisplayName
                    }
            }
    }
    |> accountSection "identities" Theme.Key t.AccountSignInMethods

let private syncs (t: Texts) (m: AccountModel) =
    match m.Syncs with
    | [] -> p(class' = "text-body-md text-on-surface-variant") { t.NoSyncsYet } :> HtmlElement
    | syncs ->
        ul(class' = "divide-y divide-outline-variant/40") {
            for sync in syncs do
                li(class' = "flex flex-col gap-1 py-3 first:pt-0 last:pb-0").data("sync", sync.Status) {
                    div(class' = "flex flex-wrap items-center gap-x-3 gap-y-1 text-body-md text-on-surface") {
                        Layout.timeElement t "relative" sync.ReceivedAt
                        pill (syncTone sync.Status) (t.SyncStatus sync.Status)
                    }

                    p(class' = "text-label-md font-normal text-on-surface-variant") {
                        t.SyncCounts sync.Processed sync.Unresolved sync.Unsupported sync.Failed
                    }
                }
        }
    |> accountSection "syncs" Theme.Sync t.RecentSyncsTitle

let private securityActivity (t: Texts) (providerName: string -> string) (m: AccountModel) =
    ul(class' = "divide-y divide-outline-variant/40") {
        for event in m.Events do
            li(class' = "flex flex-col gap-1 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-baseline sm:justify-between sm:gap-6")
                .data("security-event", event.Event) {
                span(class' = "text-body-md text-on-surface") {
                    t.SecurityEventLabel event.Event

                    match event.Provider with
                    | Some provider -> $" ({providerName provider})"
                    | None -> ""

                    if event.Actor = "operator" then $" · {t.ByOperator}" else ""
                }

                span(class' = "shrink-0 text-label-md font-normal text-on-surface-variant") { Layout.timeElement t "relative" event.OccurredAt }
            }
    }
    |> accountSection "security" Theme.Shield t.SecurityEventsTitle

/// Quiet on purpose: an outline and no fill; the destructive styling waits for the confirmation page
let private dangerZone (t: Texts) =
    section(class' = "flex flex-col gap-4 rounded-lg border border-error/30 p-6 sm:flex-row sm:items-center sm:justify-between md:p-8") {
        div(class' = "space-y-1") {
            h2(class' = "text-headline-md text-on-surface") { t.DeleteTitle }
            p(class' = "max-w-prose text-body-md text-on-surface-variant") { t.DeleteWarning }
        }

        a(href = "/account/delete", class' = btnSm Secondary + " shrink-0 text-error").data("delete-account", "") {
            icon Theme.DeleteForever
            t.DeleteAccountLink
        }
    }

let view (ctx: HttpContext) (t: Texts) (providers: AuthConfig.FrunchyProvider list) (msg: string) (isOperator: bool) (m: AccountModel) =
    let providerName (name: string) =
        providers |> List.tryFind (fun p -> p.Name = name) |> Option.map _.DisplayName |> Option.defaultValue name

    let linkable =
        providers |> List.filter (fun p -> m.Identities |> List.forall (fun i -> i.Provider <> p.Name))

    div(class' = "mx-auto w-full max-w-3xl space-y-6") {
        pageHeading t.AccountTitle None

        match notice t msg with
        | Some(isError, text) -> (alert (if isError then Failure else Success) text).data("account-notice", msg)
        | None -> Fragment()

        if isOperator then
            div() {
                a(href = "/review/mal", class' = btn Secondary).data("review-link", "") {
                    icon Theme.Link
                    t.ReviewAccountLink
                }
            }

        profile ctx t msg m
        extension t m
        binding ctx t m
        identities ctx t providerName linkable m
        syncs t m
        securityActivity t providerName m
        dangerZone t
    }
    |> Layout.page ctx t t.AccountTitle

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private load (db: DbConfig) (userId: Guid) : Task<AccountModel option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use userCmd =
            new NpgsqlCommand(
                "SELECT username, display_name, sync_key_hash IS NOT NULL, cr_account_id IS NOT NULL FROM users WHERE id = @id",
                connection
            )

        userCmd.Parameters.AddWithValue("id", userId) |> ignore

        let! user =
            task {
                use! r = userCmd.ExecuteReaderAsync()
                return if r.Read() then Some(r.GetString 0, r.GetString 1, r.GetBoolean 2, r.GetBoolean 3) else None
            }

        match user with
        | None -> return None
        | Some(username, displayName, keyActive, bound) ->
            use identitiesCmd =
                new NpgsqlCommand("SELECT provider, linked_at FROM user_identities WHERE user_id = @id ORDER BY linked_at", connection)

            identitiesCmd.Parameters.AddWithValue("id", userId) |> ignore

            let! identities =
                task {
                    use! r = identitiesCmd.ExecuteReaderAsync()
                    let rows = ResizeArray<Identity>()

                    while r.Read() do
                        rows.Add { Provider = r.GetString 0; LinkedAt = r.GetDateTime 1 }

                    return List.ofSeq rows
                }

            use syncsCmd =
                new NpgsqlCommand(
                    """
                    SELECT received_at, status, processed, unresolved, unsupported, failed
                    FROM cr_imports WHERE user_id = @id ORDER BY received_at DESC, id LIMIT 10
                    """,
                    connection
                )

            syncsCmd.Parameters.AddWithValue("id", userId) |> ignore

            let! syncs =
                task {
                    use! r = syncsCmd.ExecuteReaderAsync()
                    let rows = ResizeArray<Sync>()

                    while r.Read() do
                        rows.Add
                            { ReceivedAt = r.GetDateTime 0
                              Status = r.GetString 1
                              Processed = r.GetInt32 2
                              Unresolved = r.GetInt32 3
                              Unsupported = r.GetInt32 4
                              Failed = r.GetInt32 5 }

                    return List.ofSeq rows
                }

            let! events = SecurityEvents.recent db userId 20

            return
                Some
                    { Username = username
                      DisplayName = displayName
                      KeyActive = keyActive
                      Bound = bound
                      Identities = identities
                      Syncs = syncs
                      Events = events }
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            match! load (ctx.GetService<DbConfig>()) userId with
            | Some model ->
                let providers = ctx.RequestServices.GetServices<AuthConfig.FrunchyProvider>() |> List.ofSeq
                let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
                return! ctx.WriteHtmlView(view ctx (Texts.current ()) providers msg (WebAuth.isOperator ctx) model)
            | None ->
                do! WebAuth.signOut ctx
                return! WebAuth.redirectTo ctx "/signin"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/account" handler ]
