module FrunchyBackend.Handlers.Episodes.Episode

open System
open System.Buffers.Text
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
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
// Model (specs/009-episode-comments/data-model.md)
// ---------------------------------------------------------------------------

/// The MyAnimeList entry the episode's season matched
type MalBlock =
    { MalId: int
      Title: string
      Url: string
      Score: decimal option
      /// Up to 3, by position
      Genres: string list }

type EpisodeModel =
    { Id: string
      SeriesId: string
      Number: int option
      Title: string option
      /// The special's own label ("OVA 1"), when the catalog has one
      Label: string option
      DurationMs: int
      Thumbnail: string option
      SeriesTitle: string
      Slug: string option
      SeasonId: string option
      SeasonTitle: string option
      SeasonNumber: int option
      /// The season's display number (text), for the spoiler warning
      SeasonDisplay: string option
      Audio: string option
      Dub: bool
      Mal: MalBlock option }

/// One comment of the thread
type Comment =
    { Id: string
      /// The episode it was written on (a dub's page also shows its original's comments)
      EpisodeId: string
      AuthorId: Guid
      Username: string
      DisplayName: string
      Content: string
      Summary: string option
      Sensitive: bool
      CreatedAt: DateTime
      EditedAt: DateTime option }

/// What the comment form shows: a freshly minted id, and after a refusal the typed text and the error
type FormState =
    { CommentId: string
      Text: string
      Spoiler: bool
      Error: string option }

// ---------------------------------------------------------------------------
// View (contracts/episode-page.md)
// ---------------------------------------------------------------------------

let private episodePath (seriesId: string) (episodeId: string) = $"/anime/{seriesId}/episode/{episodeId}"

