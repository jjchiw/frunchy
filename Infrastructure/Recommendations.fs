/// What to watch next (feature 025): for the signed-in viewer, the series the people with the most similar taste
/// watched through and the viewer never started. "Similar" is feature 011's compatibility level; "people" are the
/// ones the viewer may see by the same pure rule as every `/u/` page (`Profiles.canView`). Nothing is stored.
///
/// The score is internal, as in `Compatibility`: the views read `Level`, `Finishers` and the series only.
module FrunchyBackend.Infrastructure.Recommendations

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.Profiles

// ---------------------------------------------------------------------------
// Model (specs/025-what-to-watch-next/what-to-watch-next.spec.md)
// ---------------------------------------------------------------------------

/// Other people compared with the viewer: the ones sharing the most series (then username)
let maxCandidates = 50

/// A match reaches at least this level ("Good")
let minimumLevel = 3

/// The best matches by score kept
let maxMatches = 10

/// Series on the page
let maxSeries = 12

/// Finishers named on a series before "and N more"
let shownFinishers = 3

/// A person who shares a series with the viewer and whose history is long enough, with what the visibility rule needs
type Candidate =
    { Access: OwnerAccess
      Username: string
      /// Series (of known length) shared with the viewer
      Shared: int }

/// A visible person at level `minimumLevel` or more. `Score` is internal: never print it.
type Match =
    { UserId: Guid
      Username: string
      DisplayName: string
      Level: int
      Score: float
      /// For the series titles
      Person: Compatibility.Person }

/// A match who watched a series through
type Finisher =
    { Username: string
      DisplayName: string
      Level: int }

type Recommendation =
    { SeriesId: string
      Title: string
      /// Every match who watched it through, best first (the page names `shownFinishers` of them)
      Finishers: Finisher list
      MalScore: decimal option
      Genres: string list }

type Result =
    /// The viewer has fewer than `Compatibility.minimumEpisodes` fully watched episodes
    | NotEnough
    /// Nobody the viewer can see reaches `minimumLevel`
    | NoMatches
    /// Matches exist (their count) but watched through nothing the viewer hasn't started
    | NoSeries of matchCount: int
    | Series of matchCount: int * Recommendation list

// ---------------------------------------------------------------------------
// Pure functions
// ---------------------------------------------------------------------------

/// The candidates the viewer may see (`Profiles.canView`: the viewer, this browser's grants, no exception for
/// operators), the ones sharing the most series first, then username, up to `maxCandidates`
let visibleCandidates (viewer: Guid) (grants: Grant list) (candidates: Candidate list) : Candidate list =
    candidates
    |> List.filter (fun c -> c.Access.UserId <> viewer && canView (Some viewer) grants c.Access)
    |> List.sortBy (fun c -> -c.Shared, c.Username)
    |> List.truncate maxCandidates

/// The matches among the people compared with the viewer: level `minimumLevel` or more by `Compatibility.ofPeople`,
/// best score first (then username), up to `maxMatches`
let matches (viewer: Compatibility.Person) (people: (Guid * Compatibility.Person) list) : Match list =
    [ for userId, person in people do
          let c = Compatibility.ofPeople viewer person

          match c.Level, c.Score with
          | Some level, Some score when level >= minimumLevel ->
              yield
                  { UserId = userId
                    Username = person.Username
                    DisplayName = person.DisplayName
                    Level = level
                    Score = score
                    Person = person }
          | _ -> () ]
    |> List.sortBy (fun m -> -m.Score, m.Username)
    |> List.truncate maxMatches

