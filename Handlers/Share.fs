/// Feature 017 (US3): `GET /s/{token}` opens a shared profile. A valid token adds a grant to the browser's
/// data-protected cookie and redirects (303) to `/u/{username}`; the token never rides along in a profile address.
/// Anything else, a malformed, unknown, replaced or turned-off token, gets the plain 404 page and no cookie.
module FrunchyBackend.Handlers.Share

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Data access: the token's hash against the one link each user can have
// ---------------------------------------------------------------------------

type private Shared =
    { UserId: Guid
      Username: string
      LinkId: Guid }

let private findByHash (db: DbConfig) (hash: string) : Task<Shared option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT u.id, u.username, s.share_link_id
                FROM profile_settings s JOIN users u ON u.id = s.user_id
                WHERE s.share_token_hash = @hash AND s.share_link_id IS NOT NULL
                """,
                connection
            )

        cmd.Parameters.AddWithValue("hash", hash) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        return
            if r.Read() then
                Some
                    { UserId = r.GetGuid 0
                      Username = r.GetString 1
                      LinkId = r.GetGuid 2 }
            else
                None
    }

// ---------------------------------------------------------------------------
// Handler: the shape first, then the hash. The owner opening their own link is only redirected.
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) : Task =
    let t = Texts.current ()
    ctx.SetStatusCode StatusCodes.Status404NotFound
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

let handler (token: string) : EndpointHandler =
    fun ctx ->
        task {
            if not (ShareToken.isWellFormed token) then
                return! notFound ctx
            else
                match! findByHash (ctx.GetService<DbConfig>()) (ShareToken.hash token) with
                | None -> return! notFound ctx
                | Some shared ->
                    if WebAuth.currentUserId ctx <> Some shared.UserId then
                        Profiles.grantAccess ctx shared.UserId shared.LinkId

                    // the address never reaches a Referer, and a redirect is never cached
                    ctx.Response.Headers.CacheControl <- "no-store"
                    ctx.Response.StatusCode <- StatusCodes.Status303SeeOther
                    ctx.Response.Headers.Location <- $"/u/{Uri.EscapeDataString shared.Username}"
                    return ()
        }

// ---------------------------------------------------------------------------
// Endpoint: the `share` limiter counts every request, valid or not
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/s/{%s}" handler |> configureEndpoint _.RequireRateLimiting("share") ]
