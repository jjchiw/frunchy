module FrunchyBackend.Handlers.Episodes.HideComment

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Data access (specs/009-episode-comments/research.md R11, R12): hiding is a flag on the row, so it is undone
// by clearing it. Hidden comments stay on Frunchy only; nothing is deleted.
// ---------------------------------------------------------------------------

/// Hide or unhide a live comment. The row's author when something changed; None when the comment was already in
/// that state (or doesn't exist, which the page lookup tells apart).
let private setHidden
    (connection: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    (hiding: bool)
    (id: string)
    (operatorId: Guid)
    : Task<Guid option option> =
    task {
        use cmd =
            if hiding then
                new NpgsqlCommand(
                    """
                    UPDATE comments SET hidden_at = now(), hidden_by = @op
                    WHERE id = @id AND deleted_at IS NULL AND hidden_at IS NULL
                    RETURNING author_user_id
                    """,
                    connection,
                    tx
                )
            else
                new NpgsqlCommand(
                    """
                    UPDATE comments SET hidden_at = NULL, hidden_by = NULL
                    WHERE id = @id AND deleted_at IS NULL AND hidden_at IS NOT NULL
                    RETURNING author_user_id
                    """,
                    connection,
                    tx
                )

        cmd.Parameters.AddWithValue("id", id) |> ignore

        if hiding then
            cmd.Parameters.AddWithValue("op", operatorId) |> ignore

        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            // changed: the author (None for a remote author, who has no account here)
            return Some(if r.IsDBNull 0 then None else Some(r.GetGuid 0))
        else
            return None
    }

/// The episode page and the author's username of a live comment; None for an unknown or deleted one
let private locate (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (id: string) : Task<(string * string option) option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT e.series_id, e.id, u.username
                FROM comments c
                JOIN cr_episodes e ON e.id = c.cr_episode_id
                LEFT JOIN users u ON u.id = c.author_user_id
                WHERE c.id = @id AND c.deleted_at IS NULL
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            return Some($"/anime/{r.GetString 0}/episode/{r.GetString 1}", (if r.IsDBNull 2 then None else Some(r.GetString 2)))
        else
            return None
    }

// ---------------------------------------------------------------------------
// Handlers: wrapped in requireOperator, outermost, before any form is read
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) =
    let t = Texts.current ()
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// An operator (404 for anyone else), then the antiforgery check and the form, then the id rule, then one
/// transaction: the update and, only when it changed something, the author's and the operator's security events.
let private post (hiding: bool) (id: string) : EndpointHandler =
    Layout.requireOperator (fun operatorId ctx ->
        task {
            let! _ = WebAuth.readValidatedForm ctx

            if not (CommentRules.commentIdPattern.IsMatch id) then
                return! notFound ctx
            else
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()
                use tx = connection.BeginTransaction()

                let! changed = setHidden connection tx hiding id operatorId

                match! locate connection tx id with
                | None ->
                    do! tx.RollbackAsync()
                    return! notFound ctx
                | Some(page, authorName) ->
                    match changed with
                    | None ->
                        // already in that state: nothing to record
                        do! tx.RollbackAsync()
                    | Some author ->
                        let event = if hiding then SecurityEventType.CommentHidden else SecurityEventType.CommentUnhidden

                        match author with
                        | Some authorId -> do! SecurityEvents.record connection tx authorId event None Actor.Operator
                        | None -> ()

                        do! SecurityEvents.record connection tx operatorId event None Actor.User
                        do! tx.CommitAsync()

                        ctx.GetLogger()
                            .LogInformation(
                                "comments {Event} {Comment} by {Operator} (author {Author})",
                                SecurityEventType.toDb event,
                                id,
                                WebAuth.currentUsername ctx |> Option.defaultValue "",
                                authorName |> Option.defaultValue "remote"
                            )

                    return! WebAuth.redirectTo ctx $"{page}#comment-{id}"
        })

/// POST /comments/{id}/hide
let hideHandler (id: string) : EndpointHandler = post true id

/// POST /comments/{id}/unhide
let unhideHandler (id: string) : EndpointHandler = post false id

// ---------------------------------------------------------------------------
// Endpoints: `review-write` counts operators only, so an outsider never sees a 429
// ---------------------------------------------------------------------------

let private limited (route: Endpoint) = route |> configureEndpoint _.RequireRateLimiting("review-write")

let endpoint =
    [ POST [ routef "/comments/{%s}/hide" hideHandler |> limited
             routef "/comments/{%s}/unhide" unhideHandler |> limited ] ]
