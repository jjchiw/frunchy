/// Feature 015 (research R5): the fediverse outbox worker. Comments and account changes queue a row in
/// `fediverse_outbox` in their own transaction; this worker sends each one to Megáfono's internal API, reading the
/// subject's current state when it runs (the outbox holds no payloads), so an edit or delete queued before the
/// publish needs no special case. Items of one subject run in order; failures back off and retry. Postgres is the queue.
module FrunchyBackend.Workers.FediverseOutbox

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MegafonoClient
open FrunchyBackend.Views

/// A claim older than this is taken as a crashed worker's and retried (as the import processor does)
let staleClaimAfter = TimeSpan.FromMinutes 10.0

/// How long to wait after the n-th failure of an item: one minute, doubling, at most an hour
let backoffAfter (attempts: int) : TimeSpan =
    TimeSpan.FromMinutes(Math.Min(60.0, Math.Pow(2.0, float (Math.Clamp(attempts - 1, 0, 12)))))

type private Item =
    { Id: int64
      Kind: string
      Subject: string
      Attempts: int }

/// What running one item came to
type private Outcome =
    /// Nothing more to do (an optional note is kept in `last_error`)
    | Finished of note: string option
    /// Megáfono refused it for good: done, with the reason kept and a warning logged
    | Refused of reason: string
    /// Try again later (`attempts + 1`, backoff)
    | Retry of reason: string

/// Network failures, 5xx, an idempotency conflict, a wrong token (an operator fixes that) and throttling are
/// transient; every other refusal is permanent
let private isTransient (status: int) =
    status = 0 || status >= 500 || status = 401 || status = 408 || status = 409 || status = 429

/// 2xx is done; `alsoDone` lists the statuses that mean nothing is left to do; transient failures retry; the rest
/// is refused. The note holds only the status and Megáfono's error code, never a body or the token.
let private classify (alsoDone: int list) (result: MegafonoResult) : Outcome =
    if result.IsSuccess then Finished None
    elif List.contains result.Status alsoDone then Finished(Some result.Describe)
    elif isTransient result.Status then Retry result.Describe
    else Refused result.Describe

let private param (cmd: NpgsqlCommand) (name: string) (value: obj) =
    cmd.Parameters.AddWithValue(name, value) |> ignore

let private optional (value: string option) : obj =
    match value with
    | Some v -> box v
    | None -> box DBNull.Value

let private text (reader: System.Data.Common.DbDataReader) (i: int) =
    if reader.IsDBNull i then None else Some(reader.GetString i)

// ---------------------------------------------------------------------------
// Claiming and finishing
// ---------------------------------------------------------------------------

/// The oldest item that is due, has no older pending item for its subject (per-subject order), and isn't held by a
/// live claim. `FOR UPDATE SKIP LOCKED` keeps two workers off the same row; the claim is stamped by the same statement.
let private claimNext (db: DbConfig) (now: DateTimeOffset) : Task<Item option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use claim =
            new NpgsqlCommand(
                """
                WITH next AS (
                    SELECT o.id
                    FROM fediverse_outbox o
                    WHERE o.state = 'pending'
                      AND o.next_attempt_at <= @now
                      AND (o.claimed_at IS NULL OR o.claimed_at < @now - @stale)
                      AND NOT EXISTS (SELECT 1 FROM fediverse_outbox older
                                      WHERE older.subject = o.subject AND older.state = 'pending' AND older.id < o.id)
                    ORDER BY o.id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE fediverse_outbox o SET claimed_at = @now
                FROM next
                WHERE o.id = next.id
                RETURNING o.id, o.kind, o.subject, o.attempts
                """,
                connection
            )

        param claim "now" now
        param claim "stale" staleClaimAfter
        use! reader = claim.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { Id = reader.GetInt64 0
                      Kind = reader.GetString 1
                      Subject = reader.GetString 2
                      Attempts = reader.GetInt32 3 }
        else
            return None
    }

