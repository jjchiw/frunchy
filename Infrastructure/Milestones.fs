/// Milestones (feature 029): six tracks with fixed tiers, computed per request from one user's `watch_events` and the
/// catalog. Nothing is stored, so re-sent, reordered and rebuilt histories give the same result by construction.
/// `compute` is pure; `load` runs the one statement that feeds it.
module FrunchyBackend.Infrastructure.Milestones

open System
open System.Threading.Tasks
open Npgsql

/// The tracks, in the order the dashboard shows them, with their ascending thresholds (episodes, hours, series, days,
/// episodes in one day, episodes of one series)
let tracks: (string * int list) list =
    [ "episodes", [ 10; 50; 100; 250; 500; 1000; 2500; 5000 ]
      "hours", [ 24; 100; 250; 500; 1000; 2500 ]
      "series", [ 5; 10; 25; 50; 100; 250 ]
      "streak", [ 3; 7; 14; 30; 100; 365 ]
      "marathon", [ 6; 12; 24; 48 ]
      "long-run", [ 24; 50; 100; 250; 500; 1000 ] ]

/// Every tier of every track (36)
let totalTiers = tracks |> List.sumBy (fun (_, thresholds) -> thresholds.Length)

/// One watch event of known length; `WatchedAt` is UTC
type WatchRow =
    { WatchedAt: DateTime
      EpisodeId: string
      FullyWatched: bool
      /// The dashboard's rule: the length when fully watched, else the playhead capped at the length
      WatchedMs: int64
      SeriesId: string
      SeriesTitle: string
      /// The DNA key: a dub and its original share it
      DedupKey: string }

type Track =
    { Key: string
      /// Episodes, series, days, episodes in a day or in one series; hours with the dashboard's one decimal
      Value: decimal
      /// The highest threshold reached
      Tier: int option
      /// When the reached tier was reached (the instant of the watch that made it, UTC)
      ReachedAt: DateTime option
      /// The first threshold above the value; None at the top
      Next: int option
      /// What is missing to `Next` (hours rounded up to one decimal, so it is never 0 while the tier is not reached)
      Remaining: decimal
      /// `long-run` only: the series with the most episodes (id, title); None when the value is 0
      Series: (string * string) option }

let private msPerHour = 3_600_000L

/// Rows in the order every date is derived from
let private ordered (rows: WatchRow list) =
    rows
    |> List.sortWith (fun a b ->
        match compare a.WatchedAt b.WatchedAt with
        | 0 -> String.CompareOrdinal(a.EpisodeId, b.EpisodeId)
        | c -> c)

/// The highest threshold the `value` meets, and the first one above it
let private tiers (thresholds: int list) (meets: int -> bool) =
    let reached = thresholds |> List.filter meets |> List.tryLast
    let next = thresholds |> List.tryFind (fun t -> not (meets t))
    reached, next

/// A track from `valueAt`: the instant the N-th unit was reached, for a threshold N (None when it was not)
let private build (key: string) (value: decimal) (remaining: int -> decimal) (reachedAt: int -> DateTime option) (meets: int -> bool) =
    let thresholds = tracks |> List.find (fun (k, _) -> k = key) |> snd
    let tier, next = tiers thresholds meets

    { Key = key
      Value = value
      Tier = tier
      ReachedAt = tier |> Option.bind reachedAt
      Next = next
      Remaining = (match next with Some n -> remaining n | None -> 0m)
      Series = None }

/// An integer-valued track: `instants` holds, in order, the instant each unit of the value was reached
let private counted (key: string) (instants: DateTime list) =
    let arr = Array.ofList instants
    let value = arr.Length

    build key (decimal value) (fun n -> decimal (n - value)) (fun n -> Some arr[n - 1]) (fun n -> value >= n)

let private utcDay (at: DateTime) = at.Date

