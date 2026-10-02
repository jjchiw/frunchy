/// MyAnimeList enrichment worker (feature 003, research R5–R7, R11).
/// Postgres is the queue: `cr_season_matches` rows for lookups, `mal_anime.refresh_*` for refreshes.
/// Imports are never touched: new seasons are discovered by a sweep (FR-002).
module FrunchyBackend.Workers.Enrichment

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.JikanClient
open FrunchyBackend.Infrastructure.MalMatching

type EnrichmentSettings =
    { PauseAfterFailures: int
      PauseFor: TimeSpan
      RefreshAfterDays: int
      RefreshAiringAfterDays: int }

let readSettings (config: IConfiguration) =
    let section = config.GetSection "Frunchy:Enrichment"

    { PauseAfterFailures = section.GetValue<int>("PauseAfterFailures", 5)
      PauseFor =
        match section["PauseFor"] with
        | null
        | "" -> TimeSpan.FromMinutes 5.0
        | v -> TimeSpan.Parse v
      RefreshAfterDays = section.GetValue<int>("RefreshAfterDays", 30)
      RefreshAiringAfterDays = section.GetValue<int>("RefreshAiringAfterDays", 7) }

let staleClaimAfter = TimeSpan.FromMinutes 10.0

let private jitter () = 0.8 + Random.Shared.NextDouble() * 0.4

let private add (cmd: NpgsqlCommand) (name: string) (dbType: NpgsqlDbType) (value: obj) =
    cmd.Parameters.Add(NpgsqlParameter(name, dbType, Value = value)) |> ignore

let private open' (db: DbConfig) =
    task {
        let conn = getConnection db
        do! conn.OpenAsync()
        return conn
    }

/// Queue every season without a match row; re-queue automatic no-match/ambiguous rows whose
/// titles changed since they were decided (research R6). Idempotent.
let sweep (db: DbConfig) : Task<unit> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO cr_season_matches (season_id, status)
                SELECT se.id, 'pending' FROM cr_seasons se
                WHERE NOT EXISTS (SELECT 1 FROM cr_season_matches m WHERE m.season_id = se.id);

                UPDATE cr_season_matches m
                SET status = 'pending', attempts = 0, next_attempt_at = NULL, last_error = NULL
                FROM cr_seasons se JOIN cr_series sr ON sr.id = se.series_id
                WHERE m.season_id = se.id
                  AND m.status IN ('no_match', 'ambiguous')
                  AND m.method IS DISTINCT FROM 'manual'
                  AND m.title_seen IS DISTINCT FROM (coalesce(sr.title, sr.id) || ' ‖ ' || coalesce(se.title, ''));
                """,
                conn
            )

        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// Claim the next season to look up, in FR-002a order: operator requests, then the most recently
/// watched (across all users). Rows waiting for their next attempt are skipped. SKIP LOCKED.
let claimSeason (db: DbConfig) (now: DateTimeOffset) (requestedOnly: bool) : Task<string option> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE cr_season_matches SET claimed_at = NULL WHERE claimed_at < @stale;

                WITH next AS (
                    SELECT m.season_id FROM cr_season_matches m
                    WHERE m.status IN ('pending', 'failed')
                      AND m.method IS DISTINCT FROM 'manual'
                      AND m.claimed_at IS NULL
                      AND (m.next_attempt_at IS NULL OR m.next_attempt_at <= @now)
                      AND (NOT @requestedOnly OR m.requested_at IS NOT NULL)
                    ORDER BY (m.requested_at IS NULL), m.requested_at,
                             (SELECT max(w.watched_at) FROM cr_episodes e
                              JOIN watch_events w ON w.cr_episode_id = e.id
                              WHERE e.season_id = m.season_id) DESC NULLS LAST,
                             m.season_id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE cr_season_matches m SET claimed_at = @now
                FROM next WHERE m.season_id = next.season_id
                RETURNING m.season_id
                """,
                conn
            )

        add cmd "now" NpgsqlDbType.TimestampTz now
        add cmd "stale" NpgsqlDbType.TimestampTz (now - staleClaimAfter)
        add cmd "requestedOnly" NpgsqlDbType.Boolean requestedOnly

        match! cmd.ExecuteScalarAsync() with
        | :? string as id -> return Some id
        | _ -> return None
    }