let private finish (db: DbConfig) (now: DateTimeOffset) (item: Item) (outcome: Outcome) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        let doneWith (note: string option) =
            task {
                use cmd =
                    new NpgsqlCommand(
                        "UPDATE fediverse_outbox SET state = 'done', done_at = @now, claimed_at = NULL, last_error = @error WHERE id = @id",
                        connection
                    )

                param cmd "now" now
                param cmd "error" (optional note)
                param cmd "id" item.Id
                let! _ = cmd.ExecuteNonQueryAsync()
                ()
            }

        match outcome with
        | Finished note -> do! doneWith note
        | Refused reason -> do! doneWith (Some reason)
        | Retry reason ->
            use cmd =
                new NpgsqlCommand(
                    """
                    UPDATE fediverse_outbox
                    SET attempts = attempts + 1, next_attempt_at = @next, claimed_at = NULL, last_error = @error
                    WHERE id = @id
                    """,
                    connection
                )

            param cmd "next" (now + backoffAfter (item.Attempts + 1))
            param cmd "error" reason
            param cmd "id" item.Id
            let! _ = cmd.ExecuteNonQueryAsync()
            ()
    }

// ---------------------------------------------------------------------------
// One kind each (contracts/frunchy-megafono.md)
// ---------------------------------------------------------------------------

let private episodeUrl (domain: string) (seriesId: string) (episodeId: string) =
    $"https://{domain}/anime/{seriesId}/episode/{episodeId}"

/// "Frieren: Episode 5 The Journey", in English: the thread is a post on the fediverse, not a page in the viewer's language
let private threadText (seriesTitle: string) (number: int option) (label: string option) (title: string option) =
    let episode =
        match number, label with
        | Some n, _ -> Texts.en.EpisodeLabel n
        | None, Some l -> l
        | None, None -> Texts.en.SpecialLabel

    let name = defaultArg title ""
    $"{seriesTitle}: {episode} {name}".TrimEnd()

/// What `ensure_thread` needs to know about a thread that isn't published yet
type private ThreadRow =
    { SeriesId: string
      EpisodeId: string
      SeriesTitle: string
      Number: int option
      Label: string option
      Title: string option }

