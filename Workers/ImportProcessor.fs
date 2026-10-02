/// Background normalization of history submissions. Postgres is the job queue (cr_imports.status).
/// Rules: specs/001-crunchyroll-history-import/research.md R6–R9 and data-model.md
module FrunchyBackend.Workers.ImportProcessor

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Domains.Tracking
open FrunchyBackend.Infrastructure.CrunchyrollPage
open FrunchyBackend.Infrastructure.Database

/// A claim older than this is assumed to belong to a crashed worker and is retried
let staleClaimAfter = TimeSpan.FromMinutes 10.0

let private parameter (dbType: NpgsqlDbType) (value: obj) =
    NpgsqlParameter(NpgsqlDbType = dbType, Value = (if isNull value then box DBNull.Value else value))

let private text (v: string option) =
    parameter NpgsqlDbType.Text (Option.toObj v)

let private int' (v: int option) =
    parameter NpgsqlDbType.Integer (v |> Option.map box |> Option.toObj)

let private command (sql: string) (parameters: NpgsqlParameter list) =
    let cmd = NpgsqlBatchCommand sql
    parameters |> List.iter (cmd.Parameters.Add >> ignore)
    cmd

// Catalog upserts keep the freshest data: only overwritten by data with source_at >= the stored one (R7)
let private upsertSeries =
    """INSERT INTO cr_series (id, title, slug, source_at) VALUES ($1, $2, $3, $4)
       ON CONFLICT (id) DO UPDATE SET title = excluded.title, slug = excluded.slug, source_at = excluded.source_at
       WHERE excluded.source_at >= cr_series.source_at"""

let private upsertSeason =
    """INSERT INTO cr_seasons (id, series_id, season_number, display_number, title, audio_locale, source_at)
       VALUES ($1, $2, $3, $4, $5, $6, $7)
       ON CONFLICT (id) DO UPDATE SET series_id = excluded.series_id, season_number = excluded.season_number,
           display_number = excluded.display_number, title = excluded.title, audio_locale = excluded.audio_locale,
           source_at = excluded.source_at
       WHERE excluded.source_at >= cr_seasons.source_at"""

let private upsertEpisode =
    """INSERT INTO cr_episodes (id, series_id, season_id, series_title, title, episode_number, episode_label,
                                duration_ms, thumbnail_url, source_at)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
       ON CONFLICT (id) DO UPDATE SET series_id = excluded.series_id, season_id = excluded.season_id,
           series_title = excluded.series_title, title = excluded.title, episode_number = excluded.episode_number,
           episode_label = excluded.episode_label, duration_ms = excluded.duration_ms,
           thumbnail_url = excluded.thumbnail_url, source_at = excluded.source_at
       WHERE cr_episodes.source_at IS NULL OR excluded.source_at >= cr_episodes.source_at"""

/// Unresolved entry: make sure the episode exists without touching details another entry provided (R8)
let private ensureEpisodeStub =
    "INSERT INTO cr_episodes (id, series_id) VALUES ($1, $2) ON CONFLICT (id) DO NOTHING"

/// One row per user and episode; replaced only by a strictly newer watch state (R7)
let private upsertWatch =
    """INSERT INTO watch_events (user_id, cr_episode_id, watched_at, playhead_s, fully_watched, import_id, updated_at)
       VALUES ($1, $2, $3, $4, $5, $6, now())
       ON CONFLICT (user_id, cr_episode_id) DO UPDATE SET watched_at = excluded.watched_at,
           playhead_s = excluded.playhead_s, fully_watched = excluded.fully_watched,
           import_id = excluded.import_id, updated_at = now()
       WHERE (excluded.watched_at, excluded.fully_watched, excluded.playhead_s)
           > (watch_events.watched_at, watch_events.fully_watched, watch_events.playhead_s)"""

let private insertIssue =
    """INSERT INTO cr_import_entry_issues (import_id, page_index, entry_index, cr_episode_id, outcome, reason)
       VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING"""

let private watchCommand (userId: Guid) (importId: Guid) episodeId (watchedAt: DateTimeOffset) (playhead: int) (fully: bool) =
    command
        upsertWatch
        [ parameter NpgsqlDbType.Uuid userId
          parameter NpgsqlDbType.Text episodeId
          parameter NpgsqlDbType.TimestampTz watchedAt.UtcDateTime
          parameter NpgsqlDbType.Integer playhead
          parameter NpgsqlDbType.Boolean fully
          parameter NpgsqlDbType.Uuid importId ]