/// Claim the other due seasons of the same series, so the series is resolved once (anchor reused)
let private claimSiblings (db: DbConfig) (seasonIds: string list) (now: DateTimeOffset) : Task<string list> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE cr_season_matches SET claimed_at = @now
                WHERE season_id = ANY(@ids)
                  AND status IN ('pending', 'failed')
                  AND method IS DISTINCT FROM 'manual'
                  AND claimed_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                RETURNING season_id
                """,
                conn
            )

        add cmd "now" NpgsqlDbType.TimestampTz now
        add cmd "ids" (NpgsqlDbType.Array ||| NpgsqlDbType.Text) (Array.ofList seasonIds)
        use! reader = cmd.ExecuteReaderAsync()
        let ids = ResizeArray()

        while! reader.ReadAsync() do
            ids.Add(reader.GetString 0)

        return List.ofSeq ids
    }

/// Decided rows of a series (status, mal id), to let a new dub follow an already decided original
let private decidedRows (db: DbConfig) (seasonIds: string list) : Task<Map<string, string * int option * string option>> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                SELECT season_id, status, mal_id, reason FROM cr_season_matches
                WHERE season_id = ANY(@ids) AND status IN ('matched', 'ambiguous', 'no_match')
                """,
                conn
            )

        add cmd "ids" (NpgsqlDbType.Array ||| NpgsqlDbType.Text) (Array.ofList seasonIds)
        use! reader = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while! reader.ReadAsync() do
            rows.Add(
                reader.GetString 0,
                (reader.GetString 1,
                 (if reader.IsDBNull 2 then None else Some(reader.GetInt32 2)),
                 (if reader.IsDBNull 3 then None else Some(reader.GetString 3)))
            )

        return Map.ofSeq rows
    }

type ProcessResult =
    | Idle
    | Done
    /// Nothing could be decided because the service failed (counts toward the pause, FR-015)
    | TemporaryFailure

/// Resolve the given seasons of one series and hand each outcome to `write` (a decision, with the
/// titles it was made for) or `onDeferred`. Shared with mal-rebuild, which writes only real changes
/// and leaves deferred seasons untouched; the worker writes every decision and backs off deferred ones.
let resolveAndWrite
    (db: DbConfig)
    (source: Source)
    (series: SeriesInfo)
    (pending: string list)
    (write: string -> Decision -> string -> Task<unit>)
    (onDeferred: string -> string -> Task<unit>)
    : Task<int * int> =
    task {
        let originals, dubs = splitDubs series.Seasons
        let pendingSet = Set.ofList pending

        // A new dub whose original is already decided just follows it (no lookup)
        let! decided = decidedRows db (originals |> List.map _.Id)
        let mutable followed = Set.empty
        let mutable decidedCount = 0

        for dub, original in dubs do
            if pendingSet.Contains dub.Id && not (pendingSet.Contains original.Id) then
                match decided.TryFind original.Id with
                | Some("matched", Some malId, _) ->
                    do! write dub.Id (Matched(malId, ByDub, Some original.Id)) (MalCatalog.titleSeen series.Title dub.Title)
                    followed <- followed.Add dub.Id
                    decidedCount <- decidedCount + 1
                | _ -> ()

        let toResolve = pendingSet - followed
        let! outcomes = resolveSeries source.Lookup series toResolve
        let mutable deferredCount = 0

        for season in series.Seasons do
            match outcomes.TryFind season.Id with
            | Some(Decided decision) ->
                do! write season.Id decision (MalCatalog.titleSeen series.Title season.Title)
                decidedCount <- decidedCount + 1
            | Some(Deferred reason) ->
                deferredCount <- deferredCount + 1
                do! onDeferred season.Id reason
            | None when toResolve.Contains season.Id ->
                deferredCount <- deferredCount + 1
                do! onDeferred season.Id "not-resolved"
            | None -> ()

        return decidedCount, deferredCount
    }

