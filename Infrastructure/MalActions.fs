/// The MyAnimeList match actions shared by the operator CLI (`mal-set`, `mal-none`, `mal-retry`,
/// `mal-auto`) and the review pages, so both behave identically (feature 007, research R3).
/// Each action says which state it expects the season to be in; the check and the write share one
/// transaction (the row is locked first), so a stale form or a concurrent decision writes nothing.
module FrunchyBackend.Infrastructure.MalActions

open System
open System.Net.Http
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MalMatching

/// What state the season must be in for the action to apply
type Expect =
    /// Not decided yet: ambiguous, no_match or failed, and not manual (the review list)
    | Open
    /// Decided by hand (the manual view)
    | Manual
    /// No check: what the CLI has always done
    | Any

type ActionError =
    | UnknownSeason
    /// MyAnimeList says that id doesn't exist; nothing changed
    | UnknownAnime
    /// The service failed temporarily or isn't configured; nothing changed
    | Unavailable of string
    /// The season isn't in the state the caller expected; nothing changed
    | NotOpen

let private openStatuses = set [ "ambiguous"; "no_match"; "failed" ]

let private addText (cmd: NpgsqlCommand) (name: string) (value: obj) =
    cmd.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Text, Value = value)) |> ignore

/// Lock the season's match row and check it is in the expected state. Nothing to check for `Any`.
let private guard (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (expect: Expect) (seasonId: string) : Task<Result<unit, ActionError>> =
    task {
        match expect with
        | Any -> return Ok()
        | _ ->
            use exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM cr_seasons WHERE id = @id)", conn, tx)
            addText exists "id" seasonId
            let! found = exists.ExecuteScalarAsync()

            if not (unbox<bool> found) then
                return Error UnknownSeason
            else
                use locked =
                    new NpgsqlCommand("SELECT status, method FROM cr_season_matches WHERE season_id = @id FOR UPDATE", conn, tx)

                addText locked "id" seasonId
                use! r = locked.ExecuteReaderAsync()

                // a season the sweep hasn't reached has no row yet: nothing is decided, nothing to fix
                if not (r.Read()) then
                    return Error NotOpen
                else
                    let status = r.GetString 0
                    let isManual = not (r.IsDBNull 1) && r.GetString 1 = "manual"

                    let ok =
                        match expect with
                        | Open -> openStatuses.Contains status && not isManual
                        | Manual -> isManual
                        | Any -> true

                    return if ok then Ok() else Error NotOpen
    }

/// Match a season by hand. The anime is fetched (once, paced) only when it isn't held yet; the
/// result is its title. Without a MyAnimeList client id (or while the service fails) it is
/// `Unavailable`, with no request made when the client id is missing.
let setMatch (db: DbConfig) (factory: IHttpClientFactory) (clock: TimeProvider) (mal: MalClient.MalSettings) (expect: Expect) (seasonId: string) (malId: int) : Task<Result<string, ActionError>> =
    task {
        let! season = MalCatalog.seriesOfSeason db seasonId

        match season, mal.Missing with
        | None, _ -> return Error UnknownSeason
        | Some _, Some why -> return Error(Unavailable why)
        | Some _, None ->
            match! (MalClient.Live(factory, db, clock, mal.Provider)).Lookup.Full malId with
            | Gone -> return Error UnknownAnime
            | Fetch.Unavailable reason -> return Error(Unavailable reason)
            | Got anime ->
                use conn = getConnection db
                do! conn.OpenAsync()
                use! tx = conn.BeginTransactionAsync()

                match! guard conn tx expect seasonId with
                | Error e -> return Error e
                | Ok() ->
                    let! found = MalCatalog.writeManualIn conn tx seasonId (Some malId) (clock.GetUtcNow())

                    if found then
                        do! tx.CommitAsync()
                        return Ok(anime.TitleEnglish |> Option.defaultValue anime.Title)
                    else
                        return Error UnknownSeason
    }

/// Mark a season as not on MyAnimeList (manual, never re-matched automatically); its dubs follow
let setNone (db: DbConfig) (clock: TimeProvider) (expect: Expect) (seasonId: string) : Task<Result<unit, ActionError>> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        match! guard conn tx expect seasonId with
        | Error e -> return Error e
        | Ok() ->
            let! found = MalCatalog.writeManualIn conn tx seasonId None (clock.GetUtcNow())

            if found then
                do! tx.CommitAsync()
                return Ok()
            else
                return Error UnknownSeason
    }

