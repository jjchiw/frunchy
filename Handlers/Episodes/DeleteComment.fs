module FrunchyBackend.Handlers.Episodes.DeleteComment

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Data access (specs/009-episode-comments/contracts/episode-page.md, data-model.md "State transitions")
// ---------------------------------------------------------------------------

/// The episode page of the user's own live comment; nothing for anyone else's, a deleted one or an unknown id
let private ownEpisodePage (connection: NpgsqlConnection) (id: string) (userId: Guid) : Task<string option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT e.series_id, e.id
                FROM comments c
                JOIN cr_episodes e ON e.id = c.cr_episode_id
                WHERE c.id = @id AND c.deleted_at IS NULL AND c.author_user_id = @me
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("me", userId) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            return Some $"/anime/{r.GetString 0}/episode/{r.GetString 1}"
        else
            return None
    }

/// The tombstone: the row stays (a later integration needs it), the content and the spoiler warning go. True when
/// this call deleted it.
let private tombstone (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (id: string) (userId: Guid) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                UPDATE comments
                SET deleted_at = now(), content = '', summary = NULL, sensitive = false
                WHERE id = @id AND author_user_id = @me AND deleted_at IS NULL
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("me", userId) |> ignore
        let! deleted = cmd.ExecuteNonQueryAsync()
        return deleted > 0
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) =
    let t = Texts.current ()
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// POST /comments/{id}/delete. The author check first (anyone else, signed out included, gets the plain 404 page),
/// then the antiforgery check, then the tombstone, then a redirect to the episode page.
let handler (id: string) : EndpointHandler =
    fun ctx ->
        task {
            match WebAuth.currentUserId ctx with
            | Some userId when CommentRules.commentIdPattern.IsMatch id ->
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! ownEpisodePage connection id userId with
                | None -> return! notFound ctx
                | Some page ->
                    let! _ = WebAuth.readValidatedForm ctx
                    // With federation on, the delete and its delete_comment are one transaction (the outbox)
                    use tx = connection.BeginTransaction()
                    let! deleted = tombstone connection tx id userId

                    if deleted && Fediverse.isEnabled (ctx.GetService<Federation.FederationSettings>()) then
                        do! Fediverse.enqueueCommentChange connection tx userId "delete_comment" id

                    do! tx.CommitAsync()
                    return! WebAuth.redirectTo ctx page
            | _ -> return! notFound ctx
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint =
    POST [ routef "/comments/{%s}/delete" handler |> configureEndpoint _.RequireRateLimiting("comment-write") ]