/// Look up one claimed season and the other due seasons of its series
let private lookupSeason (db: DbConfig) (source: Source) (clock: TimeProvider) (seasonId: string) : Task<ProcessResult> =
    task {
        let now = clock.GetUtcNow()

        match! MalCatalog.seriesOfSeason db seasonId with
        | None ->
            do! MalCatalog.releaseClaim db seasonId
            return Done
        | Some series ->
            let! siblings = claimSiblings db (series.Seasons |> List.map _.Id |> List.filter ((<>) seasonId)) now

            let onDeferred id reason =
                MalCatalog.writeTemporaryFailure db id reason now (jitter ())

            let write id decision seen =
                task {
                    let! _ = MalCatalog.writeDecision db id decision seen now
                    ()
                }

            let! decidedCount, deferredCount = resolveAndWrite db source series (seasonId :: siblings) write onDeferred
            return if decidedCount = 0 && deferredCount > 0 then TemporaryFailure else Done
    }

/// Claim an anime due for refresh: requested ones first, then the oldest check (data-model "Due for refresh")
let claimRefresh (db: DbConfig) (now: DateTimeOffset) (settings: EnrichmentSettings) (requestedOnly: bool) : Task<int option> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE mal_anime SET refresh_claimed_at = NULL WHERE refresh_claimed_at < @stale;

                WITH next AS (
                    SELECT mal_id FROM mal_anime
                    WHERE missing_since IS NULL
                      AND refresh_claimed_at IS NULL
                      AND (refresh_next_at IS NULL OR refresh_next_at <= @now)
                      AND (refresh_requested_at IS NOT NULL
                           OR (NOT @requestedOnly
                               AND checked_at < @now - make_interval(days => CASE WHEN airing THEN @airingDays ELSE @days END)))
                    ORDER BY (refresh_requested_at IS NULL), refresh_requested_at, checked_at, mal_id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE mal_anime a SET refresh_claimed_at = @now
                FROM next WHERE a.mal_id = next.mal_id
                RETURNING a.mal_id
                """,
                conn
            )

        add cmd "now" NpgsqlDbType.TimestampTz now
        add cmd "stale" NpgsqlDbType.TimestampTz (now - staleClaimAfter)
        add cmd "days" NpgsqlDbType.Integer settings.RefreshAfterDays
        add cmd "airingDays" NpgsqlDbType.Integer settings.RefreshAiringAfterDays
        add cmd "requestedOnly" NpgsqlDbType.Boolean requestedOnly

        match! cmd.ExecuteScalarAsync() with
        | :? int as id -> return Some id
        | _ -> return None
    }

let private storedHashAndEtag (db: DbConfig) (malId: int) : Task<string * string option> =
    task {
        use! conn = open' db
        use cmd = new NpgsqlCommand("SELECT data_hash, etag FROM mal_anime WHERE mal_id = @id", conn)
        add cmd "id" NpgsqlDbType.Integer malId
        use! reader = cmd.ExecuteReaderAsync()
        let! _ = reader.ReadAsync()
        return reader.GetString 0, (if reader.IsDBNull 1 then None else Some(reader.GetString 1))
    }

let private refreshFailed (db: DbConfig) (malId: int) (error: string) (now: DateTimeOffset) : Task<unit> =
    task {
        use! conn = open' db

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE mal_anime SET refresh_attempts = refresh_attempts + 1, refresh_error = @error,
                       refresh_claimed_at = NULL
                WHERE mal_id = @id
                RETURNING refresh_attempts
                """,
                conn
            )

        add cmd "id" NpgsqlDbType.Integer malId
        add cmd "error" NpgsqlDbType.Text (if error.Length > 300 then error.Substring(0, 300) else error)

        match! cmd.ExecuteScalarAsync() with
        | :? int as attempts ->
            use next = new NpgsqlCommand("UPDATE mal_anime SET refresh_next_at = @next WHERE mal_id = @id", conn)
            add next "id" NpgsqlDbType.Integer malId
            add next "next" NpgsqlDbType.TimestampTz (now + MalCatalog.backoff attempts (jitter ()))
            let! _ = next.ExecuteNonQueryAsync()
            ()
        | _ -> ()
    }

