/// Feature 015 (US1, FR-001): Megáfono's actor lookup, by handle and by id. Served only with federation on
/// (Program registers it then; with federation off the routes are absent and answer the plain 404). The
/// bearer token is compared in constant time; Megáfono caches a positive answer for 5 minutes.
module FrunchyBackend.Handlers.Megafono.Actors

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract: the actor JSON (specs/015-fediverse/contracts/frunchy-megafono.md)
// ---------------------------------------------------------------------------

type private Actor =
    { Id: string
      Handle: string
      Name: string
      ProfileUrl: string
      IconUrl: string option
      IsGroup: bool
      /// Whether Megáfono may list the actor in directories (a hint to other servers). Users: only a Public profile.
      Discoverable: bool
      /// Whether search engines and full-text search may index the actor's posts (same rule)
      Indexable: bool }

/// The wire shape: camelCase keys, `iconUrl` only when there is one, `discoverable` and `indexable` always booleans
let private toJson (actor: Actor) : IDictionary<string, obj> =
    let json = Dictionary<string, obj>()
    json["id"] <- actor.Id
    json["handle"] <- actor.Handle
    json["name"] <- actor.Name
    json["profileUrl"] <- actor.ProfileUrl
    actor.IconUrl |> Option.iter (fun url -> json["iconUrl"] <- url)
    json["type"] <- (if actor.IsGroup then "Group" else "Person")
    json["locked"] <- false
    json["discoverable"] <- actor.Discoverable
    json["indexable"] <- actor.Indexable
    json :> IDictionary<string, obj>

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// Crunchyroll's id alphabet, checked before any query
let private seriesIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

[<Literal>]
let private SeriesPrefix = "series-"

/// The actor of the user row the reader is on, None when the username can't federate (a trailing dash from
/// before feature 013 isn't a valid handle)
let private readUser (domain: string) (instanceDefault: Profiles.Visibility) (r: System.Data.Common.DbDataReader) : Actor option =
    if r.Read() then
        let username = r.GetString 1

        if Fediverse.isValidHandle username then
            // The user's own choice, else the instance default: only a Public profile is discoverable and indexable
            let choice = if r.IsDBNull 3 then None else Profiles.Visibility.tryParse (r.GetString 3)
            let isPublic = (Profiles.effective instanceDefault choice = Profiles.Public)

            Some
                { Id = string (r.GetGuid 0)
                  Handle = username
                  Name = r.GetString 2
                  ProfileUrl = $"https://{domain}/u/{username}"
                  IconUrl = None
                  IsGroup = false
                  Discoverable = isPublic
                  Indexable = isPublic }
        else
            None
    else
        None

let private userByName (db: DbConfig) (domain: string) (instanceDefault: Profiles.Visibility) (username: string) : Task<Actor option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand(
                """
                SELECT u.id, u.username, u.display_name, s.visibility
                FROM users u LEFT JOIN profile_settings s ON s.user_id = u.id
                WHERE u.username = @username
                """,
                connection
            )
        cmd.Parameters.AddWithValue("username", username) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        return readUser domain instanceDefault r
    }

let private userById (db: DbConfig) (domain: string) (instanceDefault: Profiles.Visibility) (id: Guid) : Task<Actor option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand(
                """
                SELECT u.id, u.username, u.display_name, s.visibility
                FROM users u LEFT JOIN profile_settings s ON s.user_id = u.id
                WHERE u.id = @id
                """,
                connection
            )
        cmd.Parameters.AddWithValue("id", id) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        return readUser domain instanceDefault r
    }

/// The series title the series page shows: the stored one, else the smallest episode series title, else the id
let private seriesTitle (connection: NpgsqlConnection) (seriesId: string) : Task<string option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT coalesce((SELECT title FROM cr_series WHERE id = @series_id),
                                (SELECT min(series_title) FROM cr_episodes WHERE series_id = @series_id),
                                @series_id),
                       EXISTS (SELECT 1 FROM cr_series WHERE id = @series_id)
                         OR EXISTS (SELECT 1 FROM cr_episodes WHERE series_id = @series_id)
                """,
                connection
            )

        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore
        use! r = cmd.ExecuteReaderAsync()

        if r.Read() && r.GetBoolean 1 then
            return Some(r.GetString 0)
        else
            return None
    }

/// The icon: the MyAnimeList image of the matched season most people watched, only when it is an absolute https
/// URL on MyAnimeList's own host
let private seriesIcon (connection: NpgsqlConnection) (seriesId: string) : Task<string option> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT a.image_url
                FROM cr_episodes e
                JOIN watch_events w ON w.cr_episode_id = e.id
                JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                JOIN mal_anime a ON a.mal_id = m.mal_id AND a.image_url IS NOT NULL
                WHERE e.series_id = @series_id AND e.duration_ms IS NOT NULL
                GROUP BY a.mal_id, a.image_url
                ORDER BY count(DISTINCT w.user_id) DESC, a.mal_id
                LIMIT 1
                """,
                connection
            )

        cmd.Parameters.AddWithValue("series_id", seriesId) |> ignore

        match! cmd.ExecuteScalarAsync() with
        | :? string as url ->
            match Uri.TryCreate(url, UriKind.Absolute) with
            | true, uri when
                uri.Scheme = Uri.UriSchemeHttps
                && (uri.Host = "myanimelist.net" || uri.Host.EndsWith ".myanimelist.net")
                ->
                return Some url
            | _ -> return None
        | _ -> return None
    }

