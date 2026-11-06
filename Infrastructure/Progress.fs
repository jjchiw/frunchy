/// Progress (feature 031): per MyAnimeList entry, how much of it a person watched, and whether it is completed,
/// being watched or left unfinished. Computed per request, nothing stored. `classify` is pure; `load` is one literal
/// statement over the person's rows (deduplicated like the DNA page: a dub and its original count once).
module FrunchyBackend.Infrastructure.Progress

open System
open System.Threading.Tasks
open Npgsql

/// An entry whose newest watch is at most this many days old is being watched
[<Literal>]
let WatchingDays = 30

/// A still-airing entry whose newest fully watched episode aired at most this many days ago is being kept up with
[<Literal>]
let AiringKeepUpDays = 21

/// The owner's completed list shows this many entries, the most recently completed; the rest are only counted
[<Literal>]
let CompletedListed = 12

/// One started show: a MyAnimeList entry (an anime season) with at least one fully watched episode
type Entry =
    { MalId: int
      /// The Crunchyroll series of the newest watch: the title, the public series page and the Crunchyroll link
      SeriesId: string
      Title: string
      /// The title of the Crunchyroll season, to tell two entries of one series apart
      SeasonTitle: string option
      Slug: string option
      Poster: string option
      /// Fully watched episodes, a dub and its original once
      Watched: int
      /// MyAnimeList's episode count when it knows one (more than 0)
      Total: int option
      /// MyAnimeList says the entry is still airing
      Airing: bool
      /// The newest watch of the entry, a partly watched episode included
      LastWatch: DateTimeOffset
      /// The air date of its newest fully watched episode, when any has one
      NewestAired: DateTimeOffset option
      /// Feature 027's rule for its series: more than 100 episodes on MyAnimeList, or any episode numbered above 100
      LongRunner: bool
      /// The episode after the highest fully watched one, in the Crunchyroll season of the newest watch
      NextNumber: int option
      /// (series id, episode id) of the next episode when the catalog has it with a known length
      NextEpisode: (string * string) option }

type State =
    | Completed
    | Watching
    | Unfinished

/// The entries sorted into the three states
type Classified =
    { /// Every completed entry, not only the listed ones
      CompletedCount: int
      /// The `CompletedListed` most recently completed, newest first
      Completed: Entry list
      Watching: Entry list
      Unfinished: Entry list }

/// Completed: MyAnimeList knows the episode count and the person fully watched at least that many (Crunchyroll
/// sometimes has an extra recap episode). A still-airing or unknown total can't be completed.
let isCompleted (e: Entry) =
    match e.Total with
    | Some total -> e.Watched >= total
    | None -> false

/// The state of a started entry at `now`
let stateOf (now: DateTimeOffset) (e: Entry) : State =
    if isCompleted e then
        Completed
    elif now - e.LastWatch <= TimeSpan.FromDays(float WatchingDays) then
        Watching
    else
        match e.NewestAired with
        | Some aired when e.Airing && now - aired <= TimeSpan.FromDays(float AiringKeepUpDays) -> Watching
        | _ -> Unfinished

/// The next episode's number: one above the highest fully watched, unless the entry's known total says that is past
/// its last episode
let nextNumber (highestWatched: int option) (total: int option) : int option =
    match highestWatched, total with
    | None, _ -> None
    | Some n, Some total when n + 1 > total -> None
    | Some n, _ -> Some(n + 1)

/// Sort the started entries into the three states, each newest watch first (ties by title, then MyAnimeList id, so
/// the page never depends on row order). An entry with no fully watched episode isn't started and is dropped.
let classify (now: DateTimeOffset) (entries: Entry list) : Classified =
    let ordered =
        entries
        |> List.filter (fun e -> e.Watched >= 1)
        |> List.sortBy (fun e -> -e.LastWatch.UtcTicks, e.Title, e.MalId)

    let inState state = ordered |> List.filter (fun e -> stateOf now e = state)
    let completed = inState Completed

    { CompletedCount = completed.Length
      Completed = completed |> List.truncate CompletedListed
      Watching = inState Watching
      Unfinished = inState Unfinished }

