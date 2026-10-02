/// SQL for the MyAnimeList catalog and the season matches (feature 003, data-model.md).
/// Shared by the enrichment worker and the mal-* operator commands.
/// Automatic writes never touch a manual row (`method = 'manual'`, FR-006).
module FrunchyBackend.Infrastructure.MalCatalog

open System
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.JikanModel
open FrunchyBackend.Infrastructure.MalMatching

let private add (cmd: NpgsqlCommand) (name: string) (dbType: NpgsqlDbType) (value: obj) =
    cmd.Parameters.Add(NpgsqlParameter(name, dbType, Value = (if isNull value then box DBNull.Value else value)))
    |> ignore

let private opt (v: 'T option) : obj =
    match v with
    | Some x -> box x
    | None -> null

let private textOpt (v: string option) : obj = opt v

// ---------------------------------------------------------------------------------------------
// Anime
// ---------------------------------------------------------------------------------------------

/// Insert or replace an anime with its tags and relations, in the caller's transaction.
/// `rederive` (mal-rebuild) keeps the fetch/check times, the etag and the refresh state.
let private upsertAnimeWith (rederive: bool) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (a: JikanAnime) (raw: string) (dataHash: string) (etag: string option) (now: DateTimeOffset) : Task<unit> =
    task {
        let freshness =
            if rederive then
                ""
            else
                """, etag = EXCLUDED.etag, fetched_at = EXCLUDED.fetched_at,
                    checked_at = EXCLUDED.checked_at, missing_since = NULL, refresh_attempts = 0,
                    refresh_next_at = NULL, refresh_claimed_at = NULL, refresh_requested_at = NULL, refresh_error = NULL"""

        use cmd =
            new NpgsqlCommand(
                $"""
                INSERT INTO mal_anime (mal_id, url, title, title_english, title_japanese, synonyms, type, episodes, status, airing,
                                       aired_from, year, season, score, image_url, raw, data_hash, etag, fetched_at, checked_at)
                VALUES (@id, @url, @title, @en, @ja, @syn, @type, @episodes, @status, @airing,
                        @aired, @year, @season, @score, @image, @raw, @hash, @etag, @now, @now)
                ON CONFLICT (mal_id) DO UPDATE SET
                    url = EXCLUDED.url, title = EXCLUDED.title, title_english = EXCLUDED.title_english,
                    title_japanese = EXCLUDED.title_japanese, synonyms = EXCLUDED.synonyms, type = EXCLUDED.type,
                    episodes = EXCLUDED.episodes, status = EXCLUDED.status, airing = EXCLUDED.airing,
                    aired_from = EXCLUDED.aired_from, year = EXCLUDED.year, season = EXCLUDED.season,
                    score = EXCLUDED.score, image_url = EXCLUDED.image_url, raw = EXCLUDED.raw,
                    data_hash = EXCLUDED.data_hash{freshness}
                """,
                conn,
                tx
            )

        add cmd "id" NpgsqlDbType.Integer a.MalId
        add cmd "url" NpgsqlDbType.Text a.Url
        add cmd "title" NpgsqlDbType.Text a.Title
        add cmd "en" NpgsqlDbType.Text (textOpt a.TitleEnglish)
        add cmd "ja" NpgsqlDbType.Text (textOpt a.TitleJapanese)
        add cmd "syn" (NpgsqlDbType.Array ||| NpgsqlDbType.Text) (box (Array.ofList a.Synonyms))
        add cmd "type" NpgsqlDbType.Text (textOpt a.Type)
        add cmd "episodes" NpgsqlDbType.Integer (opt a.Episodes)
        add cmd "status" NpgsqlDbType.Text (textOpt a.Status)
        add cmd "airing" NpgsqlDbType.Boolean a.Airing
        add cmd "aired" NpgsqlDbType.Date (opt a.AiredFrom)
        add cmd "year" NpgsqlDbType.Integer (opt a.Year)
        add cmd "season" NpgsqlDbType.Text (textOpt a.Season)
        add cmd "score" NpgsqlDbType.Numeric (opt a.Score)
        add cmd "image" NpgsqlDbType.Text (textOpt a.ImageUrl)
        add cmd "raw" NpgsqlDbType.Json raw
        add cmd "hash" NpgsqlDbType.Text dataHash
        add cmd "etag" NpgsqlDbType.Text (textOpt etag)
        add cmd "now" NpgsqlDbType.TimestampTz now
        let! _ = cmd.ExecuteNonQueryAsync()

        use clear =
            new NpgsqlCommand(
                "DELETE FROM mal_anime_tags WHERE mal_id = @id; DELETE FROM mal_relations WHERE mal_id = @id",
                conn,
                tx
            )

        add clear "id" NpgsqlDbType.Integer a.MalId
        let! _ = clear.ExecuteNonQueryAsync()

        let kinds =
            [ "genre", a.Genres
              "explicit_genre", a.ExplicitGenres
              "theme", a.Themes
              "demographic", a.Demographics
              "studio", a.Studios ]

        for kind, tags in kinds do
            for position, tag in List.indexed tags do
                use t =
                    new NpgsqlCommand(
                        """
                        INSERT INTO mal_tags (kind, tag_id, name) VALUES (@kind, @tag, @name)
                        ON CONFLICT (kind, tag_id) DO UPDATE SET name = EXCLUDED.name;
                        INSERT INTO mal_anime_tags (mal_id, kind, tag_id, position) VALUES (@id, @kind, @tag, @pos)
                        ON CONFLICT DO NOTHING
                        """,
                        conn,
                        tx
                    )

                add t "id" NpgsqlDbType.Integer a.MalId
                add t "kind" NpgsqlDbType.Text kind
                add t "tag" NpgsqlDbType.Integer tag.Id
                add t "name" NpgsqlDbType.Text tag.Name
                add t "pos" NpgsqlDbType.Integer position
                let! _ = t.ExecuteNonQueryAsync()
                ()

        for relation in a.Relations do
            for entry in relation.Entries do
                use r =
                    new NpgsqlCommand(
                        """
                        INSERT INTO mal_relations (mal_id, relation, related_id, related_name) VALUES (@id, @rel, @rid, @name)
                        ON CONFLICT DO NOTHING
                        """,
                        conn,
                        tx
                    )

                add r "id" NpgsqlDbType.Integer a.MalId
                add r "rel" NpgsqlDbType.Text relation.Relation
                add r "rid" NpgsqlDbType.Integer entry.Id
                add r "name" NpgsqlDbType.Text entry.Name
                let! _ = r.ExecuteNonQueryAsync()
                ()
    }

let upsertAnime conn tx a raw dataHash etag now = upsertAnimeWith false conn tx a raw dataHash etag now

/// Re-derive an anime's columns, tags and relations from its stored body (mal-rebuild)
let rederiveAnime conn tx a raw dataHash = upsertAnimeWith true conn tx a raw dataHash None DateTimeOffset.MinValue

/// Store a `/full` response in its own transaction
let saveAnime (db: DbConfig) (a: JikanAnime) (raw: string) (dataHash: string) (etag: string option) (now: DateTimeOffset) : Task<unit> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        do! upsertAnime conn tx a raw dataHash etag now
        do! tx.CommitAsync()
    }

