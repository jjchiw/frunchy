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
    | _ -> None

let private row (label': string) (hook: string) (content: HtmlElement) =
    div(class' = "flex flex-col gap-1 py-3 border-b border-base-300").data("account", hook) {
        span(class' = "text-sm text-gray-400") { label' }
        content
    }

let private postButton (ctx: HttpContext) (action: string) (cssClass: string) (text: string) =
    form(method = "post", action = action, class' = "inline") {
        ctx.GetAntiforgeryInput()
        button(type' = "submit", class' = cssClass) { text }
    }

let view (ctx: HttpContext) (t: Texts) (providers: AuthConfig.FrunchyProvider list) (msg: string) (m: AccountModel) =
    let providerName (name: string) =
        providers |> List.tryFind (fun p -> p.Name = name) |> Option.map _.DisplayName |> Option.defaultValue name

    let linkable =
        providers |> List.filter (fun p -> m.Identities |> List.forall (fun i -> i.Provider <> p.Name))

    div(class' = "max-w-2xl mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.AccountTitle }

        match notice t msg with
        | Some(isError, text) ->
            div(class' = (if isError then "alert alert-error" else "alert alert-success")).data("account-notice", msg) { span() { text } }
        | None -> Fragment()

        div(class' = "card bg-base-300") {
            div(class' = "card-body") {
                row t.AccountUsername "username" (span() { m.Username })

                row t.AccountDisplayName "display-name" (
                    form(method = "post", action = "/account/display-name", class' = "flex gap-2") {
                        ctx.GetAntiforgeryInput()
                        input(type' = "text", name = "displayName", value = m.DisplayName, class' = "input input-bordered input-sm w-full", required = true)
                        button(type' = "submit", class' = "btn btn-sm") { t.SaveButton }
                    }
                )

                row t.AccountDashboard "dashboard" (a(href = $"/u/{m.Username}", class' = "link") { $"/u/{m.Username}" })

                row t.AccountSyncKey "key-active" (
                    div(class' = "flex flex-col gap-2") {
                        span().data("key-active", (if m.KeyActive then "true" else "false")) {
                            if m.KeyActive then t.AccountKeyActive else t.AccountKeyMissing
                        }

                        a(href = "/account/key", class' = "btn btn-primary btn-sm w-fit") { t.ConnectExtension }
                    }
                )

                row t.AccountBinding "binding" (
                    div(class' = "flex flex-col gap-2") {
                        span() { if m.Bound then t.AccountBound else t.AccountUnbound }

                        if m.Bound then
                            postButton ctx "/account/binding/reset" "btn btn-sm btn-outline w-fit" t.BindingResetButton
                    }
                )

                row t.AccountSignInMethods "identities" (
                    div(class' = "flex flex-col gap-2") {
                        ul(class' = "space-y-1") {
                            for identity in m.Identities do
                                li(class' = "flex items-center gap-3").data("identity", identity.Provider) {
                                    span() { providerName identity.Provider }

                                    if m.Identities.Length > 1 then
                                        postButton ctx $"/account/unlink/{identity.Provider}" "btn btn-xs btn-ghost" t.UnlinkButton
                                }
                        }

                        div(class' = "flex gap-2") {
                            for p in linkable do
                                a(href = $"/account/link/{p.Name}", class' = "btn btn-sm btn-outline").data("link-provider", p.Name) {
                                    t.LinkProvider p.DisplayName
                                }
                        }
                    }
                )
            }
        }

        div(class' = "card bg-base-300") {
            div(class' = "card-body space-y-2") {
                h2(class' = "card-title") { t.RecentSyncsTitle }

                match m.Syncs with
                | [] -> p(class' = "text-gray-400") { t.NoSyncsYet }
                | syncs ->
                    ul(class' = "space-y-2") {
                        for sync in syncs do
                            li(class' = "flex flex-col").data("sync", sync.Status) {
                                span() {
                                    Layout.timeElement t "relative" sync.ReceivedAt
                                    " · "
                                    t.SyncStatus sync.Status
                                }

                                span(class' = "text-sm text-gray-400") { t.SyncCounts sync.Processed sync.Unresolved sync.Unsupported sync.Failed }
                            }
                    }
            }
        }

        div(class' = "card bg-base-300") {
            div(class' = "card-body space-y-2") {
                h2(class' = "card-title") { t.SecurityEventsTitle }

                ul(class' = "space-y-1") {
                    for event in m.Events do
                        li(class' = "text-sm").data("security-event", event.Event) {
                            Layout.timeElement t "relative" event.OccurredAt
                            " · "
                            t.SecurityEventLabel event.Event

                            match event.Provider with
                            | Some provider -> $" ({providerName provider})"
                            | None -> ""

                            if event.Actor = "operator" then $" · {t.ByOperator}" else ""
                        }
                }
            }
        }
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
                return! ctx.WriteHtmlView(view ctx (Texts.current ()) providers msg model)
            | None ->
                do! WebAuth.signOut ctx
                return! WebAuth.redirectTo ctx "/signin"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/account" handler ]
