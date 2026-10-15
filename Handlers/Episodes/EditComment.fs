module FrunchyBackend.Handlers.Episodes.EditComment

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
// Model and view (specs/009-episode-comments/contracts/episode-page.md)
// ---------------------------------------------------------------------------

/// A comment of the signed-in user and the episode it is on
type private Own =
    { Id: string
      Content: string
      Sensitive: bool
      EpisodeId: string
      SeriesId: string
      Number: int option
      Label: string option
      SeasonDisplay: string option
      SeasonNumber: int option
      /// The stored spoiler warning, kept while the comment stays a spoiler
      Summary: string option }

/// What the edit form shows: the text and flag as typed (or stored), and a refusal's error key
type private FormState =
    { Text: string
      Spoiler: bool
      Error: string option }

let private episodePage (o: Own) = $"/anime/{o.SeriesId}/episode/{o.EpisodeId}"

let private editForm (ctx: HttpContext) (t: Texts) (o: Own) (f: FormState) =
    form(method = "post", action = $"/comments/{o.Id}/edit", class' = "space-y-4").data("comment-form", "") {
        ctx.GetAntiforgeryInput()

        div() {
            label(for' = "content", class' = "fr-label") { t.CommentFormLabel }

            let area =
                textarea(id = "content", name = "content", class' = "fr-input h-auto min-h-28 py-2.5")
                    .attr("required", "")
                    .attr("maxlength", "2000")
                    .attr("rows", "6")

            match f.Error with
            | Some _ -> area.attr("aria-invalid", "true").attr("aria-describedby", "comment-error") { f.Text }
            | None -> area { f.Text }

            match f.Error with
            | Some key ->
                p(id = "comment-error", class' = "fr-field-error").attr("role", "alert").data("comment-error", key) {
                    icon Theme.ErrorMark
                    span() { t.CommentError key }
                }
            | None -> Fragment()
        }

        label(class' = "inline-flex min-h-6 items-center gap-2 text-body-md text-on-surface") {
            let box =
                input(type' = "checkbox", name = "spoiler", class' = "h-4 w-4 accent-primary").data("spoiler-toggle", "")

            if f.Spoiler then box.attr("checked", "") else box
            span() { t.CommentSpoilerLabel }
        }

        div(class' = "flex flex-wrap items-center gap-3") {
            button(type' = "submit", class' = btn Primary) { t.CommentSave }

            a(href = $"{episodePage o}#comment-{o.Id}", class' = btn Ghost).data("comment-cancel", "") { t.CommentCancel }
        }
    }

let private deleteForm (ctx: HttpContext) (t: Texts) (o: Own) =
    form(method = "post", action = $"/comments/{o.Id}/delete", class' = "space-y-3 border-t border-outline-variant/40 pt-6")
        .data("comment-delete", "") {
        ctx.GetAntiforgeryInput()
        p(class' = "text-body-md text-on-surface-variant") { t.CommentDeleteConfirm }

        button(type' = "submit", class' = btn Destructive) {
            icon Theme.DeleteForever
            t.CommentDelete
        }
    }

let private view (ctx: HttpContext) (t: Texts) (o: Own) (f: FormState) =
    div(class' = "mx-auto w-full max-w-2xl space-y-6").data("comment-edit-page", o.Id) {
        pageHeading t.CommentEditTitle None

        div(class' = "fr-card space-y-6") {
            editForm ctx t o f
            deleteForm ctx t o
        }
    }
    |> Layout.page ctx t t.CommentEditTitle

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// The user's own live comment with the episode facts the spoiler warning needs; nothing for anyone else's,
/// a deleted one or an unknown id
let private loadOwn (connection: NpgsqlConnection) (id: string) (userId: Guid) : Task<Own option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT c.id, c.content, c.sensitive, c.cr_episode_id, e.series_id, e.episode_number, e.episode_label,
                       sea.display_number, sea.season_number, c.summary
                FROM comments c
                JOIN cr_episodes e ON e.id = c.cr_episode_id
                LEFT JOIN cr_seasons sea ON sea.id = e.season_id
                WHERE c.id = @id AND c.deleted_at IS NULL AND c.author_user_id = @me
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("me", userId) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() then
            let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
            let int' (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)

            return
                Some
                    { Id = r.GetString 0
                      Content = r.GetString 1
                      Sensitive = r.GetBoolean 2
                      EpisodeId = r.GetString 3
                      SeriesId = r.GetString 4
                      Number = int' 5
                      Label = text 6
                      SeasonDisplay = text 7
                      SeasonNumber = int' 8
                      Summary = text 9 }
        else
            return None
    }

/// Change the text and the flag, only when something changed (a no-op leaves `edited_at` alone). Audience and
/// reply are never touched.
let private update
    (connection: NpgsqlConnection)
    (id: string)
    (userId: Guid)
    (content: string)
    (summary: string option)
    : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                UPDATE comments
                SET content = @content, summary = @summary, sensitive = @sensitive, edited_at = now()
                WHERE id = @id AND author_user_id = @me AND deleted_at IS NULL
                  AND (content IS DISTINCT FROM @content OR summary IS DISTINCT FROM @summary
                       OR sensitive IS DISTINCT FROM @sensitive)
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("me", userId) |> ignore
        cmd.Parameters.AddWithValue("content", content) |> ignore

        cmd.Parameters.Add(
            NpgsqlParameter("summary", NpgsqlTypes.NpgsqlDbType.Text, Value = (summary |> Option.map box |> Option.defaultValue (box DBNull.Value)))
        )
        |> ignore

        cmd.Parameters.AddWithValue("sensitive", summary.IsSome) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) (t: Texts) =
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// The author check, before anything else (the form included): the comment id is well formed, the viewer is signed in,
/// and the comment is theirs and live. Everyone else, signed out included, gets the plain 404 page.
let private asAuthor (id: string) (next: Guid -> NpgsqlConnection -> Own -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            match WebAuth.currentUserId ctx with
            | Some userId when CommentRules.commentIdPattern.IsMatch id ->
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadOwn connection id userId with
                | Some own -> return! next userId connection own ctx
                | None -> return! notFound ctx t
            | _ -> return! notFound ctx t
        }

/// GET /comments/{id}/edit
let handler (id: string) : EndpointHandler =
    asAuthor id (fun _ _ own ctx ->
        task {
            let t = Texts.current ()

            return!
                ctx.WriteHtmlView(
                    view
                        ctx
                        t
                        own
                        { Text = own.Content
                          Spoiler = own.Sensitive
                          Error = None }
                )
        })

/// POST /comments/{id}/edit: `content`, `spoiler`. The author check, then the antiforgery check and the form, then
/// the rules shared with creating, then the update.
let postHandler (id: string) : EndpointHandler =
    asAuthor id (fun userId connection own ctx ->
        task {
            let t = Texts.current ()
            let! form = WebAuth.readValidatedForm ctx

            let typed =
                match form.TryGetValue "content" with
                | true, v -> string v
                | _ -> ""

            let spoiler = WebAuth.formValue form "spoiler" = "on"

            match CommentRules.validateContent typed with
            | Error key ->
                ctx.SetStatusCode 422

                return!
                    ctx.WriteHtmlView(
                        view
                            ctx
                            t
                            own
                            { Text = typed
                              Spoiler = spoiler
                              Error = Some key }
                    )
            | Ok content ->
                // A comment that stays a spoiler keeps its warning, so editing in another language changes nothing
                let summary =
                    if not spoiler then None
                    elif own.Sensitive && own.Summary.IsSome then own.Summary
                    else Some(CommentRules.spoilerSummary t own.SeasonDisplay own.SeasonNumber own.Number own.Label)

                do! update connection id userId content summary
                return! WebAuth.redirectTo ctx $"{episodePage own}#comment-{id}"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ routef "/comments/{%s}/edit" handler ]
      POST [ routef "/comments/{%s}/edit" postHandler |> configureEndpoint _.RequireRateLimiting("comment-write") ] ]