let private exec (db: DbConfig) (sql: string) (fill: NpgsqlCommand -> unit) : Task<int> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use cmd = new NpgsqlCommand(sql, conn)
        fill cmd
        return! cmd.ExecuteNonQueryAsync()
    }

/// The refresh found the details unchanged: only `checked_at` moves
let touchChecked (db: DbConfig) (malId: int) (now: DateTimeOffset) : Task<unit> =
    task {
        let! _ =
            exec
                db
                """
                UPDATE mal_anime SET checked_at = @now, refresh_attempts = 0, refresh_next_at = NULL,
                       refresh_claimed_at = NULL, refresh_requested_at = NULL, refresh_error = NULL
                WHERE mal_id = @id
                """
                (fun cmd ->
                    add cmd "id" NpgsqlDbType.Integer malId
                    add cmd "now" NpgsqlDbType.TimestampTz now)

        ()
    }

/// The refresh got 404: keep the last details, flag them
let markMissing (db: DbConfig) (malId: int) (now: DateTimeOffset) : Task<unit> =
    task {
        let! _ =
            exec
                db
                """
                UPDATE mal_anime SET missing_since = coalesce(missing_since, @now), checked_at = @now, refresh_attempts = 0,
                       refresh_next_at = NULL, refresh_claimed_at = NULL, refresh_requested_at = NULL, refresh_error = NULL
                WHERE mal_id = @id
                """
                (fun cmd ->
                    add cmd "id" NpgsqlDbType.Integer malId
                    add cmd "now" NpgsqlDbType.TimestampTz now)

        ()
    }

/// The stored `/full` body of an anime, with when it was last confirmed
let tryAnime (db: DbConfig) (malId: int) : Task<(string * string option * DateTimeOffset) option> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT raw::text, etag, checked_at FROM mal_anime WHERE mal_id = @id", conn)
        add cmd "id" NpgsqlDbType.Integer malId
        use! reader = cmd.ExecuteReaderAsync()

        let! found = reader.ReadAsync()

        if found then
            let etag = if reader.IsDBNull 1 then None else Some(reader.GetString 1)
            return Some(reader.GetString 0, etag, reader.GetFieldValue<DateTimeOffset> 2)
        else
            return None
    }