/// Forget the stored searches of the series of these seasons, so the next lookup asks again
let private dropSearches (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (seasonIds: string[]) : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                DELETE FROM mal_searches q
                USING cr_seasons se
                      CROSS JOIN LATERAL (
                          SELECT coalesce((SELECT sr.title FROM cr_series sr WHERE sr.id = se.series_id),
                                          (SELECT min(e.series_title) FROM cr_episodes e
                                           WHERE e.series_id = se.series_id)) AS title) t
                WHERE se.id = ANY(@ids) AND (q.query = t.title OR q.query LIKE t.title || ' %')
                """,
                conn,
                tx
            )

        cmd.Parameters.AddWithValue("ids", seasonIds) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// Queue a season, or every season of a series, at the front. Automatic rows go back to pending
/// (with their stored searches dropped, so the lookup asks again); manual rows keep their match and
/// their anime is queued for refresh instead. Returns (seasons, anime) queued; `UnknownSeason` for
/// an unknown id. With `Open` or `Manual` the id must be a season id in that state.
let retry (db: DbConfig) (expect: Expect) (id: string) : Task<Result<int * int, ActionError>> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        use find =
            new NpgsqlCommand("SELECT se.id FROM cr_seasons se WHERE se.id = @id OR se.series_id = @id", conn, tx)

        addText find "id" id
        use! r = find.ExecuteReaderAsync()
        let seasons = ResizeArray<string>()

        while r.Read() do
            seasons.Add(r.GetString 0)

        do! r.CloseAsync()

        if seasons.Count = 0 || (expect <> Any && not (seasons.Contains id)) then
            return Error UnknownSeason
        else
            match! guard conn tx expect id with
            | Error e -> return Error e
            | Ok() ->
                use queue =
                    new NpgsqlCommand(
                        "INSERT INTO cr_season_matches (season_id, status) SELECT unnest(@ids), 'pending' ON CONFLICT (season_id) DO NOTHING",
                        conn,
                        tx
                    )

                queue.Parameters.AddWithValue("ids", seasons.ToArray()) |> ignore
                let! _ = queue.ExecuteNonQueryAsync()
                do! dropSearches conn tx (seasons.ToArray())

                use cmd =
                    new NpgsqlCommand(
                        """
                        WITH auto AS (
                            UPDATE cr_season_matches SET status = 'pending', mal_id = NULL, method = NULL, shared_from = NULL,
                                   attempts = 0, next_attempt_at = NULL, claimed_at = NULL, requested_at = now(), last_error = NULL
                            WHERE season_id = ANY(@ids) AND method IS DISTINCT FROM 'manual'
                            RETURNING season_id
                        ),
                        manual AS (
                            UPDATE mal_anime SET refresh_requested_at = now(), refresh_next_at = NULL
                            WHERE mal_id IN (SELECT mal_id FROM cr_season_matches
                                             WHERE season_id = ANY(@ids) AND method = 'manual' AND mal_id IS NOT NULL)
                            RETURNING mal_id
                        )
                        SELECT (SELECT count(*) FROM auto)::int, (SELECT count(*) FROM manual)::int
                        """,
                        conn,
                        tx
                    )

                cmd.Parameters.AddWithValue("ids", seasons.ToArray()) |> ignore
                use! counts = cmd.ExecuteReaderAsync()
                let! _ = counts.ReadAsync()
                let result = counts.GetInt32 0, counts.GetInt32 1
                do! counts.CloseAsync()
                do! tx.CommitAsync()
                return Ok result
    }

/// Send a manual decision back to the matcher: the season and the dubs that follow it go to
/// pending, at the front of the queue, with the series' stored searches dropped.
let backToAutomatic (db: DbConfig) (expect: Expect) (seasonId: string) : Task<Result<unit, ActionError>> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        match! guard conn tx expect seasonId with
        | Error e -> return Error e
        | Ok() ->
            use cmd =
                new NpgsqlCommand(
                    """
                    UPDATE cr_season_matches SET status = 'pending', mal_id = NULL, method = NULL, shared_from = NULL,
                           candidates = NULL, reason = NULL, attempts = 0, next_attempt_at = NULL, claimed_at = NULL,
                           requested_at = now(), last_error = NULL
                    WHERE season_id = @id OR (shared_from = @id AND method = 'dub')
                    RETURNING season_id
                    """,
                    conn,
                    tx
                )

            addText cmd "id" seasonId
            use! r = cmd.ExecuteReaderAsync()
            let touched = ResizeArray<string>()

            while r.Read() do
                touched.Add(r.GetString 0)

            do! r.CloseAsync()

            if touched.Count = 0 then
                return Error UnknownSeason
            else
                do! dropSearches conn tx (touched.ToArray())
                do! tx.CommitAsync()
                return Ok()
    }
