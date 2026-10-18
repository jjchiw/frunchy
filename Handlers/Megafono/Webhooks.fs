/// Feature 015 (US3, FR-010..FR-012): Megáfono's event receiver. Served only with federation on. The checks run in
/// this order: the bearer token (401), the raw body capped at 1 MB (413), the `Megafono-Signature` (401), the JSON
/// envelope (400); then one transaction records the event id (a repeat is 204 at once) and applies the event
/// (research R6). Events are not ordered, so a reply update can arrive before its reply and a deletion before
/// both; the rules below make every order end in the same rows.
module FrunchyBackend.Handlers.Megafono.Webhooks

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

[<Literal>]
let private MaxBodyBytes = 1048576

type private Outcome =
    | Applied
    | Ignored
    | UnknownThread
    | Stale

let private outcomeName (outcome: Outcome) =
    match outcome with
    | Applied -> "applied"
    | Ignored -> "ignored"
    | UnknownThread -> "unknown_thread"
    | Stale -> "stale"

// ---------------------------------------------------------------------------
// Reading the payload: every field is optional until checked
// ---------------------------------------------------------------------------

let private prop (parent: JsonElement) (name: string) : JsonElement option =
    if parent.ValueKind = JsonValueKind.Object then
        match parent.TryGetProperty name with
        | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
        | _ -> None
    else
        None

let private str (parent: JsonElement) (name: string) : string option =
    prop parent name
    |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)

let private nonEmpty (value: string option) = value |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private flag (parent: JsonElement) (name: string) =
    match prop parent name with
    | Some value -> value.ValueKind = JsonValueKind.True
    | None -> false

let private instant (parent: JsonElement) (name: string) : DateTime option =
    str parent name
    |> Option.bind (fun text ->
        match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, value -> Some value.UtcDateTime
        | _ -> None)

let private truncate (max: int) (value: string) =
    if value.Length > max then value.Substring(0, max) else value

/// Only https URLs are stored for a remote actor's profile and icon
let private httpsOnly (value: string option) : string option =
    value
    |> Option.filter (fun text ->
        text.Length <= 2000
        && (match Uri.TryCreate(text, UriKind.Absolute) with
            | true, uri -> uri.Scheme = Uri.UriSchemeHttps
            | _ -> false))

let private nullable (value: string option) : obj =
    match value with
    | Some text -> box text
    | None -> box DBNull.Value

type private ActorInfo =
    { Id: string
      Handle: string
      Name: string
      ProfileUrl: string option
      IconUrl: string option }

/// The envelope's `actor`: it needs an id and a handle; a missing name falls back to the handle
let private readActor (json: JsonElement) : ActorInfo option =
    match prop json "actor" with
    | None -> None
    | Some actor ->
        match nonEmpty (str actor "id"), nonEmpty (str actor "handle") with
        | Some id, Some handle when id.Length <= 2000 ->
            Some
                { Id = id
                  Handle = truncate 300 handle
                  Name = truncate 200 (nonEmpty (str actor "name") |> Option.defaultValue handle)
                  ProfileUrl = httpsOnly (str actor "profileUrl")
                  IconUrl = httpsOnly (str actor "iconUrl") }
        | _ -> None

// ---------------------------------------------------------------------------
// Data access, all in the event's transaction
// ---------------------------------------------------------------------------

/// Record the event id; false when it was already recorded (a repeat)
let private recordEvent (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (id: string) (eventType: string) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                "INSERT INTO megafono_events (id, type, outcome) VALUES (@id, @type, 'pending') ON CONFLICT (id) DO NOTHING RETURNING id",
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", id) |> ignore
        cmd.Parameters.AddWithValue("type", eventType) |> ignore
        let! inserted = cmd.ExecuteScalarAsync()
        return not (isNull inserted)
    }