/// The thread row, when it exists and has no object id yet
let private loadUnpublishedThread (connection: NpgsqlConnection) (groupKey: string) : Task<ThreadRow option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT t.series_id, t.cr_episode_id, coalesce(s.title, e.series_title, e.series_id),
                       e.episode_number, e.episode_label, e.title
                FROM episode_threads t
                JOIN cr_episodes e ON e.id = t.cr_episode_id
                LEFT JOIN cr_series s ON s.id = t.series_id
                WHERE t.group_key = @group_key AND t.object_id IS NULL
                """,
                connection
            )

        param cmd "group_key" groupKey
        use! reader = cmd.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { SeriesId = reader.GetString 0
                      EpisodeId = reader.GetString 1
                      SeriesTitle = reader.GetString 2
                      Number = (if reader.IsDBNull 3 then None else Some(reader.GetInt32 3))
                      Label = text reader 4
                      Title = text reader 5 }
        else
            return None
    }

/// `ensure_thread`: publish the group's thread from the series actor, once
let private ensureThread (connection: NpgsqlConnection) (client: HttpClient) (domain: string) (groupKey: string) : Task<Outcome> =
    task {
        match! loadUnpublishedThread connection groupKey with
        | None -> return Finished None // no such thread, or already published
        | Some thread ->
            // The lookup has to know the series' handle before Megáfono asks for the actor
            let! _ = Fediverse.ensureSeriesHandle connection thread.SeriesId

            let note =
                { Audience = "public"
                  Content = Fediverse.commentHtml (threadText thread.SeriesTitle thread.Number thread.Label thread.Title)
                  Url = episodeUrl domain thread.SeriesId thread.EpisodeId
                  InReplyTo = None
                  Summary = None
                  Sensitive = false }

            let! result = publish client $"series-{thread.SeriesId}" $"thread:{thread.SeriesId}:{groupKey}" note

            match publishedOf result with
            | Some(objectId, objectUrl) ->
                use store =
                    new NpgsqlCommand(
                        "UPDATE episode_threads SET object_id = @object_id, object_url = @object_url WHERE group_key = @group_key AND object_id IS NULL",
                        connection
                    )

                param store "object_id" objectId
                param store "object_url" objectUrl
                param store "group_key" groupKey
                let! _ = store.ExecuteNonQueryAsync()
                return Finished None
            | None when result.IsSuccess -> return Retry "unreadable answer"
            | None -> return classify [] result
    }

/// A comment as the worker reads it when it runs
type private CommentRow =
    { AuthorId: Guid option
      Username: string option
      Content: string
      Summary: string option
      Sensitive: bool
      Deleted: bool
      ObjectId: string option
      EpisodeId: string
      SeriesId: string }

let private loadComment (connection: NpgsqlConnection) (id: string) : Task<CommentRow option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT c.author_user_id, u.username, c.content, c.summary, c.sensitive, c.deleted_at IS NOT NULL,
                       c.object_id, c.cr_episode_id, e.series_id
                FROM comments c
                JOIN cr_episodes e ON e.id = c.cr_episode_id
                LEFT JOIN users u ON u.id = c.author_user_id
                WHERE c.id = @id
                """,
                connection
            )

        param cmd "id" id
        use! reader = cmd.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { AuthorId = (if reader.IsDBNull 0 then None else Some(reader.GetGuid 0))
                      Username = text reader 1
                      Content = reader.GetString 2
                      Summary = text reader 3
                      Sensitive = reader.GetBoolean 4
                      Deleted = reader.GetBoolean 5
                      ObjectId = text reader 6
                      EpisodeId = reader.GetString 7
                      SeriesId = reader.GetString 8 }
        else
            return None
    }

let private commentUrl (domain: string) (id: string) (c: CommentRow) =
    $"{episodeUrl domain c.SeriesId c.EpisodeId}#comment-{id}"

/// `publish_comment`: the reply, from the author's actor, in its group's thread
let private publishComment (connection: NpgsqlConnection) (client: HttpClient) (domain: string) (id: string) : Task<Outcome> =
    task {
        match! loadComment connection id with
        | None -> return Finished None
        | Some c when c.Deleted || c.ObjectId.IsSome -> return Finished None
        | Some c ->
            match c.AuthorId, c.Username with
            | Some authorId, Some username when Fediverse.isValidHandle username ->
                let! groupKey = Fediverse.episodeGroupKey connection c.EpisodeId

                use thread = new NpgsqlCommand("SELECT object_url FROM episode_threads WHERE group_key = @group_key", connection)
                param thread "group_key" groupKey

                match! thread.ExecuteScalarAsync() with
                | :? string as threadUrl ->
                    let note =
                        { Audience = "public"
                          Content = Fediverse.commentHtml c.Content
                          Url = commentUrl domain id c
                          InReplyTo = Some threadUrl
                          Summary = c.Summary
                          Sensitive = c.Sensitive }

                    let! result = publish client (string authorId) $"comment:{id}" note

                    match publishedOf result with
                    | Some(objectId, _) ->
                        use store = new NpgsqlCommand("UPDATE comments SET object_id = @object_id WHERE id = @id AND object_id IS NULL", connection)
                        param store "object_id" objectId
                        param store "id" id
                        let! _ = store.ExecuteNonQueryAsync()
                        return Finished None
                    | None when result.IsSuccess -> return Retry "unreadable answer"
                    | None -> return classify [] result
                | _ -> return Retry "waiting for thread"
            | _ -> return Finished(Some "author can't federate")
    }