let private isoUtc (instant: DateTime) =
    DateTime.SpecifyKind(instant, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

/// "Episode 5", the special's label, or "Special"
let private episodeLabel (t: Texts) (m: EpisodeModel) =
    match m.Number, m.Label with
    | Some n, _ -> t.EpisodeLabel n
    | None, Some label -> label
    | None, None -> t.SpecialLabel

let private seasonName (t: Texts) (m: EpisodeModel) =
    match m.SeasonId, m.SeasonTitle, m.SeasonNumber with
    | None, _, _ -> None
    | Some _, Some title, _ -> Some title
    | Some _, None, Some n -> Some(t.AnimeSeasonNumber n)
    | Some id, None, None -> Some id

let private thumbnail (url: string) =
    img(src = url, alt = "", class' = "aspect-video w-full max-w-xs shrink-0 rounded object-cover sm:w-64")
        .attr("loading", "lazy")
        .attr("style", "box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)")

let private malBlock (t: Texts) (mal: MalBlock) =
    div(class' = "space-y-2").data("mal", string mal.MalId) {
        div(class' = "flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1") {
            p(class' = "min-w-0 text-body-lg font-semibold text-on-surface [overflow-wrap:anywhere]") { mal.Title }

            a(href = mal.Url, target = "_blank", class' = "fr-link inline-flex min-h-6 items-center gap-1 text-label-md")
                .attr("rel", "noopener")
                .data("mal-link", "") {
                t.MyAnimeListLink
                icon Theme.OpenInNew
            }
        }

        if mal.Score.IsSome || not mal.Genres.IsEmpty then
            div(class' = "flex flex-wrap items-center gap-x-3 gap-y-1.5") {
                match mal.Score with
                | Some score ->
                    (pill TertiaryTone ("★ " + score.ToString("N2", t.Culture)))
                        .data("mal-score", score.ToString("0.00", CultureInfo.InvariantCulture))
                        .attr("role", "group")
                        .attr("aria-label", t.ScoreLabel)
                | None -> Fragment()

                if not mal.Genres.IsEmpty then
                    p(class' = "text-body-md text-on-surface-variant") { String.Join(" / ", mal.Genres) }
            }
    }

let private seasonLine (t: Texts) (m: EpisodeModel) =
    match seasonName t m with
    | Some name ->
        let el = div()

        let el =
            match m.Audio with
            | Some a -> el.data("audio", a)
            | None -> el

        (if m.Dub then el.data("dub", "") else el) { seasonNameLine t name m.Audio m.Dub }
    | None -> div()

let private header (t: Texts) (m: EpisodeModel) =
    let minutes = int (Math.Round(float m.DurationMs / 60000.0))
    let label = episodeLabel t m
    let heading = defaultArg m.Title label

    let numberText =
        match m.Number with
        | Some n -> string n
        | None -> ""

    div(class' = "space-y-6") {
        div(class' = "flex flex-col gap-6 sm:flex-row sm:items-end") {
            match m.Thumbnail with
            | Some url -> thumbnail url
            | None -> Fragment()

            div(class' = "min-w-0 space-y-3") {
                h1(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance [overflow-wrap:anywhere]")
                    .data("episode-title", defaultArg m.Title "") {
                    heading
                }

                p(class' = "text-body-lg text-on-surface-variant [overflow-wrap:anywhere]") { m.SeriesTitle }

                p(class' = "flex flex-wrap items-center gap-x-3 gap-y-1 text-label-md font-normal text-on-surface-variant") {
                    span().data("episode-number", numberText) { label }
                    span(class' = "tabular-nums").data("minutes", string minutes) { t.Minutes minutes }
                }

                seasonLine t m
            }
        }

        div(class' = "flex flex-wrap items-center gap-2") {
            a(href = $"https://www.crunchyroll.com/watch/{m.Id}", target = "_blank", class' = btn Primary)
                .attr("rel", "noopener")
                .data("cr-episode-link", "") {
                t.EpisodeWatchOnCrunchyroll
                icon Theme.OpenInNew
            }

            match m.Slug with
            | Some slug ->
                a(href = $"https://www.crunchyroll.com/series/{m.SeriesId}/{slug}", target = "_blank", class' = btn Secondary)
                    .attr("rel", "noopener")
                    .data("cr-series-link", "") {
                    t.EpisodeSeriesOnCrunchyroll
                    icon Theme.OpenInNew
                }
            | None -> Fragment()
        }

        match m.Mal with
        | Some mal -> malBlock t mal
        | None -> Fragment()
    }

/// The author's controls: edit (a link to the edit page) and delete (a POST). No script, no confirm dialog.
let private authorControls (ctx: HttpContext) (t: Texts) (c: Comment) =
    div(class' = "flex flex-wrap items-center gap-2") {
        a(href = $"/comments/{c.Id}/edit", class' = btnSm Ghost).data("comment-edit", "") { t.CommentEditTitle }

        form(method = "post", action = $"/comments/{c.Id}/delete").data("comment-delete", "") {
            ctx.GetAntiforgeryInput()
            button(type' = "submit", class' = btnSm Ghost) { t.CommentDelete }
        }
    }

/// One comment. `viewer` is the signed-in user: the author gets edit and delete here.
let private commentView (ctx: HttpContext) (t: Texts) (viewer: Guid option) (c: Comment) =
    let isAuthor = (viewer = Some c.AuthorId)

    let body =
        p(class' = "whitespace-pre-line text-body-md text-on-surface [overflow-wrap:anywhere]") { c.Content }

    li(id = $"comment-{c.Id}", class' = "space-y-2 py-4 first:pt-0 last:pb-0")
        .data("comment", c.Id)
        .data("author", c.Username)
        .data("episode", c.EpisodeId)
        .data("created", isoUtc c.CreatedAt) {
        div(class' = "flex flex-wrap items-baseline gap-x-3 gap-y-0.5") {
            a(href = $"/u/{c.Username}", class' = "text-body-md font-semibold text-on-surface [overflow-wrap:anywhere] hover:underline") {
                c.DisplayName
            }

            span(class' = "text-label-md font-normal text-on-surface-variant") { Layout.timeElement t "relative" c.CreatedAt }

            if c.EditedAt.IsSome then
                span(class' = "text-label-md font-normal text-on-surface-variant").data("edited", "") { t.CommentEdited }
        }

        if c.Sensitive then
            details(class' = "rounded border border-outline-variant bg-surface-container px-4 py-3").data("spoiler", "") {
                summary(class' = "flex cursor-pointer flex-wrap items-center justify-between gap-x-4 gap-y-1 text-label-md text-on-surface-variant")
                    .data("spoiler-summary", "") {
                    span() { defaultArg c.Summary "" }
                    span(class' = "text-primary") { t.CommentShowSpoiler }
                }

                div(class' = "pt-3") { body }
            }
        else
            body

        if isAuthor then
            authorControls ctx t c
    }

let private commentForm (ctx: HttpContext) (t: Texts) (m: EpisodeModel) (f: FormState) =
    form(method = "post", action = $"{episodePath m.SeriesId m.Id}/comments", class' = "space-y-4").data("comment-form", "") {
        ctx.GetAntiforgeryInput()
        input(type' = "hidden", name = "id", value = f.CommentId)

        div() {
            label(for' = "content", class' = "fr-label") { t.CommentFormLabel }

            let area =
                textarea(id = "content", name = "content", class' = "fr-input h-auto min-h-28 py-2.5")
                    .attr("required", "")
                    .attr("maxlength", "2000")
                    .attr("rows", "4")

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

        div(class' = "flex flex-wrap items-center justify-between gap-x-6 gap-y-3") {
            label(class' = "inline-flex min-h-6 items-center gap-2 text-body-md text-on-surface") {
                let box =
                    input(type' = "checkbox", name = "spoiler", class' = "h-4 w-4 accent-primary").data("spoiler-toggle", "")

                if f.Spoiler then box.attr("checked", "") else box
                span() { t.CommentSpoilerLabel }
            }

            button(type' = "submit", class' = btn Primary) { t.CommentSubmit }
        }
    }

let private signInPrompt (t: Texts) (m: EpisodeModel) =
    a(href = $"/signin?returnUrl={Uri.EscapeDataString(episodePath m.SeriesId m.Id)}", class' = "fr-link text-body-md")
        .data("signin-prompt", "") {
        t.CommentSignInPrompt
    }

let private thread (ctx: HttpContext) (t: Texts) (viewer: Guid option) (comments: Comment list) =
    if comments.IsEmpty then
        (p(class' = "text-body-md text-on-surface-variant").data("thread-empty", "") { t.CommentsEmpty }) :> HtmlElement
    else
        (ol(class' = "divide-y divide-outline-variant/40") {
            for c in comments do
                commentView ctx t viewer c
        })
        :> HtmlElement

let private view (ctx: HttpContext) (t: Texts) (viewer: Guid option) (m: EpisodeModel) (comments: Comment list) (f: FormState) =
    div(class' = "space-y-10").data("episode-page", m.Id).data("series", m.SeriesId) {
        header t m

        section(class' = "space-y-6").data("thread", string comments.Length) {
            sectionTitle t.CommentsHeading

            match viewer with
            | Some _ -> commentForm ctx t m f
            | None -> signInPrompt t m

            thread ctx t viewer comments
        }
    }
    |> Layout.page ctx t (t.EpisodePageTitle m.SeriesTitle (episodeLabel t m))

// ---------------------------------------------------------------------------
// Data access: one statement for the episode, one for the thread
// ---------------------------------------------------------------------------

let private loadEpisode (connection: NpgsqlConnection) (seriesId: string) (episodeId: string) : Task<EpisodeModel option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT e.id, e.series_id, e.episode_number, e.title, e.episode_label, e.duration_ms, e.thumbnail_url,
                       coalesce(s.title, e.series_title, e.series_id), s.slug,
                       e.season_id, sea.title, sea.season_number, sea.display_number, sea.audio_locale,
                       m.method = 'dub',
                       a.mal_id, a.title, a.url, a.score,
                       array(SELECT t.name FROM mal_anime_tags at
                             JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                             WHERE at.mal_id = a.mal_id AND at.kind = 'genre'
                             ORDER BY at.position, t.name LIMIT 3)
                FROM cr_episodes e
                LEFT JOIN cr_series s ON s.id = e.series_id
                LEFT JOIN cr_seasons sea ON sea.id = e.season_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                LEFT JOIN mal_anime a ON a.mal_id = m.mal_id
                WHERE e.id = @episode_id AND e.series_id = @series_id AND e.duration_ms IS NOT NULL
                """,
                connection
            )

        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore
        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
        let int' (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)

        if r.Read() then
            return
                Some
                    { Id = r.GetString 0
                      SeriesId = r.GetString 1
                      Number = int' 2
                      Title = text 3
                      Label = text 4
                      DurationMs = r.GetInt32 5
                      Thumbnail = text 6
                      SeriesTitle = r.GetString 7
                      Slug = text 8
                      SeasonId = text 9
                      SeasonTitle = text 10
                      SeasonNumber = int' 11
                      SeasonDisplay = text 12
                      Audio = text 13
                      Dub = (not (r.IsDBNull 14) && r.GetBoolean 14)
                      Mal =
                        if r.IsDBNull 15 then
                            None
                        else
                            Some
                                { MalId = r.GetInt32 15
                                  Title = r.GetString 16
                                  Url = r.GetString 17
                                  Score = (if r.IsDBNull 18 then None else Some(r.GetDecimal 18))
                                  Genres = r.GetFieldValue<string[]> 19 |> List.ofArray } }
        else
            return None
    }

/// The page episode's group (research R2): its own id, plus, when it has a season and a number, the episodes with
/// that number in its original season or in a season whose matched row shares that original. One statement.
let private loadGroup (connection: NpgsqlConnection) (episodeId: string) : Task<string list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH origin AS (
                    SELECT e.episode_number, coalesce(m.shared_from, e.season_id) AS season_id
                    FROM cr_episodes e
                    LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    WHERE e.id = @episode_id AND e.season_id IS NOT NULL AND e.episode_number IS NOT NULL
                )
                SELECT @episode_id
                UNION
                SELECT x.id
                FROM origin o
                JOIN cr_episodes x ON x.episode_number = o.episode_number AND x.duration_ms IS NOT NULL
                LEFT JOIN cr_season_matches xm ON xm.season_id = x.season_id AND xm.status = 'matched'
                WHERE x.season_id = o.season_id OR xm.shared_from = o.season_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let ids = ResizeArray<string>()

        while r.Read() do
            ids.Add(r.GetString 0)

        return List.ofSeq ids
    }

/// The comments shown under the given episodes (the page's group), oldest first
let private loadThread (connection: NpgsqlConnection) (episodeIds: string list) : Task<Comment list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT c.id, c.cr_episode_id, c.author_user_id, u.username, u.display_name,
                       c.content, c.summary, c.sensitive, c.created_at, c.edited_at
                FROM comments c
                JOIN users u ON u.id = c.author_user_id
                WHERE c.cr_episode_id = ANY(@episode_ids)
                  AND c.deleted_at IS NULL AND c.audience IN ('public', 'unlisted')
                ORDER BY c.created_at, c.id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("episode_ids", Array.ofList episodeIds) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while r.Read() do
            rows.Add
                { Id = r.GetString 0
                  EpisodeId = r.GetString 1
                  AuthorId = r.GetGuid 2
                  Username = r.GetString 3
                  DisplayName = r.GetString 4
                  Content = r.GetString 5
                  Summary = (if r.IsDBNull 6 then None else Some(r.GetString 6))
                  Sensitive = r.GetBoolean 7
                  CreatedAt = r.GetDateTime 8
                  EditedAt = (if r.IsDBNull 9 then None else Some(r.GetDateTime 9)) }

        return List.ofSeq rows
    }

/// Insert a comment; false when the id already exists (nothing is written then)
let private insertComment
    (connection: NpgsqlConnection)
    (id: string)
    (episodeId: string)
    (authorId: Guid)
    (content: string)
    (summary: string option)
    : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO comments (id, cr_episode_id, author_user_id, content, summary, sensitive)
                VALUES (@id, @episode_id, @author_id, @content, @summary, @sensitive)
                ON CONFLICT (id) DO NOTHING
                RETURNING id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore
        cmd.Parameters.AddWithValue("author_id", authorId) |> ignore
        cmd.Parameters.AddWithValue("content", content) |> ignore

        cmd.Parameters.AddWithValue(
            "summary",
            (match summary with
             | Some s -> box s
             | None -> box DBNull.Value)
        )
        |> ignore

        cmd.Parameters.AddWithValue("sensitive", summary.IsSome) |> ignore
        let! inserted = cmd.ExecuteScalarAsync()
        return not (isNull inserted)
    }

/// Is the comment this user's, on this episode (a double submit of the same form)
let private isOwnOnEpisode (connection: NpgsqlConnection) (id: string) (authorId: Guid) (episodeId: string) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                "SELECT count(*) FROM comments WHERE id = @id AND author_user_id = @author_id AND cr_episode_id = @episode_id",
                connection
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("author_id", authorId) |> ignore
        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore
        let! count = cmd.ExecuteScalarAsync()
        return unbox<int64> count > 0L
    }

// ---------------------------------------------------------------------------
// Rules (research R3, R7, R9)
// ---------------------------------------------------------------------------

/// 16 random bytes as 22 characters of base64url
let private mintId () =
    Base64Url.EncodeToString(RandomNumberGenerator.GetBytes 16)

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

let private notFound (ctx: HttpContext) (t: Texts) =
    ctx.SetStatusCode 404
    ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)

let private freshForm (spoiler: bool) (text: string) (error: string option) =
    { CommentId = mintId ()
      Text = text
      Spoiler = spoiler
      Error = error }

/// GET /anime/{seriesId}/episode/{episodeId}
let handler (seriesId: string) (episodeId: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()

            if not (CommentRules.crunchyrollIdPattern.IsMatch seriesId && CommentRules.crunchyrollIdPattern.IsMatch episodeId) then
                return! notFound ctx t
            else
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadEpisode connection seriesId episodeId with
                | None -> return! notFound ctx t
                | Some model ->
                    let! group = loadGroup connection model.Id
                    let! comments = loadThread connection group
                    return! ctx.WriteHtmlView(view ctx t (WebAuth.currentUserId ctx) model comments (freshForm true "" None))
        }

/// POST /anime/{seriesId}/episode/{episodeId}/comments: `id`, `content`, `spoiler`. Signed in (else the
/// sign-in page and back to the episode), then the antiforgery check and the form, then the rules, then the insert.
let createHandler (seriesId: string) (episodeId: string) : EndpointHandler =
    WebAuth.requireSignedInReturning (episodePath seriesId episodeId) (fun userId ctx ->
        task {
            let t = Texts.current ()
            let! form = WebAuth.readValidatedForm ctx

            if not (CommentRules.crunchyrollIdPattern.IsMatch seriesId && CommentRules.crunchyrollIdPattern.IsMatch episodeId) then
                return! notFound ctx t
            else
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                match! loadEpisode connection seriesId episodeId with
                | None -> return! notFound ctx t
                | Some model ->
                    let typed =
                        match form.TryGetValue "content" with
                        | true, v -> string v
                        | _ -> ""

                    let spoiler = WebAuth.formValue form "spoiler" = "on"
                    let id = WebAuth.formValue form "id"

                    let refuse (key: string) =
                        task {
                            let! group = loadGroup connection model.Id
                            let! comments = loadThread connection group
                            ctx.SetStatusCode 422
                            return! ctx.WriteHtmlView(view ctx t (Some userId) model comments (freshForm spoiler typed (Some key)))
                        }

                    if not (CommentRules.commentIdPattern.IsMatch id) then
                        return! refuse "bad-id"
                    else
                        match CommentRules.validateContent typed with
                        | Error key -> return! refuse key
                        | Ok content ->
                            let summary =
                                if spoiler then
                                    Some(CommentRules.spoilerSummary t model.SeasonDisplay model.SeasonNumber model.Number model.Label)
                                else
                                    None

                            let! inserted = insertComment connection id model.Id userId content summary
                            let! mine = if inserted then Task.FromResult true else isOwnOnEpisode connection id userId model.Id

                            if mine then
                                return! WebAuth.redirectTo ctx $"{episodePath seriesId episodeId}#comment-{id}"
                            else
                                return! refuse "bad-id"
        })

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ routef "/anime/{%s}/episode/{%s}" handler ]
      POST [ routef "/anime/{%s}/episode/{%s}/comments" createHandler |> configureEndpoint _.RequireRateLimiting("comment-write") ] ]
