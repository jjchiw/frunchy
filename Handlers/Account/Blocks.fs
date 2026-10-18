/// Feature 015 (US6, FR-015): a user adds and lifts their fediverse blocks from the account page. The page's own
/// section is rendered by `AccountPage` (it lists the blocks); these are the two POSTs. The calls to Megáfono are
/// synchronous, and every answer comes back to `/account#blocks` as a notice. Served only with federation on.
module FrunchyBackend.Handlers.Account.Blocks

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Handlers: signed in, then the antiforgery check and the form (readValidatedForm), then the input rule, then
// Megáfono. A username that can't federate (a trailing dash from before feature 013) has no blocks to manage.
// ---------------------------------------------------------------------------

let private post (action: string) (work: HttpClient -> DbConfig -> Guid -> IFormCollection -> Task<string>) : EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx
            let log = ctx.GetLogger()

            let canFederate =
                match WebAuth.currentUsername ctx with
                | Some username -> Fediverse.isValidHandle username
                | None -> false

            let! key =
                if canFederate then
                    work (ctx.GetService<IHttpClientFactory>().CreateClient MegafonoClient.ClientName) (ctx.GetService<DbConfig>()) userId form
                else
                    Task.FromResult "block-error"

            log.LogInformation("account blocks {Action} by {User}: {Key}", action, userId, key)
            return! WebAuth.redirectTo ctx $"/account?msg={key}#blocks"
        })

/// POST /account/blocks/add: `target` (`@user@host`, `user@host`, an `https://` actor URL, or a domain)
let addHandler: EndpointHandler =
    post "add" (fun client db userId form ->
        BlockActions.add db client (BlockActions.User userId) userId (WebAuth.formValue form "target"))

/// POST /account/blocks/remove: `kind` ("actor" or "domain") and `target`, as the list showed them
let removeHandler: EndpointHandler =
    post "remove" (fun client db userId form ->
        BlockActions.remove db client (BlockActions.User userId) userId (WebAuth.formValue form "kind") (WebAuth.formValue form "target"))

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("account-write")

let endpoint =
    [ POST [ route "/account/blocks/add" addHandler |> limited
             route "/account/blocks/remove" removeHandler |> limited ] ]
