/// Feature 015 (US6, FR-016): operators block servers and accounts for the whole instance. Same rules as the other
/// review pages: every handler is wrapped in `Layout.requireOperator`, outermost, before any form is read, so
/// everyone else gets the plain 404 on every route and verb; the `review-write` limiter counts operators only, so an
/// outsider never sees a 429. The change is recorded on the operator's own account. Served only with federation on.
module FrunchyBackend.Handlers.Review.FediverseBlocks

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// View: the intro, the last notice, the add form and the instance's blocks
// ---------------------------------------------------------------------------

let private view (ctx: HttpContext) (t: Texts) (msg: string) (listing: BlockActions.Listing) =
    div(class' = "mx-auto w-full max-w-3xl space-y-6").data("fediverse-review", "") {
        pageHeading t.InstanceBlocksTitle (Some t.InstanceBlocksIntro)
        Blocks.notice t msg

        div(class' = "fr-card") { Blocks.addForm ctx t "/review/fediverse/blocks/add" }

        div(class' = "fr-card") {
            Blocks.list ctx t "instance-blocks" "/review/fediverse/blocks/remove" t.InstanceBlocksEmpty t.InstanceBlocksLoadFailed listing
        }
    }
    |> Layout.page ctx t t.InstanceBlocksTitle

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private client (ctx: HttpContext) =
    ctx.GetService<IHttpClientFactory>().CreateClient MegafonoClient.ClientName

/// GET /review/fediverse
let handler: EndpointHandler =
    Layout.requireOperator (fun _ ctx ->
        task {
            let t = Texts.current ()
            let msg = ctx.TryGetQueryValue "msg" |> Option.defaultValue ""
            let! listing = BlockActions.list (client ctx) BlockActions.Instance
            return! ctx.WriteHtmlView(view ctx t msg listing)
        })

/// A POST: operator (404 for anyone else), then the antiforgery check and the form, then Megáfono. Always
/// redirects back with a notice.
let private post (action: string) (work: HttpClient -> DbConfig -> Guid -> IFormCollection -> Task<string>) : EndpointHandler =
    Layout.requireOperator (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx
            let! key = work (client ctx) (ctx.GetService<DbConfig>()) userId form
            ctx.GetLogger().LogInformation("review fediverse blocks {Action} by {User}: {Key}", action, userId, key)
            return! WebAuth.redirectTo ctx $"/review/fediverse?msg={key}"
        })

/// POST /review/fediverse/blocks/add: `target` (a handle, an actor URL or a domain)
let addHandler: EndpointHandler =
    post "add" (fun client db userId form -> BlockActions.add db client BlockActions.Instance userId (WebAuth.formValue form "target"))

/// POST /review/fediverse/blocks/remove: `kind` ("actor" or "domain") and `target`
let removeHandler: EndpointHandler =
    post "remove" (fun client db userId form ->
        BlockActions.remove db client BlockActions.Instance userId (WebAuth.formValue form "kind") (WebAuth.formValue form "target"))

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("review-write")

let endpoint =
    [ GET [ route "/review/fediverse" handler ]
      POST [ route "/review/fediverse/blocks/add" addHandler |> limited
             route "/review/fediverse/blocks/remove" removeHandler |> limited ] ]