let private issueCommand (importId: Guid) (page: int) (index: int) (episodeId: string option) (outcome: EntryOutcome) reason =
    command
        insertIssue
        [ parameter NpgsqlDbType.Uuid importId
          parameter NpgsqlDbType.Integer page
          parameter NpgsqlDbType.Integer index
          text episodeId
          parameter NpgsqlDbType.Text (EntryOutcome.toDb outcome)
          parameter NpgsqlDbType.Text reason ]

/// Commands for one parsed entry, plus its outcome
let private entryCommands userId importId (sourceAt: DateTime) page index (entry: ParsedEntry) =
    match entry with
    | Episode e ->
        let source = parameter NpgsqlDbType.TimestampTz sourceAt

        [ if e.ParentSeriesTitle.IsSome then
              command upsertSeries [ parameter NpgsqlDbType.Text e.SeriesId; text e.ParentSeriesTitle; text e.SeriesSlug; source ]
          match e.SeasonId with
          | Some seasonId ->
              command
                  upsertSeason
                  [ parameter NpgsqlDbType.Text seasonId
                    parameter NpgsqlDbType.Text e.SeriesId
                    int' e.SeasonNumber
                    text e.SeasonDisplayNumber
                    text e.SeasonTitle
                    text e.AudioLocale
                    parameter NpgsqlDbType.TimestampTz sourceAt ]
          | None -> ()
          command
              upsertEpisode
              [ parameter NpgsqlDbType.Text e.EpisodeId
                parameter NpgsqlDbType.Text e.SeriesId
                text e.SeasonId
                text e.SeriesTitle
                text e.Title
                int' e.EpisodeNumber
                text e.EpisodeLabel
                parameter NpgsqlDbType.Integer e.DurationMs
                text e.ThumbnailUrl
                parameter NpgsqlDbType.TimestampTz sourceAt ]
          watchCommand userId importId e.EpisodeId e.WatchedAt e.PlayheadS e.FullyWatched ],
        EntryOutcome.Processed
    | Unresolved u ->
        [ command ensureEpisodeStub [ parameter NpgsqlDbType.Text u.EpisodeId; parameter NpgsqlDbType.Text u.SeriesId ]
          watchCommand userId importId u.EpisodeId u.WatchedAt u.PlayheadS u.FullyWatched
          issueCommand importId page index (Some u.EpisodeId) EntryOutcome.Unresolved unresolvedReason ],
        EntryOutcome.Unresolved
    | Unsupported(id, reason) -> [ issueCommand importId page index id EntryOutcome.Unsupported reason ], EntryOutcome.Unsupported
    | Failed(id, reason) -> [ issueCommand importId page index id EntryOutcome.Failed reason ], EntryOutcome.Failed

/// Reset stale claims, then claim the oldest pending import (FOR UPDATE SKIP LOCKED) and mark it processing
let private claimNext (db: DbConfig) : Task<Guid option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use recover =
            new NpgsqlCommand(
                "UPDATE cr_imports SET status = 'pending', processing_started_at = NULL WHERE status = 'processing' AND processing_started_at < now() - @stale",
                connection,
                tx
            )

        recover.Parameters.AddWithValue("stale", staleClaimAfter) |> ignore
        let! _ = recover.ExecuteNonQueryAsync()

        use claim =
            new NpgsqlCommand(
                "SELECT id FROM cr_imports WHERE status = 'pending' ORDER BY received_at, id LIMIT 1 FOR UPDATE SKIP LOCKED",
                connection,
                tx
            )

        let! claimed = claim.ExecuteScalarAsync()

        match claimed with
        | :? Guid as importId ->
            use mark =
                new NpgsqlCommand(
                    "UPDATE cr_imports SET status = 'processing', processing_started_at = now() WHERE id = @id",
                    connection,
                    tx
                )

            mark.Parameters.AddWithValue("id", importId) |> ignore
            let! _ = mark.ExecuteNonQueryAsync()
            do! tx.CommitAsync()
            return Some importId
        | _ ->
            do! tx.CommitAsync()
            return None
    }