// ---------------------------------------------------------------------------------------------
// Searches
// ---------------------------------------------------------------------------------------------

let saveSearch (db: DbConfig) (query: string) (raw: string) (now: DateTimeOffset) : Task<unit> =
    task {
        let! _ =
            exec
                db
                """
                INSERT INTO mal_searches (query, raw, searched_at) VALUES (@q, @raw, @now)
                ON CONFLICT (query) DO UPDATE SET raw = EXCLUDED.raw, searched_at = EXCLUDED.searched_at
                """
                (fun cmd ->
                    add cmd "q" NpgsqlDbType.Text query
                    add cmd "raw" NpgsqlDbType.Json raw
                    add cmd "now" NpgsqlDbType.TimestampTz now)

        ()
    }

let trySearch (db: DbConfig) (query: string) : Task<(string * DateTimeOffset) option> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT raw::text, searched_at FROM mal_searches WHERE query = @q", conn)
        add cmd "q" NpgsqlDbType.Text query
        use! reader = cmd.ExecuteReaderAsync()
        let! found = reader.ReadAsync()

        if found then
            return Some(reader.GetString 0, reader.GetFieldValue<DateTimeOffset> 1)
        else
            return None
    }

// ---------------------------------------------------------------------------------------------
// Season matches
// ---------------------------------------------------------------------------------------------

let private candidatesJson (candidates: Candidate list) =
    candidates
    |> List.map (fun c ->
        dict [ "malId", box c.MalId; "title", box c.Title; "type", opt c.Type; "year", opt c.Year ])
    |> JsonSerializer.Serialize

/// The series and season titles a decision was made for (re-queued when they change)
let titleSeen (seriesTitle: string) (seasonTitle: string option) =
    $"""{seriesTitle} ‖ {defaultArg seasonTitle ""}"""

/// Write an automatic decision for one season. Manual rows are left alone; returns false then.
let writeDecision (db: DbConfig) (seasonId: string) (decision: Decision) (seen: string) (now: DateTimeOffset) : Task<bool> =
    task {
        let status, malId, methodName, sharedFrom, reason, candidates =
            match decision with
            | Matched(id, m, shared) -> "matched", Some id, Some m.Db, shared, None, None
            | Ambiguous(reason, cs) -> "ambiguous", None, None, None, Some reason, Some cs
            | NoMatch(reason, cs) -> "no_match", None, None, None, Some reason, (if cs.IsEmpty then None else Some cs)

        let! n =
            exec
                db
                """
                UPDATE cr_season_matches SET
                    status = @status, mal_id = @mal, method = @method, shared_from = @shared,
                    candidates = @candidates::jsonb, reason = @reason, title_seen = @seen, decided_at = @now,
                    attempts = 0, next_attempt_at = NULL, claimed_at = NULL, requested_at = NULL, last_error = NULL
                WHERE season_id = @id AND method IS DISTINCT FROM 'manual'
                """
                (fun cmd ->
                    add cmd "id" NpgsqlDbType.Text seasonId
                    add cmd "status" NpgsqlDbType.Text status
                    add cmd "mal" NpgsqlDbType.Integer (opt malId)
                    add cmd "method" NpgsqlDbType.Text (textOpt methodName)
                    add cmd "shared" NpgsqlDbType.Text (textOpt sharedFrom)
                    add cmd "candidates" NpgsqlDbType.Text (candidates |> Option.map candidatesJson |> textOpt)
                    add cmd "reason" NpgsqlDbType.Text (textOpt reason)
                    add cmd "seen" NpgsqlDbType.Text seen
                    add cmd "now" NpgsqlDbType.TimestampTz now)

        return n > 0
    }

/// Backoff after a temporary failure: 1 min × 2^(attempts−1), capped at 6 h, ±20% jitter (FR-014)
let backoff (attempts: int) (jitter: float) =
    let minutes = Math.Min(360.0, Math.Pow(2.0, float (max 0 (attempts - 1))))
    TimeSpan.FromMinutes(minutes * jitter)

