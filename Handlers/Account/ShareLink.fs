/// Feature 017 (US3): the owner's share link. One per user: creating replaces the old one, turning it off ends it.
/// The token is 256 random bits, shown once on the page this POST renders (like the sync key) and stored only as a
/// SHA-256 hash with a fresh link id; grants in browsers name the link id, so replacing or turning off the link ends
/// every browser's access at once.
module FrunchyBackend.Handlers.Account.ShareLink

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
// View: the link in a selectable box with a copy button, shown this once
// ---------------------------------------------------------------------------

/// The link is the box's only text (tests read it from data-share-link)
let private linkBox (t: Texts) (link: string) =
    div(class' = "space-y-3").attr("x-data", "{ copied: false, canCopy: !!navigator.clipboard }") {
        div(class' = "rounded bg-surface-container-lowest p-4 font-mono text-body-md text-primary break-all select-all border border-outline-variant")
            .data("share-link", "")
            .attr("x-ref", "link")
            .attr("translate", "no") {
            link
        }

        button(type' = "button", class' = btnSm Secondary)
            .attr("x-show", "canCopy")
            .attr("x-on:click", "navigator.clipboard.writeText($refs.link.textContent.trim()).then(() => { copied = true; setTimeout(() => copied = false, 2000) })") {
            icon Theme.ContentCopy
            span().attr("x-show", "!copied") { t.CopyLink }
            span().attr("x-show", "copied").attr("style", "display: none") { t.LinkCopied }
        }

        span(class' = "sr-only").attr("aria-live", "polite") { span().attr("x-show", "copied").attr("style", "display: none") { t.LinkCopied } }
    }

/// The only time the link is ever shown
let shownView (ctx: HttpContext) (t: Texts) (link: string) =
    narrow () {
        div(class' = "fr-card space-y-6") {
            pageHeading t.ShareLinkShownTitle None
            linkBox t link
            alert Notice t.ShareLinkShownOnce

            a(href = "/account#visibility", class' = btn Secondary) {
                icon Theme.ArrowBack
                t.BackToAccount
            }
        }
    }
    |> Layout.page ctx t t.ShareLinkShownTitle

// ---------------------------------------------------------------------------
// Data access: the change and its security event share one transaction
// ---------------------------------------------------------------------------

/// Store a new link (hash and a fresh id), replacing any old one. Returns true when it replaced a link.
let private createOrReplace (db: DbConfig) (userId: Guid) (tokenHash: string) : Task<bool> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use existingCmd =
            new NpgsqlCommand("SELECT share_link_id IS NOT NULL FROM profile_settings WHERE user_id = @id FOR UPDATE", connection, tx)

        existingCmd.Parameters.AddWithValue("id", userId) |> ignore

        let! existing = existingCmd.ExecuteScalarAsync()
        let replaced = (match existing with | :? bool as b -> b | _ -> false)

        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO profile_settings (user_id, share_token_hash, share_link_id, share_link_created_at)
                VALUES (@id, @hash, @link, now())
                ON CONFLICT (user_id) DO UPDATE
                SET share_token_hash = EXCLUDED.share_token_hash,
                    share_link_id = EXCLUDED.share_link_id,
                    share_link_created_at = EXCLUDED.share_link_created_at,
                    updated_at = now()
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        cmd.Parameters.AddWithValue("hash", tokenHash) |> ignore
        cmd.Parameters.AddWithValue("link", Guid.NewGuid()) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()

        let event = if replaced then SecurityEventType.ShareLinkReplaced else SecurityEventType.ShareLinkCreated
        do! SecurityEvents.record connection tx userId event None Actor.User
        do! tx.CommitAsync()
        return replaced
    }

/// Empty the link columns. The event is recorded only when there was a link to turn off.
let private revoke (db: DbConfig) (userId: Guid) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE profile_settings
                SET share_token_hash = NULL, share_link_id = NULL, share_link_created_at = NULL, updated_at = now()
                WHERE user_id = @id AND share_link_id IS NOT NULL
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        let! changed = cmd.ExecuteNonQueryAsync()

        if changed > 0 then
            do! SecurityEvents.record connection tx userId SecurityEventType.ShareLinkRevoked None Actor.User

        do! tx.CommitAsync()
    }

// ---------------------------------------------------------------------------
// Handlers: signed in, then the antiforgery check and the form. No fresh sign-in: no credential is exposed.
// ---------------------------------------------------------------------------

/// POST /account/share-link: create or replace; renders the page that shows the link once (no redirect)
let create: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx
            let token = ShareToken.generate ()
            let! _ = createOrReplace (ctx.GetService<DbConfig>()) userId (ShareToken.hash token)
            let link = $"{ctx.Request.Scheme}://{ctx.Request.Host}/s/{token}"
            ctx.Response.Headers.CacheControl <- "no-store"
            return! ctx.WriteHtmlView(shownView ctx (Texts.current ()) link)
        })

/// POST /account/share-link/revoke
let revokeHandler: EndpointHandler =
    WebAuth.requireSignedIn (fun userId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx
            do! revoke (ctx.GetService<DbConfig>()) userId
            return! WebAuth.redirectTo ctx "/account?msg=share-link-revoked#visibility"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("account-write")

let endpoint =
    [ POST [ route "/account/share-link" create |> limited
             route "/account/share-link/revoke" revokeHandler |> limited ] ]
