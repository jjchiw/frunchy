/// Taste compatibility between two users (feature 011), shared by the dashboard card and the compare
/// page the way `MalActions` is shared. `load` runs two SQL statements; everything else is pure F#.
/// Nothing is stored: the result follows the watch history and the MyAnimeList matches.
///
/// The score is symmetric (viewer and owner can swap and the level stays the same) and is never shown:
/// views read `Level` and the sections only.
module FrunchyBackend.Infrastructure.Compatibility

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Model (specs/011-taste-compatibility/data-model.md)
// ---------------------------------------------------------------------------

/// Both people need this many fully watched episodes (with a known length) for a level to show.
/// It mirrors `Handlers.Users.Dna.minimumEpisodes`, which Infrastructure can't reference.
let minimumEpisodes = 15L

type SeriesFacts =
    { Title: string
      /// Minutes watched, a partial episode up to its playhead (the dashboard's rule), in ms
      Ms: int64
      FullyWatched: int64 }

type Person =
    { Username: string
      DisplayName: string
      /// Fully watched episodes with a known length
      FullyWatched: int64
      /// Every series with an episode of known length, by Crunchyroll series id
      Series: Map<string, SeriesFacts>
      /// Deduplicated ms per MyAnimeList genre (matched seasons only; the DNA page's rule)
      GenreMs: Map<string, int64>
      /// Deduplicated ms per studio, with the series ids it covers
      StudioMs: Map<string, int64 * Set<string>>
      /// Deduplicated ms of matched seasons
      MatchedMs: int64
      /// Deduplicated ms of all seasons
      DedupMs: int64 }

/// Whose history is long enough for a level
type Enough =
    | Both
    | ViewerShort
    | OwnerShort
    | BothShort

type SharedSeries =
    { SeriesId: string
      /// The profile owner's title (the page links to the owner's anime pages)
      Title: string
      ViewerEpisodes: int64
      OwnerEpisodes: int64
      ViewerMs: int64
      OwnerMs: int64 }

/// A genre of the comparison with both people's shares, in percent with 1 decimal
type GenreRow =
    { Name: string
      ViewerShare: decimal
      OwnerShare: decimal }

/// A series of the owner's that the viewer never watched
type TheirSeries =
    { SeriesId: string
      Title: string
      /// The owner's fully watched episodes
      Episodes: int64
      OwnerMs: int64
      /// MyAnimeList score of the series' most watched matched season
      MalScore: decimal option
      /// Up to 3 genres of that season
      Genres: string list }

type SharedStudio =
    { Name: string
      /// Shared series that both people watched through this studio
      Series: int
      /// Summed deduplicated ms of both people
      Ms: int64 }

type Comparison =
    { Viewer: Person
      Owner: Person
      Enough: Enough
      /// 0..1, None unless both histories are long enough. Internal: never print it.
      Score: float option
      /// 1..5, None unless both histories are long enough
      Level: int option
      /// False when either side has no matched genres: the score then rests on shared series alone
      GenresCompared: bool
      /// Every shared series, most watched by both first (the card takes 4, the page 10)
      Shared: SharedSeries list
      /// Up to 3 genres in both people's top 5
      SharedGenres: string list
      /// Up to 6 genres, for the page
      GenreRows: GenreRow list
      /// Up to 8 owner series the viewer never watched
      Theirs: TheirSeries list
      /// Studios both watch, most shared series first (the card takes 1, the page 5)
      Studios: SharedStudio list }

// ---------------------------------------------------------------------------
// Pure functions (research R3, R4)
// ---------------------------------------------------------------------------

/// Cosine similarity of two minutes-per-genre vectors over the union of their genres, 0..1.
/// 0 when either vector is empty (the score then falls back to shared series; see `score`).
let cosine (a: Map<string, int64>) (b: Map<string, int64>) : float =
    let dot =
        a
        |> Map.fold
            (fun acc name x ->
                match b.TryFind name with
                | Some y -> acc + float x * float y
                | None -> acc)
            0.0

    let norm (m: Map<string, int64>) = m |> Map.fold (fun acc _ x -> acc + float x * float x) 0.0
    let denominator = sqrt (norm a * norm b)

    if denominator = 0.0 then 0.0 else min 1.0 (dot / denominator)

/// The shared-series signal: series both watched, measured against the smaller history. 0 when a side is empty.
let sharedSignal (a: Set<string>) (b: Set<string>) : float =
    let smaller = min a.Count b.Count

    if smaller = 0 then
        0.0
    else
        float (Set.intersect a b).Count / float smaller

