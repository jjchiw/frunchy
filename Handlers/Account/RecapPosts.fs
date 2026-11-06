/// Feature 030: the user turns weekly recap posts on the fediverse on or off from `/account#recap-posts`. The section is
/// rendered by `AccountPage`; this is the one POST. Served only with federation on (absent otherwise: the plain 404).
/// Turning it off also deletes the recaps already posted (`RecapPosts.queueRemoval`), in the same transaction.
module FrunchyBackend.Handlers.Account.RecapPosts

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Data access: the setting, the removal and the security event share one transaction. The event is recorded only
// when the state really changed; turning off always queues the removal (it queues nothing when there is nothing posted).
// ---------------------------------------------------------------------------

let private save (db: DbConfig) (userId: Guid) (on: bool) : Task<bool> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        let! changed = RecapPosts.setEnabled connection tx userId on

        if not on then
            do! RecapPosts.queueRemoval connection tx userId

        if changed then
            let event = if on then SecurityEventType.RecapPostsEnabled else SecurityEventType.RecapPostsDisabled
            do! SecurityEvents.record connection tx userId event None Actor.User

        do! tx.CommitAsync()
        return changed
    }

// ---------------------------------------------------------------------------
// Handler: signed in, then the antiforgery check and the form, then the value rule. Nothing changes on a bad value.
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! form = WebAuth.readValidatedForm ctx

            // Exactly the two words the form sends
            match WebAuth.formValue form "enabled" with
            | "on"
            | "off" as value ->
                let on = (value = "on")
                let! changed = save (ctx.GetService<DbConfig>()) userId on
                ctx.GetLogger().LogInformation("account recap posts {State} by {User} (changed: {Changed})", value, userId, changed)
                return! WebAuth.redirectTo ctx $"/account?msg=recap-posts-{value}#recap-posts"
            | _ -> return! WebAuth.redirectTo ctx "/account?msg=recap-posts-invalid#recap-posts"
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint =
    [ POST [ route "/account/recap-posts" handler |> configureEndpoint _.RequireRateLimiting("account-write") ] ]