/// The six milestones of a history. Pure and independent of the order of `rows`.
let compute (rows: WatchRow list) : Track list =
    let all = ordered rows
    let full = all |> List.filter (fun r -> r.FullyWatched)

    // episodes: the watched_at of the N-th fully watched episode
    let episodes = counted "episodes" (full |> List.map (fun r -> r.WatchedAt))

    // hours: the row whose running sum of watched milliseconds first reaches N hours; the tier test is exact
    let hours =
        let running = all |> List.scan (fun sum r -> sum + r.WatchedMs) 0L |> List.tail |> Array.ofList
        let total = if running.Length = 0 then 0L else running[running.Length - 1]
        let meets n = total >= int64 n * msPerHour
        let dated = Array.ofList all

        let reachedAt n =
            let at = running |> Array.findIndex (fun sum -> sum >= int64 n * msPerHour)
            Some dated[at].WatchedAt

        let remaining n =
            Math.Ceiling(decimal (int64 n * msPerHour - total) / decimal msPerHour * 10m) / 10m

        build "hours" (Math.Round(decimal total / decimal msPerHour, 1, MidpointRounding.AwayFromZero)) remaining reachedAt meets

    // series: the instant each distinct series got its first fully watched episode
    let series =
        let firsts =
            full
            |> List.fold
                (fun (seen: Set<string>, acc: DateTime list) r ->
                    if seen.Contains r.SeriesId then seen, acc else seen.Add r.SeriesId, r.WatchedAt :: acc)
                (Set.empty, [])
            |> snd
            |> List.rev

        counted "series" firsts

    // streak: runs of consecutive UTC days with a fully watched episode; the date is the first watch of the run's N-th day
    let streak =
        let days = full |> List.map (fun r -> utcDay r.WatchedAt) |> List.distinct // already in order
        let firstOfDay = full |> List.groupBy (fun r -> utcDay r.WatchedAt) |> Map.ofList

        let runs =
            days
            |> List.fold
                (fun (runs: DateTime list list) day ->
                    match runs with
                    | (last :: _ as run) :: rest when day = last.AddDays 1.0 -> (day :: run) :: rest
                    | _ -> [ day ] :: runs)
                []
            |> List.map List.rev
            |> List.rev

        let longest = runs |> List.fold (fun best run -> max best run.Length) 0

        let reachedAt n =
            runs
            |> List.tryFind (fun run -> run.Length >= n)
            |> Option.map (fun run -> (firstOfDay[run[n - 1]] |> List.head).WatchedAt)

        build "streak" (decimal longest) (fun n -> decimal (n - longest)) reachedAt (fun n -> longest >= n)

    // marathon: the first UTC day with N fully watched episodes, at that day's N-th one
    let marathon =
        let perDay = full |> List.groupBy (fun r -> utcDay r.WatchedAt) // days in order, rows in order
        let most = perDay |> List.fold (fun best (_, rs) -> max best rs.Length) 0

        let reachedAt n =
            perDay
            |> List.tryFind (fun (_, rs) -> rs.Length >= n)
            |> Option.map (fun (_, rs) -> rs[n - 1].WatchedAt)

        build "marathon" (decimal most) (fun n -> decimal (n - most)) reachedAt (fun n -> most >= n)

    // long-run: per series, the dedup keys by the earliest fully watched watch of each
    let longRun =
        let perSeries =
            full
            |> List.groupBy (fun r -> r.SeriesId)
            |> List.map (fun (seriesId, rs) ->
                // `rs` is in order, so the first row of a key is its earliest
                let instants = rs |> List.distinctBy (fun r -> r.DedupKey) |> List.map (fun r -> r.WatchedAt) |> Array.ofList
                seriesId, rs[0].SeriesTitle, instants)

        let most = perSeries |> List.fold (fun best (_, _, instants) -> max best instants.Length) 0

        let reachedAt n =
            perSeries
            |> List.choose (fun (_, _, instants) -> if instants.Length >= n then Some instants[n - 1] else None)
            |> function
                | [] -> None
                | instants -> Some(List.min instants)

        let named =
            perSeries
            |> List.filter (fun (_, _, instants) -> instants.Length = most)
            |> List.sortWith (fun (idA, titleA, _) (idB, titleB, _) ->
                match String.Compare(titleA, titleB, StringComparison.OrdinalIgnoreCase) with
                | 0 ->
                    match String.CompareOrdinal(titleA, titleB) with
                    | 0 -> String.CompareOrdinal(idA, idB)
                    | c -> c
                | c -> c)
            |> List.tryHead
            |> Option.filter (fun _ -> most > 0)
            |> Option.map (fun (id, title, _) -> id, title)

        { build "long-run" (decimal most) (fun n -> decimal (n - most)) reachedAt (fun n -> most >= n) with Series = named }

    [ episodes; hours; series; streak; marathon; longRun ]

/// Tiers reached, summed over the tracks
let reached (all: Track list) =
    all
    |> List.sumBy (fun t ->
        match t.Tier with
        | Some tier -> tracks |> List.find (fun (k, _) -> k = t.Key) |> snd |> List.filter (fun x -> x <= tier) |> List.length
        | None -> 0)

/// One statement: a compact row per watch event of known length (the dashboard's own rule), with the DNA key
/// (`coalesce(shared_from, season_id) # episode_number`, else the episode id) built the way the DNA page builds it
let load (connection: NpgsqlConnection) (userId: Guid) : Task<Track list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT w.watched_at, e.id, w.fully_watched,
                       CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                            ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END,
                       e.series_id, coalesce(e.series_title, s.title, e.series_id),
                       CASE WHEN e.episode_number IS NULL OR coalesce(m.shared_from, e.season_id) IS NULL THEN e.id
                            ELSE coalesce(m.shared_from, e.season_id) || '#' || e.episode_number END
                FROM watch_events w
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_series s ON s.id = e.series_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                """,
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<WatchRow>()

        while r.Read() do
            rows.Add
                { WatchedAt = DateTime.SpecifyKind(r.GetDateTime 0, DateTimeKind.Utc)
                  EpisodeId = r.GetString 1
                  FullyWatched = r.GetBoolean 2
                  WatchedMs = r.GetInt64 3
                  SeriesId = r.GetString 4
                  SeriesTitle = r.GetString 5
                  DedupKey = r.GetString 6 }

        return compute (List.ofSeq rows)
    }
