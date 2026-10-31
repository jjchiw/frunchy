/// Feature 028: following the same series on other Frunchy instances. Every Frunchy keys its series actor on the
/// Crunchyroll series id (`https://{domain}/ap/actors/series-{seriesId}`, feature 015), so "the same series" is found
/// by id, never by title. This module holds the pure id rules and the data access the operator page, the outbox
/// worker and the webhook receiver share. Every change writes its `fediverse_outbox` items in the caller's
/// transaction (the outbox holds no payloads: the worker reads the follow when it runs).
module FrunchyBackend.Infrastructure.InstanceFollows

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open Npgsql

// ---------------------------------------------------------------------------
// Pure rules
// ---------------------------------------------------------------------------

let private seriesIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

/// A Crunchyroll series id as every route checks it
let isSeriesId (id: string) = not (isNull id) && seriesIdPattern.IsMatch id

/// A remote episode's group key: `{originalSeasonId}#{number}` (feature 009), or the episode id alone
let private groupKeyPattern = Regex(@"\A[A-Z0-9]{6,20}(#[0-9]{1,6})?\z", RegexOptions.CultureInvariant)

let isGroupKey (key: string) = not (isNull key) && groupKeyPattern.IsMatch key

/// The series actor of `seriesId` on another instance
let remoteSeriesActor (domain: string) (seriesId: string) = Fediverse.actorUrl domain $"series-{seriesId}"

let private actorPattern =
    Regex(@"\Ahttps://(?<host>[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*)/ap/actors/series-(?<id>[A-Z0-9]{6,20})\z", RegexOptions.CultureInvariant)

/// `(host, series id)` of `https://{host}/ap/actors/series-{ID}` exactly: https, a lower-case host, no port, user
/// info, query, fragment or extra path, and an `ID` that is a series id
let seriesIdOfActor (actorId: string) : (string * string) option =
    if isNull actorId || actorId.Length > 2048 then
        None
    else
        let m = actorPattern.Match actorId
        if m.Success then Some(m.Groups["host"].Value, m.Groups["id"].Value) else None

/// How often the outbox service runs the sweep, and how long a series the other instance didn't know waits to be
/// asked again
let sweepEvery = TimeSpan.FromHours 1.0
let retryNotFoundAfter = TimeSpan.FromDays 7.0

// ---------------------------------------------------------------------------
// Changes (each in the caller's transaction)
// ---------------------------------------------------------------------------

let private param (cmd: NpgsqlCommand) (name: string) (value: obj) =
    cmd.Parameters.AddWithValue(name, value) |> ignore

let private optionalDomain (domain: string option) : obj =
    match domain with
    | Some d -> box d
    | None -> box DBNull.Value

