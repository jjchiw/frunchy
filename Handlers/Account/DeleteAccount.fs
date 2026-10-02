module FrunchyBackend.Handlers.Account.DeleteAccount

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

let view (ctx: HttpContext) (t: Texts) (username: string) (mismatch: bool) =
    div(class' = "max-w-md mx-auto space-y-6") {
        h1(class' = "text-3xl font-bold") { t.DeleteTitle }
        div(class' = "alert alert-warning") { span() { t.DeleteWarning } }

        if mismatch then
            div(class' = "alert alert-error").data("delete-error", "") { span() { t.DeleteMismatch } }

        form(method = "post", action = "/account/delete", class' = "space-y-4") {
            ctx.GetAntiforgeryInput()

            label(class' = "form-control w-full") {
                div(class' = "label") { span(class' = "label-text") { t.DeleteTypeUsername username } }
                input(type' = "text", name = "confirmUsername", class' = "input input-bordered w-full", required = true, autocomplete = "off")
            }

            button(type' = "submit", class' = "btn btn-error w-full") { t.DeleteButton }
        }

        a(href = "/account", class' = "link") { t.BackToAccount }
    }
    |> Layout.page ctx t t.DeleteTitle

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private usernameOf (db: DbConfig) (userId: Guid) : Task<string option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT username FROM users WHERE id = @id", connection)
        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! name = cmd.ExecuteScalarAsync()
        return match name with :? string as n -> Some n | _ -> None
    }

// ---------------------------------------------------------------------------
// Handlers: both need a sign-in within the last 10 minutes (FR-014); same effect as user-delete (FR-013)
// ---------------------------------------------------------------------------

let page: EndpointHandler =
    WebAuth.requireFresh "/account/delete" (fun userId ctx ->
        task {
            match! usernameOf (ctx.GetService<DbConfig>()) userId with
            | Some username -> return! ctx.WriteHtmlView(view ctx (Texts.current ()) username false)
            | None -> return! WebAuth.redirectTo ctx "/signin"
        })

let submit: EndpointHandler =
    WebAuth.requireFresh "/account/delete" (fun userId ctx ->
        task {
            let db = ctx.GetService<DbConfig>()
            let! form = WebAuth.readValidatedForm ctx
            let typed = (WebAuth.formValue form "confirmUsername").ToLowerInvariant()

            match! usernameOf db userId with
            | Some username when typed = username ->
                let! _ = AccountActions.deleteUser db userId
                do! WebAuth.signOut ctx
                return! WebAuth.redirectTo ctx "/?deleted=1"
            | Some username ->
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                return! ctx.WriteHtmlView(view ctx (Texts.current ()) username true)
            | None -> return! WebAuth.redirectTo ctx "/signin"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/account/delete" page ]
      POST [ route "/account/delete" submit |> configureEndpoint _.RequireRateLimiting("account-write") ] ]