/// Refresh one held anime (User Story 4, research R7)
let private refreshAnime (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (malId: int) : Task<ProcessResult> =
    task {
        let now = clock.GetUtcNow()
        let! storedHash, etag = storedHashAndEtag db malId

        match! get factory (fullPath malId) etag with
        | Ok(body, newEtag) ->
            match JikanModel.parseFull body with
            | Result.Ok(_, hash) when hash = storedHash ->
                do! MalCatalog.touchChecked db malId now
                return Done
            | Result.Ok(anime, hash) ->
                do! MalCatalog.saveAnime db anime body hash newEtag now
                return Done
            | Result.Error e ->
                do! refreshFailed db malId $"unreadable anime response: {e}" now
                return TemporaryFailure
        | Unchanged ->
            do! MalCatalog.touchChecked db malId now
            return Done
        | NotFound ->
            do! MalCatalog.markMissing db malId now
            return Done
        | Definite e
        | Temporary e ->
            do! refreshFailed db malId e now
            return TemporaryFailure
    }

/// One unit of work: sweep, then (in order) a requested lookup, a requested refresh, a lookup,
/// a due refresh. Called by the service in a loop, and directly by the tests.
let processNext (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (settings: EnrichmentSettings) : Task<ProcessResult> =
    task {
        do! sweep db
        let now = clock.GetUtcNow()
        let source = Live(factory, db, clock)

        match! claimSeason db now true with
        | Some seasonId -> return! lookupSeason db source clock seasonId
        | None ->
            match! claimRefresh db now settings true with
            | Some malId -> return! refreshAnime db factory clock malId
            | None ->
                match! claimSeason db now false with
                | Some seasonId -> return! lookupSeason db source clock seasonId
                | None ->
                    match! claimRefresh db now settings false with
                    | Some malId -> return! refreshAnime db factory clock malId
                    | None -> return Idle
    }

/// The pause of FR-015: after `PauseAfterFailures` failed units in a row, wait `PauseFor`, then
/// let one probe through (a failed probe pauses again; a success resets). Returns the new count
/// and the pause to take, if any.
let afterResult (settings: EnrichmentSettings) (failuresInARow: int) (result: ProcessResult) : int * TimeSpan option =
    match result with
    | Done -> 0, None
    | Idle -> failuresInARow, None
    | TemporaryFailure ->
        let failures = failuresInARow + 1

        if failures >= settings.PauseAfterFailures then
            settings.PauseAfterFailures - 1, Some settings.PauseFor
        else
            failures, None

type EnrichmentService(db: DbConfig, factory: IHttpClientFactory, clock: TimeProvider, config: IConfiguration, logger: ILogger<EnrichmentService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        let settings = readSettings config

        task {
            logger.LogInformation("MyAnimeList enrichment worker started")
            let mutable failures = 0

            while not stoppingToken.IsCancellationRequested do
                try
                    let! result = processNext db factory clock settings
                    let count, pause = afterResult settings failures result
                    failures <- count

                    match result, pause with
                    | _, Some p ->
                        logger.LogWarning("Jikan keeps failing; pausing enrichment for {Pause}", p)
                        do! Task.Delay(p, clock, stoppingToken)
                        logger.LogInformation("Resuming enrichment with one probe")
                    | Idle, None -> do! Task.Delay(TimeSpan.FromSeconds 5.0, clock, stoppingToken)
                    | _ -> ()
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
                | ex ->
                    logger.LogError(ex, "Enrichment step failed")

                    try
                        do! Task.Delay(TimeSpan.FromSeconds 5.0, clock, stoppingToken)
                    with :? OperationCanceledException ->
                        ()
        }
        :> Task