/// The compatibility score, 0..1: the average of the shared-series signal and the genre cosine, or the
/// shared-series signal alone when either genre map is empty. Symmetric in its two sides.
let score (seriesA: Set<string>) (genresA: Map<string, int64>) (seriesB: Set<string>) (genresB: Map<string, int64>) : float =
    let s = sharedSignal seriesA seriesB

    if genresA.IsEmpty || genresB.IsEmpty then
        s
    else
        (s + cosine genresA genresB) / 2.0

/// 1 Low below 0.2, 2 Some below 0.4, 3 Good below 0.6, 4 High below 0.8, 5 Very high from 0.8
let level (score: float) : int =
    if score < 0.2 then 1
    elif score < 0.4 then 2
    elif score < 0.6 then 3
    elif score < 0.8 then 4
    else 5

/// A person's share of genre minutes per genre, in percent (unrounded): the DNA page's rule (over the sum of
/// genre minutes, so a person's shares add up to 100 and match their DNA page)
let genreShares (genreMs: Map<string, int64>) : Map<string, decimal> =
    let total = genreMs |> Map.fold (fun acc _ ms -> acc + ms) 0L

    if total <= 0L then
        Map.empty
    else
        genreMs |> Map.map (fun _ ms -> 100m * decimal ms / decimal total)

let private topNames (n: int) (shares: Map<string, decimal>) =
    shares |> Map.toList |> List.sortBy (fun (name, share) -> -share, name) |> List.truncate n |> List.map fst

let private shareOf (shares: Map<string, decimal>) (name: string) =
    shares.TryFind name |> Option.defaultValue 0m

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

/// Genres in both people's top 5 by share, ordered by the sum of shares, up to 3
let sharedGenres (a: Map<string, decimal>) (b: Map<string, decimal>) : string list =
    let inB = set (topNames 5 b)

    topNames 5 a
    |> List.filter inB.Contains
    |> List.sortBy (fun name -> -(shareOf a name + shareOf b name), name)
    |> List.truncate 3

/// The union of each person's top 4 genres, ordered by the sum of shares, up to 6 (viewer first, owner second)
let genreRows (viewer: Map<string, decimal>) (owner: Map<string, decimal>) : GenreRow list =
    Set.union (set (topNames 4 viewer)) (set (topNames 4 owner))
    |> Set.toList
    |> List.sortBy (fun name -> -(shareOf viewer name + shareOf owner name), name)
    |> List.truncate 6
    |> List.map (fun name ->
        { Name = name
          ViewerShare = round1 (shareOf viewer name)
          OwnerShare = round1 (shareOf owner name) })

/// Series both watched, by the smaller of the two people's minutes (descending), then title, then id
let sharedSeries (viewer: Person) (owner: Person) : SharedSeries list =
    [ for KeyValue(id, theirs) in owner.Series do
          match viewer.Series.TryFind id with
          | Some mine ->
              { SeriesId = id
                Title = theirs.Title
                ViewerEpisodes = mine.FullyWatched
                OwnerEpisodes = theirs.FullyWatched
                ViewerMs = mine.Ms
                OwnerMs = theirs.Ms }
          | None -> () ]
    |> List.sortBy (fun s -> -(min s.ViewerMs s.OwnerMs), s.Title, s.SeriesId)

/// The owner's series the viewer never watched, by the owner's minutes, up to 8. MyAnimeList details come from `load`.
let theirSeries (viewer: Person) (owner: Person) : TheirSeries list =
    [ for KeyValue(id, theirs) in owner.Series do
          if not (viewer.Series.ContainsKey id) then
              { SeriesId = id
                Title = theirs.Title
                Episodes = theirs.FullyWatched
                OwnerMs = theirs.Ms
                MalScore = None
                Genres = [] } ]
    |> List.sortBy (fun s -> -s.OwnerMs, s.Title, s.SeriesId)
    |> List.truncate 8

/// Studios in both people's studio rows, by the number of shared series they cover, then summed minutes.
/// A series is covered when both people's matched seasons of it list the studio.
let sharedStudios (viewer: Person) (owner: Person) : SharedStudio list =
    let shared = set [ for KeyValue(id, _) in owner.Series do if viewer.Series.ContainsKey id then id ]

    [ for KeyValue(name, (ownerMs, ownerSeries)) in owner.StudioMs do
          match viewer.StudioMs.TryFind name with
          | Some(viewerMs, viewerSeries) ->
              { Name = name
                Series = Set.intersect shared (Set.intersect viewerSeries ownerSeries) |> Set.count
                Ms = viewerMs + ownerMs }
          | None -> () ]
    |> List.sortBy (fun s -> -s.Series, -s.Ms, s.Name)
    |> List.truncate 5

let private enough (viewer: Person) (owner: Person) =
    match viewer.FullyWatched >= minimumEpisodes, owner.FullyWatched >= minimumEpisodes with
    | true, true -> Both
    | false, true -> ViewerShort
    | true, false -> OwnerShort
    | false, false -> BothShort

