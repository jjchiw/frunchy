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

type AccountModel =
    { Username: string
      DisplayName: string
      KeyActive: bool
      Bound: bool
      Identities: Identity list }

let private row (label': string) (hook: string) (content: HtmlElement) =
    div(class' = "flex flex-col gap-1 py-3 border-b border-base-300").data("account", hook) {
        span(class' = "text-sm text-gray-400") { label' }
        content
    }

let view (ctx: HttpContext) (t: Texts) (providers: AuthConfig.FrunchyProvider list) (m: AccountModel) =
    let providerName (name: string) =
        providers |> List.tryFind (fun p -> p.Name = name) |> Option.map _.DisplayName |> Option.defaultValue name

    div(class' = "max-w-2xl mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.AccountTitle }

        div(class' = "card bg-base-300") {
            div(class' = "card-body") {
                row t.AccountUsername "username" (span() { m.Username })
                row t.AccountDisplayName "display-name" (span() { m.DisplayName })
                row t.AccountDashboard "dashboard" (a(href = $"/u/{m.Username}", class' = "link") { $"/u/{m.Username}" })

                row t.AccountSyncKey "key-active" (
                    div(class' = "flex flex-col gap-2") {
                        span().data("key-active", (if m.KeyActive then "true" else "false")) {
                            if m.KeyActive then t.AccountKeyActive else t.AccountKeyMissing
                        }

                        a(href = "/account/key", class' = "btn btn-primary btn-sm w-fit") { t.ConnectExtension }
                    }
                )

                row t.AccountBinding "binding" (span() { if m.Bound then t.AccountBound else t.AccountUnbound })

                row t.AccountSignInMethods "identities" (
                    ul() {
                        for identity in m.Identities do
                            li().data("identity", identity.Provider) { providerName identity.Provider }
                    }
                )
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
            use! r = identitiesCmd.ExecuteReaderAsync()
            let identities = ResizeArray<Identity>()

            while r.Read() do
                identities.Add { Provider = r.GetString 0; LinkedAt = r.GetDateTime 1 }

            return
                Some
                    { Username = username
                      DisplayName = displayName
                      KeyActive = keyActive
                      Bound = bound
                      Identities = List.ofSeq identities }
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
                return! ctx.WriteHtmlView(view ctx (Texts.current ()) providers model)
            | None ->
                do! WebAuth.signOut ctx
                return! WebAuth.redirectTo ctx "/signin"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ route "/account" handler ]