/// What `load` returns: every started entry with a MyAnimeList match, and how many started Crunchyroll seasons have
/// none (they have no total to compare with, so they are in no count and no list)
type Loaded = { Entries: Entry list; Unmatched: int }

/// One statement, one row shape: 'entry' rows (one per started entry) and one 'unmatched' row whose `watched` is the
/// number of started Crunchyroll seasons without a matched entry. Values go in as parameters only.
/// The person's started shows and the unmatched count. Call it only after the profile gate has passed.
let load (connection: NpgsqlConnection) (userId: Guid) : Task<Loaded> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
            WITH w AS (
                SELECT e.id, e.series_id, e.series_title, e.thumbnail_url, e.episode_number, e.season_id, e.aired_at,
                       w.fully_watched, w.watched_at,
                       CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                            ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END AS ms,
                       m.mal_id, coalesce(m.shared_from, e.season_id) AS original_season
                FROM watch_events w
                JOIN cr_episodes e ON e.id = w.cr_episode_id
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
            ),
            keyed AS (
                SELECT w.*, CASE WHEN w.episode_number IS NULL OR w.original_season IS NULL THEN w.id
                                 ELSE w.original_season || '#' || w.episode_number END AS dedup_key
                FROM w
            ),
            -- A dub and its original are one episode: it aired when its earliest version did, and it was fully watched
            -- when any version was
            timed AS (
                SELECT k.*, min(k.aired_at) OVER (PARTITION BY k.dedup_key) AS group_aired,
                       bool_or(k.fully_watched) OVER (PARTITION BY k.dedup_key) AS group_full
                FROM keyed k
            ),
            dna AS (
                SELECT DISTINCT ON (t.dedup_key) t.*
                FROM timed t
                ORDER BY t.dedup_key, t.ms DESC, t.id
            ),
            entry AS (
                SELECT d.mal_id, count(*) FILTER (WHERE d.group_full) AS watched,
                       max(d.group_aired) FILTER (WHERE d.group_full) AS newest_aired
                FROM dna d WHERE d.mal_id IS NOT NULL
                GROUP BY d.mal_id
                HAVING count(*) FILTER (WHERE d.group_full) >= 1
            ),
            -- The newest watch of the entry over all its rows: its series, and the original Crunchyroll season the next
            -- episode is looked for in
            last_row AS (
                SELECT DISTINCT ON (w.mal_id) w.mal_id, w.watched_at, w.series_id, w.series_title, w.season_id, w.original_season
                FROM w WHERE w.mal_id IS NOT NULL
                ORDER BY w.mal_id, w.watched_at DESC, w.id
            ),
            last_thumb AS (
                SELECT DISTINCT ON (w.mal_id) w.mal_id, w.thumbnail_url
                FROM w WHERE w.mal_id IS NOT NULL AND w.thumbnail_url IS NOT NULL
                ORDER BY w.mal_id, w.watched_at DESC, w.id
            ),
            -- A long runner: a matched season of the series has more episodes than the limit on MyAnimeList, or any known
            -- episode of it is numbered above it (feature 027)
            long_runners AS (
                SELECT e.series_id FROM cr_episodes e
                WHERE e.episode_number > @long_runner_episodes AND e.series_id IN (SELECT series_id FROM w)
                UNION
                SELECT sea.series_id FROM cr_seasons sea
                JOIN cr_season_matches m ON m.season_id = sea.id AND m.status = 'matched'
                JOIN mal_anime a ON a.mal_id = m.mal_id
                WHERE a.episodes > @long_runner_episodes AND sea.series_id IN (SELECT series_id FROM w)
            ),
            -- The highest fully watched episode number in the original season of the newest watch
            top_number AS (
                SELECT l.mal_id, max(d.episode_number) AS top
                FROM last_row l
                JOIN dna d ON d.mal_id = l.mal_id AND d.original_season = l.original_season
                WHERE d.group_full AND d.episode_number IS NOT NULL
                GROUP BY l.mal_id
            ),
            -- The next one in the catalog, with a known length: in that original season or in a dub of it that matches the
            -- same entry, preferring the season of the newest watch
            next_episode AS (
                SELECT DISTINCT ON (t.mal_id) t.mal_id, e.series_id, e.id AS episode_id
                FROM top_number t
                JOIN last_row l ON l.mal_id = t.mal_id
                JOIN cr_season_matches m ON m.mal_id = t.mal_id AND m.status = 'matched'
                                        AND coalesce(m.shared_from, m.season_id) = l.original_season
                JOIN cr_episodes e ON e.season_id = m.season_id AND e.episode_number = t.top + 1 AND e.duration_ms IS NOT NULL
                ORDER BY t.mal_id, (m.season_id = l.season_id) DESC, e.id
            ),
            unmatched AS (
                SELECT count(DISTINCT coalesce(d.original_season, d.series_id)) AS seasons
                FROM dna d WHERE d.group_full AND d.mal_id IS NULL
            )
            SELECT 'entry'::text, en.mal_id, l.series_id,
                   coalesce(s.title, l.series_title, l.series_id) AS title,
                   sea.title AS season_title,
                   coalesce(a.image_url, lt.thumbnail_url) AS poster,
                   en.watched::bigint, a.episodes, coalesce(a.status = 'currently_airing', false),
                   l.watched_at, en.newest_aired,
                   EXISTS (SELECT 1 FROM long_runners lr WHERE lr.series_id = l.series_id),
                   t.top, ne.series_id, ne.episode_id, s.slug
            FROM entry en
            JOIN last_row l ON l.mal_id = en.mal_id
            JOIN mal_anime a ON a.mal_id = en.mal_id
            LEFT JOIN cr_series s ON s.id = l.series_id
            LEFT JOIN cr_seasons sea ON sea.id = l.original_season
            LEFT JOIN last_thumb lt ON lt.mal_id = en.mal_id
            LEFT JOIN top_number t ON t.mal_id = en.mal_id
            LEFT JOIN next_episode ne ON ne.mal_id = en.mal_id
            UNION ALL
            SELECT 'unmatched', NULL, NULL, NULL, NULL, NULL, (SELECT seasons FROM unmatched), NULL, NULL, NULL, NULL, NULL,
                   NULL, NULL, NULL, NULL
                """,
                connection
            )
        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.AddWithValue("long_runner_episodes", Seasons.LongRunnerEpisodes) |> ignore

        use! r = cmd.ExecuteReaderAsync()

        let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
        let int' (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)
        let when' (i: int) = if r.IsDBNull i then None else Some(r.GetFieldValue<DateTimeOffset> i)

        let entries = ResizeArray<Entry>()
        let mutable unmatched = 0

        while r.Read() do
            if r.GetString 0 = "unmatched" then
                unmatched <- int (r.GetInt64 6)
            else
                let total = int' 7 |> Option.filter (fun n -> n > 0)
                let highest = int' 12

                entries.Add
                    { MalId = r.GetInt32 1
                      SeriesId = r.GetString 2
                      Title = r.GetString 3
                      SeasonTitle = text 4
                      Slug = text 15
                      Poster = text 5
                      Watched = int (r.GetInt64 6)
                      Total = total
                      Airing = r.GetBoolean 8
                      LastWatch = r.GetFieldValue<DateTimeOffset> 9
                      NewestAired = when' 10
                      LongRunner = r.GetBoolean 11
                      NextNumber = nextNumber highest total
                      NextEpisode =
                        match text 13, text 14 with
                        | Some seriesId, Some episodeId when (nextNumber highest total).IsSome -> Some(seriesId, episodeId)
                        | _ -> None }

        return
            { Entries = List.ofSeq entries
              Unmatched = unmatched }
    }