/// Everything but the MyAnimeList details of `Theirs`, from two people's figures. Symmetric score.
let ofPeople (viewer: Person) (owner: Person) : Comparison =
    let enough = enough viewer owner
    let seriesOf (p: Person) = p.Series |> Map.toSeq |> Seq.map fst |> Set.ofSeq

    if enough <> Both then
        { Viewer = viewer
          Owner = owner
          Enough = enough
          Score = None
          Level = None
          GenresCompared = false
          Shared = []
          SharedGenres = []
          GenreRows = []
          Theirs = []
          Studios = [] }
    else
        let value = score (seriesOf viewer) viewer.GenreMs (seriesOf owner) owner.GenreMs
        let viewerShares = genreShares viewer.GenreMs
        let ownerShares = genreShares owner.GenreMs

        { Viewer = viewer
          Owner = owner
          Enough = enough
          Score = Some value
          Level = Some(level value)
          GenresCompared = not viewer.GenreMs.IsEmpty && not owner.GenreMs.IsEmpty
          Shared = sharedSeries viewer owner
          SharedGenres = sharedGenres viewerShares ownerShares
          GenreRows = genreRows viewerShares ownerShares
          Theirs = theirSeries viewer owner
          Studios = sharedStudios viewer owner }

// ---------------------------------------------------------------------------
// Data access (research R2, R4). The `w` and `dna` CTEs copy the DNA page's: minutes per episode are the
// whole duration when fully watched, else up to the playhead; episodes without a duration don't count;
// a dub and its original count once per episode number (keyed on the original season) in `dna`.
// ---------------------------------------------------------------------------

/// One row of statement 1: (user id, kind, name, text, ms, n)
type private Row = Guid * string * string option * string option * int64 option * int64 option

let private emptyPerson =
    { Username = ""
      DisplayName = ""
      FullyWatched = 0L
      Series = Map.empty
      GenreMs = Map.empty
      StudioMs = Map.empty
      MatchedMs = 0L
      DedupMs = 0L }

/// Statement 1, for both users at once: who they are, their series, genres, studios and coverage
let private loadPeople (connection: NpgsqlConnection) (users: Guid[]) : Task<Map<Guid, Person>> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH w AS (
                    SELECT w.user_id, e.id, e.series_id, e.series_title, e.episode_number, e.season_id,
                           w.fully_watched,
                           CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END AS ms,
                           m.mal_id, coalesce(m.shared_from, e.season_id) AS original_season
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    WHERE w.user_id = ANY(@users) AND e.duration_ms IS NOT NULL
                ),
                dna AS (
                    SELECT DISTINCT ON (user_id, dedup_key) *
                    FROM (SELECT w.*, CASE WHEN w.episode_number IS NULL OR w.original_season IS NULL THEN w.id
                                          ELSE w.original_season || '#' || w.episode_number END AS dedup_key
                          FROM w) k
                    ORDER BY user_id, dedup_key, ms DESC, id
                )
                SELECT u.id, 'user'::text, u.username, u.display_name, NULL::bigint, NULL::bigint
                FROM users u WHERE u.id = ANY(@users)
                UNION ALL
                SELECT x.user_id, 'series', x.series_id, coalesce(max(s.title), min(x.series_title), x.series_id),
                       sum(x.ms)::bigint, count(*) FILTER (WHERE x.fully_watched)
                FROM w x LEFT JOIN cr_series s ON s.id = x.series_id
                GROUP BY x.user_id, x.series_id
                UNION ALL
                SELECT d.user_id, 'coverage', NULL, NULL,
                       coalesce(sum(d.ms) FILTER (WHERE d.mal_id IS NOT NULL), 0)::bigint,
                       coalesce(sum(d.ms), 0)::bigint
                FROM dna d GROUP BY d.user_id
                UNION ALL
                SELECT d.user_id, 'genre', tg.name, NULL, sum(d.ms)::bigint, NULL
                FROM dna d
                JOIN mal_anime_tags at ON at.mal_id = d.mal_id AND at.kind = 'genre'
                JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                GROUP BY d.user_id, tg.name
                UNION ALL
                SELECT d.user_id, 'studio', tg.name, d.series_id, sum(d.ms)::bigint, NULL
                FROM dna d
                JOIN mal_anime_tags at ON at.mal_id = d.mal_id AND at.kind = 'studio'
                JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                GROUP BY d.user_id, tg.name, d.series_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("users", users) |> ignore

        let rows = ResizeArray<Row>()

        use! r = cmd.ExecuteReaderAsync()
        let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
        let int64' (i: int) = if r.IsDBNull i then None else Some(r.GetInt64 i)

        while r.Read() do
            rows.Add((r.GetGuid 0, r.GetString 1, text 2, text 3, int64' 4, int64' 5))

        let person (userId: Guid) =
            let mine = rows |> Seq.filter (fun (u, _, _, _, _, _) -> u = userId) |> List.ofSeq
            let ofKind kind = mine |> List.filter (fun (_, k, _, _, _, _) -> k = kind)

            let username, displayName =
                match ofKind "user" with
                | (_, _, Some username, Some display, _, _) :: _ -> username, display
                | _ -> "", ""

            let series =
                [ for _, _, id, title, ms, n in ofKind "series" do
                      match id, title with
                      | Some id, Some title ->
                          yield
                              id,
                              { Title = title
                                Ms = defaultArg ms 0L
                                FullyWatched = defaultArg n 0L }
                      | _ -> () ]
                |> Map.ofList

            let genres =
                [ for _, _, name, _, ms, _ in ofKind "genre" do
                      match name, ms with
                      | Some name, Some ms -> yield name, ms
                      | _ -> () ]
                |> Map.ofList

            let studios =
                [ for _, _, name, seriesId, ms, _ in ofKind "studio" do
                      match name, seriesId, ms with
                      | Some name, Some seriesId, Some ms -> yield name, (ms, seriesId)
                      | _ -> () ]
                |> List.groupBy fst
                |> List.map (fun (name, xs) -> name, (xs |> List.sumBy (fun (_, (ms, _)) -> ms), xs |> List.map (fun (_, (_, id)) -> id) |> Set.ofList))
                |> Map.ofList

            let matched, dedup =
                match ofKind "coverage" with
                | (_, _, _, _, Some matched, Some dedup) :: _ -> matched, dedup
                | _ -> 0L, 0L

            { emptyPerson with
                Username = username
                DisplayName = displayName
                FullyWatched = series |> Map.fold (fun acc _ s -> acc + s.FullyWatched) 0L
                Series = series
                GenreMs = genres
                StudioMs = studios
                MatchedMs = matched
                DedupMs = dedup }

        return users |> Array.map (fun id -> id, person id) |> Map.ofArray
    }