/// The sweep (FR-013): for every instance follow (or the one `domain`), the series follows it still lacks, one per
/// series with a watch event on an episode of known length, and the instance-made `not_found` rows untouched for 7
/// days put back in the queue. Each gets a `follow_series` item. Returns how many follows it queued.
let sweep (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (domain: string option) (now: DateTimeOffset) : Task<int> =
    task {
        use requeue =
            new NpgsqlCommand(
                """
                WITH back AS (
                    UPDATE series_follows s
                    SET state = 'queued', last_error = NULL, updated_at = @now
                    WHERE s.source = 'instance' AND s.state = 'not_found' AND s.updated_at < @now - @retry
                      AND (@domain::text IS NULL OR s.domain = @domain)
                      AND EXISTS (SELECT 1 FROM instance_follows f WHERE f.domain = s.domain)
                    RETURNING s.series_id, s.domain
                ), q AS (
                    INSERT INTO fediverse_outbox (kind, subject)
                    SELECT 'follow_series', series_id || '@' || domain FROM back
                    RETURNING 1
                )
                SELECT count(*) FROM q
                """,
                connection,
                tx
            )

        param requeue "now" now.UtcDateTime
        param requeue "retry" retryNotFoundAfter
        param requeue "domain" (optionalDomain domain)
        let! requeued = requeue.ExecuteScalarAsync()

        use missing =
            new NpgsqlCommand(
                """
                WITH watched AS (
                    SELECT DISTINCT e.series_id
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE e.duration_ms IS NOT NULL AND e.series_id ~ '^[A-Z0-9]{6,20}$'
                ), ins AS (
                    INSERT INTO series_follows (series_id, domain, remote_actor, source, created_at, updated_at)
                    SELECT w.series_id, f.domain, 'https://' || f.domain || '/ap/actors/series-' || w.series_id, 'instance', @now, @now
                    FROM instance_follows f
                    CROSS JOIN watched w
                    WHERE @domain::text IS NULL OR f.domain = @domain
                    ON CONFLICT DO NOTHING
                    RETURNING series_id, domain
                ), q AS (
                    INSERT INTO fediverse_outbox (kind, subject)
                    SELECT 'follow_series', series_id || '@' || domain FROM ins
                    RETURNING 1
                )
                SELECT count(*) FROM q
                """,
                connection,
                tx
            )

        param missing "now" now.UtcDateTime
        param missing "domain" (optionalDomain domain)
        let! added = missing.ExecuteScalarAsync()
        return int (unbox<int64> requeued) + int (unbox<int64> added)
    }

/// Follow every series watched here on `domain` (an instance follow, FR-003): the instance row, then the sweep for it.
/// True when anything was new.
let addInstance (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (domain: string) (now: DateTimeOffset) : Task<bool> =
    task {
        use insert =
            new NpgsqlCommand(
                "INSERT INTO instance_follows (domain, created_at) VALUES (@domain, @now) ON CONFLICT (domain) DO NOTHING RETURNING domain",
                connection,
                tx
            )

        param insert "domain" domain
        param insert "now" now.UtcDateTime
        let! inserted = insert.ExecuteScalarAsync()
        let! queued = sweep connection tx (Some domain) now
        return not (isNull inserted) || queued > 0
    }

/// Follow one series on `domain` (FR-003, FR-005). A new follow is queued; one an instance follow made becomes an
/// explicit one (so removing the instance follow keeps it); one that ended without being accepted (`not_found`,
/// `failed`, `rejected`, `removed`, `unfollowed`) is asked again. True when anything changed.
let addSeries (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (seriesId: string) (domain: string) (now: DateTimeOffset) : Task<bool> =
    task {
        use insert =
            new NpgsqlCommand(
                """
                INSERT INTO series_follows (series_id, domain, remote_actor, source, created_at, updated_at)
                VALUES (@series_id, @domain, @actor, 'series', @now, @now)
                ON CONFLICT (series_id, domain) DO NOTHING
                RETURNING series_id
                """,
                connection,
                tx
            )

        param insert "series_id" seriesId
        param insert "domain" domain
        param insert "actor" (remoteSeriesActor domain seriesId)
        param insert "now" now.UtcDateTime
        let! inserted = insert.ExecuteScalarAsync()

        if not (isNull inserted) then
            do! Fediverse.enqueue connection tx "follow_series" $"{seriesId}@{domain}"
            return true
        else
            use retry =
                new NpgsqlCommand(
                    """
                    UPDATE series_follows
                    SET source = 'series', state = 'queued', last_error = NULL, updated_at = @now
                    WHERE series_id = @series_id AND domain = @domain AND state IN ('not_found', 'failed', 'rejected', 'removed', 'unfollowed')
                    RETURNING series_id
                    """,
                    connection,
                    tx
                )

            param retry "series_id" seriesId
            param retry "domain" domain
            param retry "now" now.UtcDateTime
            let! requeued = retry.ExecuteScalarAsync()

            if not (isNull requeued) then
                do! Fediverse.enqueue connection tx "follow_series" $"{seriesId}@{domain}"
                return true
            else
                use promote =
                    new NpgsqlCommand(
                        "UPDATE series_follows SET source = 'series', updated_at = @now WHERE series_id = @series_id AND domain = @domain AND source = 'instance' RETURNING series_id",
                        connection,
                        tx
                    )

                param promote "series_id" seriesId
                param promote "domain" domain
                param promote "now" now.UtcDateTime
                let! promoted = promote.ExecuteScalarAsync()
                return not (isNull promoted)
    }

/// Stop following `domain` as an instance (FR-004): the instance row and every series follow it made go, each queued
/// for `unfollow_series`; explicit series follows stay, and the `unfollowed` markers (already undone) go without a call.
/// True when anything was removed.
let removeInstance (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (domain: string) : Task<bool> =
    task {
        use instance = new NpgsqlCommand("DELETE FROM instance_follows WHERE domain = @domain RETURNING domain", connection, tx)
        param instance "domain" domain
        let! row = instance.ExecuteScalarAsync()

        use series =
            new NpgsqlCommand(
                """
                WITH gone AS (
                    DELETE FROM series_follows WHERE domain = @domain AND source = 'instance' RETURNING series_id, domain
                ), markers AS (
                    DELETE FROM series_follows WHERE domain = @domain AND state = 'unfollowed'
                ), q AS (
                    INSERT INTO fediverse_outbox (kind, subject)
                    SELECT 'unfollow_series', series_id || '@' || domain FROM gone
                    RETURNING 1
                )
                SELECT count(*) FROM q
                """,
                connection,
                tx
            )

        param series "domain" domain
        let! removed = series.ExecuteScalarAsync()
        return not (isNull row) || unbox<int64> removed > 0L
    }

/// Stop following one series on `domain`, whatever made the follow. While an instance follow of `domain` stays, the row
/// stays too as an `unfollowed` marker, so the sweep doesn't follow the series again (adding it by hand does); otherwise
/// it goes. True when there was a follow to stop.
let removeSeries (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (seriesId: string) (domain: string) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH marked AS (
                    UPDATE series_follows
                    SET state = 'unfollowed', source = 'series', follow_id = NULL, last_error = NULL, updated_at = now()
                    WHERE series_id = @series_id AND domain = @domain AND state <> 'unfollowed'
                      AND EXISTS (SELECT 1 FROM instance_follows f WHERE f.domain = @domain)
                    RETURNING series_id
                ), gone AS (
                    DELETE FROM series_follows
                    WHERE series_id = @series_id AND domain = @domain AND state <> 'unfollowed'
                      AND NOT EXISTS (SELECT 1 FROM instance_follows f WHERE f.domain = @domain)
                    RETURNING series_id
                )
                SELECT series_id FROM marked UNION ALL SELECT series_id FROM gone
                """,
                connection,
                tx
            )
        param cmd "series_id" seriesId
        param cmd "domain" domain
        let! row = cmd.ExecuteScalarAsync()

        if isNull row then
            return false
        else
            do! Fediverse.enqueue connection tx "unfollow_series" $"{seriesId}@{domain}"
            return true
    }

// ---------------------------------------------------------------------------
// Reads
// ---------------------------------------------------------------------------

type InstanceRow =
    { Domain: string
      Since: DateTime
      /// Series followed on this domain, whoever made the follow
      SeriesCount: int }

type SeriesRow =
    { SeriesId: string
      Title: string option
      Domain: string
      RemoteActor: string
      /// The remote actor's handle, once a webhook has told us
      Handle: string option
      Source: string
      State: string
      Since: DateTime
      LastError: string option }

/// How many series follows the page lists
[<Literal>]
let MaxListed = 500

/// The page's data: the instance follows, and the 500 newest series follows
let list (connection: NpgsqlConnection) : Task<InstanceRow list * SeriesRow list> =
    task {
        use instances =
            new NpgsqlCommand(
                """
                SELECT f.domain, f.created_at, (SELECT count(*) FROM series_follows s WHERE s.domain = f.domain)
                FROM instance_follows f
                ORDER BY f.created_at DESC, f.domain
                """,
                connection
            )

        let instanceRows = ResizeArray<InstanceRow>()

        do!
            task {
                use! reader = instances.ExecuteReaderAsync()

                while reader.Read() do
                    instanceRows.Add { Domain = reader.GetString 0; Since = reader.GetDateTime 1; SeriesCount = int (reader.GetInt64 2) }
            }

        use series =
            new NpgsqlCommand(
                """
                SELECT s.series_id, c.title, s.domain, s.remote_actor, a.handle, s.source, s.state, s.created_at, s.last_error
                FROM series_follows s
                LEFT JOIN cr_series c ON c.id = s.series_id
                LEFT JOIN remote_actors a ON a.id = s.remote_actor
                ORDER BY s.created_at DESC, s.domain, s.series_id
                LIMIT @limit
                """,
                connection
            )

        param series "limit" MaxListed
        let seriesRows = ResizeArray<SeriesRow>()

        do!
            task {
                use! reader = series.ExecuteReaderAsync()
                let text (i: int) = if reader.IsDBNull i then None else Some(reader.GetString i)

                while reader.Read() do
                    seriesRows.Add
                        { SeriesId = reader.GetString 0
                          Title = text 1
                          Domain = reader.GetString 2
                          RemoteActor = reader.GetString 3
                          Handle = text 4
                          Source = reader.GetString 5
                          State = reader.GetString 6
                          Since = reader.GetDateTime 7
                          LastError = text 8 }
            }

        return List.ofSeq instanceRows, List.ofSeq seriesRows
    }

/// Which local episode a remote thread belongs to (FR-010, "Which local episode"): the remote episode id when our
/// catalog has that episode of this series; otherwise an episode of this series with the group key's number whose
/// season is the key's season or a season matched as sharing it (`cr_season_matches.shared_from`), our own season
/// first. None when neither is known.
let localEpisodeOf
    (connection: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    (seriesId: string)
    (remoteEpisodeId: string)
    (remoteGroupKey: string option)
    : Task<string option> =
    task {
        use byId = new NpgsqlCommand("SELECT id FROM cr_episodes WHERE id = @id AND series_id = @series_id", connection, tx)
        param byId "id" remoteEpisodeId
        param byId "series_id" seriesId

        match! byId.ExecuteScalarAsync() with
        | :? string as id -> return Some id
        | _ ->
            let seasonAndNumber =
                match remoteGroupKey with
                | Some key ->
                    match key.Split '#' with
                    | [| season; number |] ->
                        match Int32.TryParse number with
                        | true, n -> Some(season, n)
                        | _ -> None
                    | _ -> None
                | None -> None

            match seasonAndNumber with
            | None -> return None
            | Some(season, number) ->
                use byGroup =
                    new NpgsqlCommand(
                        """
                        SELECT e.id
                        FROM cr_episodes e
                        LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                        WHERE e.series_id = @series_id AND e.episode_number = @number
                          AND (e.season_id = @season OR m.shared_from = @season)
                        ORDER BY (e.season_id = @season) DESC, e.id
                        LIMIT 1
                        """,
                        connection,
                        tx
                    )

                param byGroup "series_id" seriesId
                param byGroup "number" number
                param byGroup "season" season

                match! byGroup.ExecuteScalarAsync() with
                | :? string as id -> return Some id
                | _ -> return None
    }