let private seriesActor (db: DbConfig) (domain: string) (seriesId: string) : Task<Actor option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        match! seriesTitle connection seriesId with
        | None -> return None
        | Some title ->
            let! handle = Fediverse.ensureSeriesHandle connection seriesId
            let! icon = seriesIcon connection seriesId

            return
                Some
                    { Id = SeriesPrefix + seriesId
                      Handle = handle
                      Name = title
                      ProfileUrl = $"https://{domain}/anime/{seriesId}"
                      IconUrl = icon
                      IsGroup = true
                      Discoverable = true
                      Indexable = true }
    }

/// A series that already has a handle, found by it (a handle never creates one)
let private seriesByHandle (db: DbConfig) (domain: string) (handle: string) : Task<Actor option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use cmd = new NpgsqlCommand("SELECT series_id FROM series_handles WHERE handle = @handle", connection)
        cmd.Parameters.AddWithValue("handle", handle) |> ignore

        match! cmd.ExecuteScalarAsync() with
        | :? string as seriesId -> return! seriesActor db domain seriesId
        | _ -> return None
    }

/// By handle: a user (when the name qualifies as a user and a handle), else a series with that handle
let private byHandle (db: DbConfig) (domain: string) (instanceDefault: Profiles.Visibility) (raw: string) : Task<Actor option> =
    task {
        let handle = raw.Trim().ToLowerInvariant()

        if not (Fediverse.isValidHandle handle) then
            return None
        else
            let! user =
                match Username.tryCreate handle with
                | Some username -> userByName db domain instanceDefault username
                | None -> Task.FromResult None

            match user with
            | Some _ -> return user
            | None -> return! seriesByHandle db domain handle
    }

/// By id: a user's uuid, or `series-{seriesId}`
let private byId (db: DbConfig) (domain: string) (instanceDefault: Profiles.Visibility) (id: string) : Task<Actor option> =
    task {
        if id.StartsWith(SeriesPrefix, StringComparison.Ordinal) then
            let seriesId = id.Substring SeriesPrefix.Length

            if seriesIdPattern.IsMatch seriesId then
                return! seriesActor db domain seriesId
            else
                return None
        else
            match Guid.TryParse id with
            | true, guid -> return! userById db domain instanceDefault guid
            | _ -> return None
    }

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// `Authorization: Bearer {token}` against the configured token, in constant time over the UTF-8 bytes
let authorized (token: string) (ctx: HttpContext) =
    let given = Encoding.UTF8.GetBytes(string ctx.Request.Headers.Authorization)
    let expected = Encoding.UTF8.GetBytes $"Bearer {token}"
    token <> "" && CryptographicOperations.FixedTimeEquals(given, expected)

let private status (code: int) (ctx: HttpContext) =
    ctx.SetStatusCode code
    Task.CompletedTask

let private answer (find: Federation.FederationValues -> HttpContext -> Task<Actor option>) : EndpointHandler =
    fun ctx ->
        task {
            match ctx.GetService<Federation.FederationSettings>() with
            | Federation.Disabled -> return! status 404 ctx
            | Federation.Enabled values ->
                if not (authorized values.Token ctx) then
                    return! status 401 ctx
                else
                    match! find values ctx with
                    | Some actor -> return! ctx.WriteJson(toJson actor)
                    | None -> return! status 404 ctx
        }

/// GET /megafono/actors?handle={h}
let byHandleHandler: EndpointHandler =
    answer (fun values ctx ->
        match ctx.TryGetQueryValue "handle" with
        | Some handle -> byHandle (ctx.GetService<DbConfig>()) values.Domain (ctx.GetService<Profiles.ProfileSettings>()).DefaultVisibility handle
        | None -> Task.FromResult None)

/// GET /megafono/actors/{id}
let byIdHandler (id: string) : EndpointHandler =
    answer (fun values ctx -> byId (ctx.GetService<DbConfig>()) values.Domain (ctx.GetService<Profiles.ProfileSettings>()).DefaultVisibility id)

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint =
    [ GET [ route "/megafono/actors" byHandleHandler ]
      GET [ routef "/megafono/actors/{%s}" byIdHandler ] ]