let private setOutcome (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (id: string) (outcome: Outcome) : Task<unit> =
    task {
        use cmd = new NpgsqlCommand("UPDATE megafono_events SET outcome = @outcome WHERE id = @id", connection, tx)
        cmd.Parameters.AddWithValue("outcome", outcomeName outcome) |> ignore
        cmd.Parameters.AddWithValue("id", id) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// The episode a published thread belongs to, by Megáfono's object id for it
let private threadEpisode (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (objectId: string) : Task<string option> =
    task {
        use cmd = new NpgsqlCommand("SELECT cr_episode_id FROM episode_threads WHERE object_id = @object_id", connection, tx)
        cmd.Parameters.AddWithValue("object_id", objectId) |> ignore

        match! cmd.ExecuteScalarAsync() with
        | :? string as episodeId -> return Some episodeId
        | _ -> return None
    }

let private upsertActor (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (actor: ActorInfo) : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO remote_actors (id, handle, name, profile_url, icon_url)
                VALUES (@id, @handle, @name, @profile_url, @icon_url)
                ON CONFLICT (id) DO UPDATE
                SET handle = EXCLUDED.handle, name = EXCLUDED.name, profile_url = EXCLUDED.profile_url,
                    icon_url = EXCLUDED.icon_url, updated_at = now()
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", actor.Id) |> ignore
        cmd.Parameters.AddWithValue("handle", actor.Handle) |> ignore
        cmd.Parameters.AddWithValue("name", actor.Name) |> ignore
        cmd.Parameters.AddWithValue("profile_url", nullable actor.ProfileUrl) |> ignore
        cmd.Parameters.AddWithValue("icon_url", nullable actor.IconUrl) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

type private Reply =
    { ObjectId: string
      EpisodeId: string
      ActorId: string
      Content: string
      Summary: string option
      Sensitive: bool
      InReplyTo: string option
      Audience: string
      Published: DateTime
      Updated: DateTime option }

/// Insert a new remote comment, or update a live one when the incoming copy is newer than the stored one. A
/// tombstone is never revived, and a local comment is never touched. True when a row was written.
let private upsertComment (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (reply: Reply) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO comments (id, cr_episode_id, remote_actor, content, format, summary, sensitive, in_reply_to,
                                      audience, object_id, created_at, edited_at)
                VALUES (@id, @episode_id, @actor, @content, 'html', @summary, @sensitive, @in_reply_to,
                        @audience, @object_id, @published, @updated)
                ON CONFLICT (object_id) DO UPDATE
                SET content = EXCLUDED.content, summary = EXCLUDED.summary, sensitive = EXCLUDED.sensitive,
                    audience = EXCLUDED.audience, edited_at = EXCLUDED.edited_at
                WHERE comments.remote_actor IS NOT NULL AND comments.deleted_at IS NULL
                  AND coalesce(EXCLUDED.edited_at, EXCLUDED.created_at) > coalesce(comments.edited_at, comments.created_at)
                RETURNING comments.id
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", Fediverse.remoteCommentId reply.ObjectId) |> ignore
        cmd.Parameters.AddWithValue("episode_id", reply.EpisodeId) |> ignore
        cmd.Parameters.AddWithValue("actor", reply.ActorId) |> ignore
        cmd.Parameters.AddWithValue("content", reply.Content) |> ignore
        cmd.Parameters.AddWithValue("summary", nullable reply.Summary) |> ignore
        cmd.Parameters.AddWithValue("sensitive", reply.Sensitive) |> ignore
        cmd.Parameters.AddWithValue("in_reply_to", nullable reply.InReplyTo) |> ignore
        cmd.Parameters.AddWithValue("audience", reply.Audience) |> ignore
        cmd.Parameters.AddWithValue("object_id", reply.ObjectId) |> ignore
        cmd.Parameters.AddWithValue("published", reply.Published) |> ignore

        cmd.Parameters.AddWithValue(
            "updated",
            (match reply.Updated with
             | Some value -> box value
             | None -> box DBNull.Value)
        )
        |> ignore

        let! written = cmd.ExecuteScalarAsync()
        return not (isNull written)
    }

/// Tombstone a live remote comment (its text goes, as a local delete clears it); true when one was found
let private tombstone (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (objectId: string) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                UPDATE comments
                SET deleted_at = now(), content = '', summary = NULL, sensitive = false
                WHERE object_id = @object_id AND remote_actor IS NOT NULL AND deleted_at IS NULL
                RETURNING id
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("object_id", objectId) |> ignore
        let! found = cmd.ExecuteScalarAsync()
        return not (isNull found)
    }

/// A deletion that arrived before its reply: a tombstone row, so the late `reply.received` stays deleted. False
/// when the object id already has a row.
let private insertTombstone
    (connection: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    (episodeId: string)
    (actorId: string)
    (objectId: string)
    : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO comments (id, cr_episode_id, remote_actor, content, format, audience, object_id, created_at, deleted_at)
                VALUES (@id, @episode_id, @actor, '', 'html', 'public', @object_id, now(), now())
                ON CONFLICT (object_id) DO NOTHING
                RETURNING id
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("id", Fediverse.remoteCommentId objectId) |> ignore
        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore
        cmd.Parameters.AddWithValue("actor", actorId) |> ignore
        cmd.Parameters.AddWithValue("object_id", objectId) |> ignore
        let! inserted = cmd.ExecuteScalarAsync()
        return not (isNull inserted)
    }

// ---------------------------------------------------------------------------
// The events (research R6)
// ---------------------------------------------------------------------------

let private audiences = set [ "public"; "unlisted"; "followers"; "direct" ]

/// `reply.received` and `reply.updated`: the same upsert, the thread decides whether it is ours
let private applyReply (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (now: DateTime) (json: JsonElement) : Task<Outcome> =
    task {
        let objectJson = prop json "object"

        let objectId = objectJson |> Option.bind (fun o -> nonEmpty (str o "id"))

        let threadId =
            objectJson
            |> Option.bind (fun o -> prop o "thread")
            |> Option.bind (fun thread -> nonEmpty (str thread "objectId"))

        match objectJson, readActor json, objectId, threadId with
        | Some o, Some actor, Some objectId, Some threadId when objectId.Length <= 2000 ->
            match! threadEpisode connection tx threadId with
            | None -> return UnknownThread
            | Some episodeId ->
                do! upsertActor connection tx actor

                // An audience this version doesn't know is kept but never shown
                let audience =
                    match str o "audience" with
                    | Some a when audiences.Contains a -> a
                    | _ -> "direct"

                let reply =
                    { ObjectId = objectId
                      EpisodeId = episodeId
                      ActorId = actor.Id
                      Content = str o "content" |> Option.defaultValue ""
                      Summary = nonEmpty (str o "summary")
                      Sensitive = flag o "sensitive"
                      InReplyTo = nonEmpty (str o "inReplyTo")
                      Audience = audience
                      Published = instant o "published" |> Option.orElse (instant json "occurredAt") |> Option.defaultValue now
                      Updated = instant o "updated" }

                let! written = upsertComment connection tx reply
                return (if written then Applied else Stale)
        | _ -> return Ignored
    }

let private applyDeleted (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (json: JsonElement) : Task<Outcome> =
    task {
        let objectJson = prop json "object"
        let objectId = objectJson |> Option.bind (fun o -> nonEmpty (str o "id"))

        let threadId =
            objectJson
            |> Option.bind (fun o -> prop o "thread")
            |> Option.bind (fun thread -> nonEmpty (str thread "objectId"))

        match objectId with
        | Some objectId when objectId.Length <= 2000 ->
            let! found = tombstone connection tx objectId

            if found then
                return Applied
            else
                // Unknown (or already deleted): on a known thread, leave a tombstone behind
                match threadId with
                | None -> return Ignored
                | Some threadId ->
                    match! threadEpisode connection tx threadId with
                    | None -> return UnknownThread
                    | Some episodeId ->
                        let actor = readActor json
                        let actorId = actor |> Option.map _.Id |> Option.defaultValue objectId

                        match actor with
                        | Some a -> do! upsertActor connection tx a
                        | None -> ()

                        let! inserted = insertTombstone connection tx episodeId actorId objectId
                        return (if inserted then Applied else Stale)
        | _ -> return Ignored
    }

let private applyActorUpdated (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (json: JsonElement) : Task<Outcome> =
    task {
        match readActor json with
        | Some actor ->
            do! upsertActor connection tx actor
            return Applied
        | None -> return Ignored
    }

let private apply (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (now: DateTime) (eventType: string) (json: JsonElement) : Task<Outcome> =
    match eventType with
    | "reply.received"
    | "reply.updated" -> applyReply connection tx now json
    | "object.deleted" -> applyDeleted connection tx json
    | "actor.updated" -> applyActorUpdated connection tx json
    // follow.*, delivery.failed and types a later contract version adds are acknowledged and recorded
    | _ -> Task.FromResult Ignored

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let private status (code: int) (ctx: HttpContext) =
    ctx.SetStatusCode code
    Task.CompletedTask

/// The raw body, or None when it is over the cap (checked on `Content-Length`, and again while reading, since
/// the header may be absent or a lie)
let private readBody (ctx: HttpContext) : Task<byte[] option> =
    task {
        let declared = ctx.Request.ContentLength

        if declared.HasValue && declared.Value > int64 MaxBodyBytes then
            return None
        else
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable total = 0
            let mutable tooBig = false
            let mutable reading = true

            while reading do
                let! read = ctx.Request.Body.ReadAsync(chunk, 0, chunk.Length)

                if read = 0 then
                    reading <- false
                else
                    total <- total + read

                    if total > MaxBodyBytes then
                        tooBig <- true
                        reading <- false
                    else
                        buffer.Write(chunk, 0, read)

            return (if tooBig then None else Some(buffer.ToArray()))
    }

/// The envelope: a JSON object with a string `id` and `type`
let private parseEnvelope (body: byte[]) : (JsonDocument * string * string) option =
    try
        let doc = JsonDocument.Parse body
        let root = doc.RootElement

        match nonEmpty (str root "id"), nonEmpty (str root "type") with
        | Some id, Some eventType when id.Length <= 200 && eventType.Length <= 100 -> Some(doc, id, eventType)
        | _ ->
            doc.Dispose()
            None
    with :? JsonException ->
        None

/// POST /megafono/webhooks
let handler: EndpointHandler =
    fun ctx ->
        task {
            match ctx.GetService<Federation.FederationSettings>() with
            | Federation.Disabled -> return! status 404 ctx
            | Federation.Enabled values ->
                if not (Actors.authorized values.Token ctx) then
                    return! status 401 ctx
                else
                    match! readBody ctx with
                    | None -> return! status 413 ctx
                    | Some body ->
                        let now = ctx.GetService<TimeProvider>().GetUtcNow()

                        if not (Fediverse.verifySignature values.WebhookSecret now (string ctx.Request.Headers["Megafono-Signature"]) body) then
                            return! status 401 ctx
                        else
                            match parseEnvelope body with
                            | None -> return! status 400 ctx
                            | Some(doc, eventId, eventType) ->
                                use doc = doc
                                use connection = getConnection (ctx.GetService<DbConfig>())
                                do! connection.OpenAsync()
                                use tx = connection.BeginTransaction()

                                match! recordEvent connection tx eventId eventType with
                                | false ->
                                    // a repeat: nothing to apply
                                    do! tx.CommitAsync()
                                | true ->
                                    let! outcome = apply connection tx now.UtcDateTime eventType doc.RootElement
                                    do! setOutcome connection tx eventId outcome
                                    do! tx.CommitAsync()

                                    ctx.GetLogger()
                                        .LogInformation("megafono webhook {Event} {Type} {Outcome}", eventId, eventType, outcomeName outcome)

                                return! status 204 ctx
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = [ POST [ route "/megafono/webhooks" handler ] ]