/// Normalize every page of one claimed import in a single transaction
let private processImport (db: DbConfig) (importId: Guid) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        // Holding this lock makes a concurrent user deletion wait until we're done (research R13)
        use lockImport =
            new NpgsqlCommand(
                "SELECT user_id, received_at FROM cr_imports WHERE id = @id AND status = 'processing' FOR UPDATE",
                connection,
                tx
            )

        lockImport.Parameters.AddWithValue("id", importId) |> ignore

        let! header =
            task {
                use! r = lockImport.ExecuteReaderAsync()
                return if r.Read() then Some(r.GetGuid 0, r.GetDateTime 1) else None
            }

        match header with
        | None -> () // deleted or reclaimed meanwhile
        | Some(userId, receivedAt) ->
            use pagesCmd =
                new NpgsqlCommand(
                    "SELECT page_index, captured_at, body::text FROM cr_import_pages WHERE import_id = @id ORDER BY page_index",
                    connection,
                    tx
                )

            pagesCmd.Parameters.AddWithValue("id", importId) |> ignore

            let! pages =
                task {
                    use! r = pagesCmd.ExecuteReaderAsync()
                    let rows = ResizeArray()

                    while r.Read() do
                        rows.Add(r.GetInt32 0, (if r.IsDBNull 1 then receivedAt else r.GetDateTime 1), r.GetString 2)

                    return List.ofSeq rows
                }

            let outcomes = ResizeArray<EntryOutcome>()

            for pageIndex, sourceAt, body in pages do
                use doc = JsonDocument.Parse body

                match parsePage doc.RootElement with
                | Error reason -> failwith $"page {pageIndex}: {reason}" // validated on accept; corrupt storage fails the import
                | Ok entries ->
                    use batch = new NpgsqlBatch(connection, tx)

                    for index, entry in List.indexed entries do
                        let commands, outcome = entryCommands userId importId sourceAt pageIndex index entry
                        commands |> List.iter batch.BatchCommands.Add
                        outcomes.Add outcome

                    if batch.BatchCommands.Count > 0 then
                        let! _ = batch.ExecuteNonQueryAsync()
                        ()

            let countOf outcome =
                outcomes |> Seq.filter ((=) outcome) |> Seq.length

            use complete =
                new NpgsqlCommand(
                    """
                    UPDATE cr_imports SET status = 'completed', completed_at = now(), error = NULL,
                        processed = @processed, unresolved = @unresolved, unsupported = @unsupported, failed = @failed
                    WHERE id = @id
                    """,
                    connection,
                    tx
                )

            complete.Parameters.AddWithValue("id", importId) |> ignore
            complete.Parameters.AddWithValue("processed", countOf EntryOutcome.Processed) |> ignore
            complete.Parameters.AddWithValue("unresolved", countOf EntryOutcome.Unresolved) |> ignore
            complete.Parameters.AddWithValue("unsupported", countOf EntryOutcome.Unsupported) |> ignore
            complete.Parameters.AddWithValue("failed", countOf EntryOutcome.Failed) |> ignore
            let! _ = complete.ExecuteNonQueryAsync()
            do! tx.CommitAsync()
    }

let private markFailed (db: DbConfig) (importId: Guid) (error: string) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "UPDATE cr_imports SET status = 'failed', completed_at = now(), error = @error WHERE id = @id AND status = 'processing'",
                connection
            )

        cmd.Parameters.AddWithValue("id", importId) |> ignore
        cmd.Parameters.AddWithValue("error", error) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// Process the next pending import, if any. Returns true when one was processed (or failed).
let processNext (db: DbConfig) : Task<bool> =
    task {
        match! claimNext db with
        | None -> return false
        | Some importId ->
            try
                do! processImport db importId
            with ex ->
                do! markFailed db importId ex.Message

            return true
    }

/// Polls for pending imports; sleeps only when there is nothing to do
type ImportProcessorService(db: DbConfig, logger: ILogger<ImportProcessorService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    let! worked = processNext db

                    if not worked then
                        do! Task.Delay(TimeSpan.FromSeconds 2.0, stoppingToken)
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    logger.LogError(ex, "Import worker error; retrying shortly")

                    try
                        do! Task.Delay(TimeSpan.FromSeconds 5.0, stoppingToken)
                    with :? OperationCanceledException ->
                        ()
        }