/// The series of the matches' watched-through facts, ranked: per series the best score among the matches who
/// watched it through (descending), then how many did (descending), then title, then series id; up to `maxSeries`.
/// `through` holds (match id, series id). Finishers come best first (score, then username).
let recommend (found: Match list) (through: (Guid * string) list) : Recommendation list =
    let byId = found |> List.map (fun m -> m.UserId, m) |> dict

    through
    |> List.filter (fun (userId, _) -> byId.ContainsKey userId)
    |> List.distinct
    |> List.groupBy snd
    |> List.map (fun (seriesId, pairs) ->
        let finishers = pairs |> List.map (fun (userId, _) -> byId[userId]) |> List.sortBy (fun m -> -m.Score, m.Username)
        let best = List.head finishers

        let title =
            match best.Person.Series.TryFind seriesId with
            | Some facts -> facts.Title
            | None -> seriesId

        -best.Score,
        -finishers.Length,
        title,
        seriesId,
        { SeriesId = seriesId
          Title = title
          Finishers =
            finishers
            |> List.map (fun m ->
                { Username = m.Username
                  DisplayName = m.DisplayName
                  Level = m.Level })
          MalScore = None
          Genres = [] })
    |> List.sortBy (fun (score, count, title, id, _) -> score, count, title, id)
    |> List.truncate maxSeries
    |> List.map (fun (_, _, _, _, r) -> r)

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// SQL 1: other users with at least `Compatibility.minimumEpisodes` fully watched episodes of known length who share
/// a series (of known length) with the viewer, with the two columns of the visibility rule. The rule itself runs in F#.
let private loadCandidates (connection: NpgsqlConnection) (settings: ProfileSettings) (viewer: Guid) : Task<Candidate list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH mine AS (
                    SELECT DISTINCT e.series_id
                    FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id = @viewer AND e.duration_ms IS NOT NULL
                ),
                shared AS (
                    SELECT w.user_id, count(DISTINCT e.series_id) AS n
                    FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id <> @viewer AND e.duration_ms IS NOT NULL
                      AND e.series_id IN (SELECT series_id FROM mine)
                    GROUP BY w.user_id
                )
                SELECT u.id, u.username, p.visibility, p.share_link_id, sh.n
                FROM shared sh
                JOIN users u ON u.id = sh.user_id
                LEFT JOIN profile_settings p ON p.user_id = u.id
                WHERE (SELECT count(*) FROM watch_events w2 JOIN cr_episodes e2 ON e2.id = w2.cr_episode_id
                       WHERE w2.user_id = u.id AND w2.fully_watched AND e2.duration_ms IS NOT NULL) >= @minimum
                """,
                connection
            )

        cmd.Parameters.AddWithValue("viewer", viewer) |> ignore
        cmd.Parameters.AddWithValue("minimum", Compatibility.minimumEpisodes) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let candidates = ResizeArray<Candidate>()

        while r.Read() do
            let choice = if r.IsDBNull 2 then None else Visibility.tryParse (r.GetString 2)

            candidates.Add
                { Access =
                    { UserId = r.GetGuid 0
                      Choice = choice
                      Visibility = effective settings.DefaultVisibility choice
                      LinkId = if r.IsDBNull 3 then None else Some(r.GetGuid 3) }
                  Username = r.GetString 1
                  Shared = int (r.GetInt64 4) }

        return List.ofSeq candidates
    }

/// SQL 2: (match, series) pairs the match watched through and the viewer never started. A person has watched a series
/// through when, over the seasons of it they watched anything of (a dub season and its original are separate
/// seasons), they fully watched at least 80% of the episodes Frunchy knows (known length) and at least one: integer
/// arithmetic, `full * 100 >= 80 * known`. Any watch event of the viewer on any episode of the series, an unresolved
/// one included, means started.
let private loadWatchedThrough (connection: NpgsqlConnection) (viewer: Guid) (people: Guid[]) : Task<(Guid * string) list> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                WITH seasons AS (
                    SELECT DISTINCT w.user_id, e.series_id, e.season_id
                    FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
                    WHERE w.user_id = ANY(@people) AND e.duration_ms IS NOT NULL
                      AND e.series_id NOT IN (SELECT s.series_id
                                              FROM watch_events v JOIN cr_episodes s ON s.id = v.cr_episode_id
                                              WHERE v.user_id = @viewer)
                ),
                per AS (
                    SELECT s.user_id, s.series_id, e2.id, coalesce(w2.fully_watched, false) AS fw
                    FROM seasons s
                    JOIN cr_episodes e2 ON e2.series_id = s.series_id
                                       AND e2.season_id IS NOT DISTINCT FROM s.season_id
                                       AND e2.duration_ms IS NOT NULL
                    LEFT JOIN watch_events w2 ON w2.user_id = s.user_id AND w2.cr_episode_id = e2.id
                )
                SELECT user_id, series_id
                FROM per
                GROUP BY user_id, series_id
                HAVING count(*) FILTER (WHERE fw) >= 1
                   AND count(*) FILTER (WHERE fw) * 100 >= 80 * count(*)
                """,
                connection
            )

        cmd.Parameters.AddWithValue("viewer", viewer) |> ignore
        cmd.Parameters.AddWithValue("people", people) |> ignore

        use! r = cmd.ExecuteReaderAsync()
        let pairs = ResizeArray<Guid * string>()

        while r.Read() do
            pairs.Add((r.GetGuid 0, r.GetString 1))

        return List.ofSeq pairs
    }

/// The viewer's recommendations. `grants` are this request's share-link grants (`Profiles.readGrants`); `settings`
/// carries the instance's default visibility. The viewer's id comes from the session.
let load (db: DbConfig) (settings: ProfileSettings) (grants: Grant list) (viewer: Guid) : Task<Result> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        let! all = loadCandidates connection settings viewer
        let candidates = visibleCandidates viewer grants all
        let! people = Compatibility.loadPeople connection (Array.append [| viewer |] (candidates |> List.map _.Access.UserId |> Array.ofList))
        let me = people.TryFind viewer |> Option.defaultValue Compatibility.emptyPerson

        if me.FullyWatched < Compatibility.minimumEpisodes then
            return NotEnough
        else
            let found =
                matches
                    me
                    [ for c in candidates do
                          match people.TryFind c.Access.UserId with
                          | Some person -> yield c.Access.UserId, person
                          | None -> () ]

            if found.IsEmpty then
                return NoMatches
            else
                let! through = loadWatchedThrough connection viewer (found |> List.map _.UserId |> Array.ofList)
                let recommended = recommend found through

                if recommended.IsEmpty then
                    return NoSeries found.Length
                else
                    let! details =
                        Compatibility.loadTheirDetails
                            connection
                            (found |> List.map _.UserId |> Array.ofList)
                            (recommended |> List.map _.SeriesId |> Array.ofList)

                    return
                        Series(
                            found.Length,
                            recommended
                            |> List.map (fun r ->
                                match details.TryFind r.SeriesId with
                                | Some(score, genres) -> { r with MalScore = score; Genres = genres }
                                | None -> r)
                        )
    }
