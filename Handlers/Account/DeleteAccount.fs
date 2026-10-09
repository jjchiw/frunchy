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
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View
// ---------------------------------------------------------------------------

let view (ctx: HttpContext) (t: Texts) (username: string) (mismatch: bool) =
    let confirmInput =
        input(
            type' = "text",
            id = "confirmUsername",
            name = "confirmUsername",
            class' = "fr-input fr-mono",
            required = true,
            autocomplete = "off"
        )
            .attr("autocapitalize", "none")
            .attr("spellcheck", "false")

    if mismatch then
        confirmInput.attr("aria-invalid", "true").attr("aria-describedby", "confirmUsername-error") |> ignore

    narrow () {
        div(class' = "fr-card space-y-6 border-error/30").data("delete-account", "") {
            span(class' = "flex h-12 w-12 items-center justify-center rounded-full bg-error/10 text-headline-md text-error") { icon Theme.Warning }
            pageHeading t.DeleteTitle (Some t.DeleteWarning)

            ul(class' = "space-y-3") {
                for item in t.DeleteItems do
                    li(class' = "flex items-start gap-3 text-body-md text-on-surface") {
                        span(class' = "text-error") { icon Theme.DeleteForever }
                        span() { item }
                    }
            }

            form(method = "post", action = "/account/delete", class' = "space-y-4 border-t border-outline-variant/40 pt-6") {
                ctx.GetAntiforgeryInput()

                div() {
                    label(for' = "confirmUsername", class' = "mb-1.5 block text-label-md text-on-surface") { t.DeleteTypeUsername username }
                    confirmInput

                    if mismatch then
                        p(id = "confirmUsername-error", class' = "fr-field-error").data("delete-error", "") {
                            icon Theme.ErrorMark
                            span() { t.DeleteMismatch }
                        }
                }

                div(class' = "flex flex-col gap-2") {
                    button(type' = "submit", class' = btn Destructive + " w-full") {
                        icon Theme.DeleteForever
                        t.DeleteButton
                    }

                    a(href = "/account", class' = btn Ghost + " w-full") { t.BackToAccount }
                }
            }
        }
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