/// `update_comment`: the stored object takes the comment's current text
let private updateComment (connection: NpgsqlConnection) (client: HttpClient) (domain: string) (id: string) : Task<Outcome> =
    task {
        match! loadComment connection id with
        | Some c when not c.Deleted && c.ObjectId.IsSome ->
            let! result =
                updateObject
                    client
                    c.ObjectId.Value
                    { Content = Fediverse.commentHtml c.Content
                      Url = commentUrl domain id c
                      Summary = c.Summary
                      Sensitive = c.Sensitive }

            return classify [] result
        | _ -> return Finished None // not published yet (the pending publish sends the latest text), deleted, or gone
    }

/// `delete_comment`: delete the stored object, when there is one
let private deleteComment (connection: NpgsqlConnection) (client: HttpClient) (id: string) : Task<Outcome> =
    task {
        match! loadComment connection id with
        | Some { ObjectId = Some objectId } ->
            let! result = deleteObject client objectId
            return classify [ 404; 410 ] result
        | _ -> return Finished None
    }

let private run (db: DbConfig) (client: HttpClient) (domain: string) (item: Item) : Task<Outcome> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        match item.Kind with
        | "ensure_thread" -> return! ensureThread connection client domain item.Subject
        | "publish_comment" -> return! publishComment connection client domain item.Subject
        | "update_comment" -> return! updateComment connection client domain item.Subject
        | "delete_comment" -> return! deleteComment connection client item.Subject
        | "delete_actor" ->
            let! result = deleteActor client item.Subject
            return classify [ 404; 410 ] result
        | "refresh_profile" ->
            let! result = refreshProfile client item.Subject
            return classify [ 404; 410 ] result
        | other -> return Refused $"unknown kind {other}"
    }

// ---------------------------------------------------------------------------
// The worker
// ---------------------------------------------------------------------------

/// Process the next due item, if any, at `now`. Returns true when one was processed (done, refused or rescheduled).
let processNextWith (logger: ILogger) (db: DbConfig) (client: HttpClient) (values: Federation.FederationValues) (now: DateTimeOffset) : Task<bool> =
    task {
        match! claimNext db now with
        | None -> return false
        | Some item ->
            let! outcome =
                task {
                    try
                        return! run db client values.Domain item
                    with ex ->
                        // The exception goes to the log; only its type is kept on the row
                        logger.LogError(ex, "Fediverse outbox item {Id} ({Kind}) failed", item.Id, item.Kind)
                        return Retry(ex.GetType().Name)
                }

            match outcome with
            | Refused reason -> logger.LogWarning("Megafono refused outbox item {Id} ({Kind}): {Reason}", item.Id, item.Kind, reason)
            | Retry reason -> logger.LogInformation("Outbox item {Id} ({Kind}) will be retried: {Reason}", item.Id, item.Kind, reason)
            | Finished _ -> ()

            do! finish db now item outcome
            return true
    }

/// `processNextWith` without a logger (the tests drive the worker through this)
let processNext (db: DbConfig) (client: HttpClient) (values: Federation.FederationValues) (now: DateTimeOffset) : Task<bool> =
    processNextWith NullLogger.Instance db client values now

/// Polls the outbox: drains while there is work, sleeps 5 seconds when idle. Program registers it only with federation on.
type FediverseOutboxService
    (db: DbConfig, factory: IHttpClientFactory, settings: Federation.FederationSettings, time: TimeProvider, logger: ILogger<FediverseOutboxService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            match settings with
            | Federation.Disabled -> ()
            | Federation.Enabled values ->
                while not stoppingToken.IsCancellationRequested do
                    try
                        let client = factory.CreateClient MegafonoClient.ClientName
                        let! worked = processNextWith logger db client values (time.GetUtcNow())

                        if not worked then
                            do! Task.Delay(TimeSpan.FromSeconds 5.0, stoppingToken)
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        logger.LogError(ex, "Fediverse outbox worker error; retrying shortly")

                        try
                            do! Task.Delay(TimeSpan.FromSeconds 5.0, stoppingToken)
                        with :? OperationCanceledException ->
                            ()
        }