/// Statement 2: the MyAnimeList score and up to 3 genres of the most watched matched season of each
/// of the owner's series (the anime page's poster rule), for the given series only
let private loadTheirDetails (connection: NpgsqlConnection) (owner: Guid) (seriesIds: string[]) : Task<Map<string, decimal option * string list>> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH per AS (
                    SELECT e.series_id, m.mal_id,
                           sum(CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                    ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END) AS ms
                    FROM watch_events w
                    JOIN cr_episodes e ON e.id = w.cr_episode_id
                    JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                    WHERE w.user_id = @owner AND e.duration_ms IS NOT NULL AND e.series_id = ANY(@series)
                    GROUP BY e.series_id, m.mal_id
                ),
                best AS (
                    SELECT DISTINCT ON (series_id) series_id, mal_id FROM per ORDER BY series_id, ms DESC, mal_id
                )
                SELECT b.series_id, a.score,
                       array(SELECT t.name FROM mal_anime_tags at
                             JOIN mal_tags t ON t.kind = at.kind AND t.tag_id = at.tag_id
                             WHERE at.mal_id = a.mal_id AND at.kind = 'genre'
                             ORDER BY at.position, t.name LIMIT 3)
                FROM best b JOIN mal_anime a ON a.mal_id = b.mal_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("owner", owner) |> ignore
        cmd.Parameters.AddWithValue("series", seriesIds) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let details = ResizeArray()

        while r.Read() do
            details.Add(r.GetString 0, (if r.IsDBNull 1 then None else Some(r.GetDecimal 1)), List.ofArray (r.GetFieldValue<string[]> 2))

        return details |> Seq.map (fun (id, score, genres) -> id, (score, genres)) |> Map.ofSeq
    }

/// Compare the viewer with the profile owner. Both ids are real users; the viewer's id comes from the session.
let load (db: DbConfig) (viewer: Guid) (owner: Guid) : Task<Comparison> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        let! people = loadPeople connection [| viewer; owner |]
        let comparison = ofPeople (people.TryFind viewer |> Option.defaultValue emptyPerson) (people.TryFind owner |> Option.defaultValue emptyPerson)

        if comparison.Theirs.IsEmpty then
            return comparison
        else
            let! details = loadTheirDetails connection owner (comparison.Theirs |> List.map _.SeriesId |> Array.ofList)

            return
                { comparison with
                    Theirs =
                        comparison.Theirs
                        |> List.map (fun s ->
                            match details.TryFind s.SeriesId with
                            | Some(score, genres) -> { s with MalScore = score; Genres = genres }
                            | None -> s) }
    }
