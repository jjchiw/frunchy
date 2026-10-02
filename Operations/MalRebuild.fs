module FrunchyBackend.Operations.MalRebuild

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MalMatching
open FrunchyBackend.Workers

type RebuildCounts =
    { AnimeRederived: int
      Unchanged: int
      Changed: int
      /// Seasons whose decision needs data that isn't held; left as they are
      DataMissing: int }

let private storedAnime (db: DbConfig) : Task<(int * string) list> =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT mal_id, raw::text FROM mal_anime ORDER BY mal_id", conn)
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while r.Read() do
            rows.Add(r.GetInt32 0, r.GetString 1)

        return List.ofSeq rows
    }

/// Current automatic decisions: season → (status, mal id, method, reason, series id)
let private automaticRows (db: DbConfig) =
    task {
        use conn = getConnection db
        do! conn.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT m.season_id, m.status, m.mal_id, m.method, m.reason, se.series_id
                FROM cr_season_matches m JOIN cr_seasons se ON se.id = m.season_id
                WHERE m.status IN ('matched', 'ambiguous', 'no_match') AND m.method IS DISTINCT FROM 'manual'
                ORDER BY se.series_id, m.season_id
                """,
                conn
            )

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray()

        while r.Read() do
            rows.Add(
                r.GetString 0,
                (r.GetString 1,
                 (if r.IsDBNull 2 then None else Some(r.GetInt32 2)),
                 (if r.IsDBNull 3 then None else Some(r.GetString 3)),
                 (if r.IsDBNull 4 then None else Some(r.GetString 4)),
                 r.GetString 5)
            )

        return List.ofSeq rows
    }

let private shape (decision: Decision) =
    match decision with
    | Matched(id, m, _) -> "matched", Some id, Some m.Db, None
    | Ambiguous(reason, _) -> "ambiguous", None, None, Some reason
    | NoMatch(reason, _) -> "no_match", None, None, Some reason

/// Re-derive every held anime from its stored body, then re-decide every automatic match from
/// stored responses only (FR-021). Sends no requests; manual rows are never touched.
let rebuild (db: DbConfig) : Task<RebuildCounts> =
    task {
        let! anime = storedAnime db
        let mutable rederived = 0

        for _, raw in anime do
            match JikanModel.parseFull raw with
            | Ok(parsed, hash) ->
                use conn = getConnection db
                do! conn.OpenAsync()
                use! tx = conn.BeginTransactionAsync()
                do! MalCatalog.rederiveAnime conn tx parsed raw hash
                do! tx.CommitAsync()
                rederived <- rederived + 1
            | Error _ -> ()

        let! rows = automaticRows db
        let current = rows |> List.map (fun (id, (status, mal, m, reason, _)) -> id, (status, mal, m, reason)) |> Map.ofList
        let mutable unchanged = 0
        let mutable changed = 0
        let mutable missing = 0
        let now = DateTimeOffset.UtcNow

        for seriesId, seasons in rows |> List.groupBy (fun (_, (_, _, _, _, series)) -> series) do
            match! MalCatalog.seriesOfSeason db (fst (List.head seasons)) with
            | None -> ()
            | Some series ->
                let write id decision seen =
                    task {
                        let status, mal, m, reason = shape decision

                        let same =
                            match current.TryFind id with
                            | Some(s, cm, cmeth, creason) -> s = status && cm = mal && cmeth = m && (status = "matched" || creason = reason)
                            | None -> false

                        if same then
                            unchanged <- unchanged + 1
                        else
                            let! _ = MalCatalog.writeDecision db id decision seen now
                            changed <- changed + 1
                    }

                let onDeferred _ _ =
                    task { missing <- missing + 1 }

                let! _ =
                    Enrichment.resolveAndWrite db (JikanClient.Stored db) series (seasons |> List.map fst) write onDeferred

                ignore seriesId

        return
            { AnimeRederived = rederived
              Unchanged = unchanged
              Changed = changed
              DataMissing = missing }
    }

/// `mal-rebuild`
let run (db: DbConfig) (_: string list) : Task<int> =
    task {
        let! c = rebuild db

        printfn
            "re-derived %d anime; matches: %d unchanged, %d changed, %d left (data not held)"
            c.AnimeRederived
            c.Unchanged
            c.Changed
            c.DataMissing

        return 0
    }