/// Record a temporary failure: attempts + 1, next attempt later, `failed` from the third (FR-014, FR-017)
let writeTemporaryFailure (db: DbConfig) (seasonId: string) (error: string) (now: DateTimeOffset) (jitter: float) : Task<unit> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                UPDATE cr_season_matches SET attempts = attempts + 1,
                       status = CASE WHEN attempts + 1 >= 3 THEN 'failed' ELSE 'pending' END,
                       last_error = @error, claimed_at = NULL, requested_at = NULL
                WHERE season_id = @id AND method IS DISTINCT FROM 'manual'
                RETURNING attempts
                """,
                conn
            )

        add cmd "id" NpgsqlDbType.Text seasonId
        add cmd "error" NpgsqlDbType.Text (if error.Length > 300 then error.Substring(0, 300) else error)
        let! attempts = cmd.ExecuteScalarAsync()

        match attempts with
        | :? int as n ->
            use next = new NpgsqlCommand("UPDATE cr_season_matches SET next_attempt_at = @next WHERE season_id = @id", conn)
            add next "id" NpgsqlDbType.Text seasonId
            add next "next" NpgsqlDbType.TimestampTz (now + backoff n jitter)
            let! _ = next.ExecuteNonQueryAsync()
            ()
        | _ -> ()
    }

/// Leave a claimed season as it was (data not available now, e.g. during a rebuild)
let releaseClaim (db: DbConfig) (seasonId: string) : Task<unit> =
    task {
        let! _ =
            exec db "UPDATE cr_season_matches SET claimed_at = NULL WHERE season_id = @id" (fun cmd ->
                add cmd "id" NpgsqlDbType.Text seasonId)

        ()
    }

/// The series of a season, with every season the catalog has for it
let seriesOfSeason (db: DbConfig) (seasonId: string) : Task<SeriesInfo option> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT sr.id, coalesce(sr.title, sr.id), se.id, se.title, se.season_number, se.audio_locale,
                       (SELECT count(*) FROM cr_episodes e WHERE e.season_id = se.id)::int,
                       (SELECT max(e.duration_ms) FROM cr_episodes e WHERE e.season_id = se.id)
                FROM cr_seasons me
                JOIN cr_series sr ON sr.id = me.series_id
                JOIN cr_seasons se ON se.series_id = sr.id
                WHERE me.id = @id
                ORDER BY se.season_number NULLS LAST, se.id
                """,
                conn
            )

        add cmd "id" NpgsqlDbType.Text seasonId
        use! reader = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while! reader.ReadAsync() do
            let s =
                { Id = reader.GetString 2
                  Title = if reader.IsDBNull 3 then None else Some(reader.GetString 3)
                  Number = if reader.IsDBNull 4 then None else Some(reader.GetInt32 4)
                  AudioLocale = if reader.IsDBNull 5 then None else Some(reader.GetString 5)
                  EpisodeCount = reader.GetInt32 6
                  MaxDurationMs = if reader.IsDBNull 7 then None else Some(reader.GetInt32 7) }

            rows.Add((reader.GetString 0, reader.GetString 1, s))

        match List.ofSeq rows with
        | [] -> return None
        | ((seriesId, title, _) :: _) as all ->
            return
                Some
                    { SeriesId = seriesId
                      Title = title
                      Seasons = all |> List.map (fun (_, _, s) -> s) }
    }

/// Set a season by hand (FR-006): matched to `malId`, or "not on MyAnimeList" with None.
/// Seasons whose match follows this one as dubs follow it too. False for an unknown season.
let writeManual (db: DbConfig) (seasonId: string) (malId: int option) (now: DateTimeOffset) : Task<bool> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()

        use exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM cr_seasons WHERE id = @id)", conn, tx)
        add exists "id" NpgsqlDbType.Text seasonId
        let! found = exists.ExecuteScalarAsync()

        if not (unbox<bool> found) then
            return false
        else
            let status = if malId.IsSome then "matched" else "no_match"

            use cmd =
                new NpgsqlCommand(
                    """
                    INSERT INTO cr_season_matches (season_id, status) VALUES (@id, 'pending')
                    ON CONFLICT (season_id) DO NOTHING;

                    UPDATE cr_season_matches SET
                        status = @status, mal_id = @mal, method = 'manual', shared_from = NULL, candidates = NULL,
                        reason = CASE WHEN @mal IS NULL THEN 'manual-none' END, decided_at = @now, attempts = 0,
                        next_attempt_at = NULL, claimed_at = NULL, requested_at = NULL, last_error = NULL
                    WHERE season_id = @id;

                    UPDATE cr_season_matches SET
                        status = @status, mal_id = @mal, candidates = NULL, reason = NULL, decided_at = @now,
                        attempts = 0, next_attempt_at = NULL, claimed_at = NULL, requested_at = NULL, last_error = NULL
                    WHERE shared_from = @id AND method = 'dub';
                    """,
                    conn,
                    tx
                )

            add cmd "id" NpgsqlDbType.Text seasonId
            add cmd "status" NpgsqlDbType.Text status
            add cmd "mal" NpgsqlDbType.Integer (opt malId)
            add cmd "now" NpgsqlDbType.TimestampTz now
            let! _ = cmd.ExecuteNonQueryAsync()
            do! tx.CommitAsync()
            return true
    }
