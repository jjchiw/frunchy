/// Feature 015 (US3, T010): Megáfono's event receiver. Bodies are signed with the test webhook secret at a fixed
/// clock; threads are made the real way (a local comment is published through the outbox against the fake Megáfono),
/// so `episode_threads.object_id` is the id the fake handed out. This file holds the protocol rules (auth, repeats,
/// ordering); how the comments look on the page is in RemoteCommentsTests.
module FrunchyBackend.Tests.MegafonoWebhooksTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.FreshAuthTests
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// ---------------------------------------------------------------------------
// Setup shared with RemoteCommentsTests
// ---------------------------------------------------------------------------

type Env =
    { App: TestApp
      Clock: TestClock
      Ep: EpisodeRef
      /// A signed-in author whose comment ("Alice's take") opened the thread
      Alice: Browser
      /// Megáfono's object id for the thread
      ThreadId: string
      AliceCommentId: string
      AliceObjectId: string }

    member env.Now = env.Clock.Now

    interface IAsyncDisposable with
        member env.DisposeAsync() = (env.App :> IAsyncDisposable).DisposeAsync()

/// A federated app with the 2026 sample, "alice" and her published comment, so the episode's thread exists
let newEnvWith (extra: (string * string) list) : Task<Env> =
    task {
        let! db = newDatabase ()
        let clock = TestClock()

        let! app =
            createFederatedAppWith db extra (fun (b: WebApplicationBuilder) -> b.Services.AddSingleton<TimeProvider>(clock) |> ignore)

        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL AND e.season_id IS NOT NULL AND e.title IS NOT NULL"
        let! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Alice's take" false
        let! _ = drainOutbox app

        let! thread = Db.scalar<string> app.Db "SELECT object_id FROM episode_threads" []
        let! commentId = Db.scalar<string> app.Db "SELECT id FROM comments WHERE content = 'Alice''s take'" []
        let! objectId = Db.scalar<string> app.Db "SELECT object_id FROM comments WHERE content = 'Alice''s take'" []

        return
            { App = app
              Clock = clock
              Ep = ep
              Alice = alice
              ThreadId = thread
              AliceCommentId = commentId
              AliceObjectId = objectId }
    }

let newEnv () = newEnvWith []

// ---------------------------------------------------------------------------
// Payloads (Megáfono's app contract, 002-replies-threads)
// ---------------------------------------------------------------------------

/// What a `reply.*` event says; null strings are JSON nulls
type Reply =
    { EventId: string
      Thread: string
      ObjectId: string
      ActorId: string
      Handle: string
      Name: string
      ProfileUrl: string
      Content: string
      Summary: string
      Sensitive: bool
      Audience: string
      Published: string
      Updated: string
      InReplyTo: string }

/// Bob on mastodon.example, replying in `thread`; `n` makes the event and the object ids
let reply (thread: string) (n: int) : Reply =
    { EventId = $"evt-{n}"
      Thread = thread
      ObjectId = $"https://mastodon.example/users/bob/statuses/{n}"
      ActorId = "https://mastodon.example/users/bob"
      Handle = "@bob@mastodon.example"
      Name = "Bob"
      ProfileUrl = "https://mastodon.example/@bob"
      Content = "<p>that ending!</p>"
      Summary = null
      Sensitive = false
      Audience = "public"
      Published = "2026-10-12T17:59:58Z"
      Updated = null
      InReplyTo = $"https://frunchy.test/ap/objects/{thread}" }

let private actorOf (r: Reply) =
    {| id = r.ActorId
       handle = r.Handle
       name = r.Name
       iconUrl = "https://files.mastodon.example/bob.png"
       profileUrl = r.ProfileUrl |}

let private noExtensions: string = null

/// `reply.received` or `reply.updated`
let replyJson (eventType: string) (r: Reply) : string =
    JsonSerializer.Serialize(
        {| id = r.EventId
           version = 1
           ``type`` = eventType
           occurredAt = "2026-10-12T18:00:00Z"
           seq = 812
           user = {| id = "series-52991"; handle = "frieren" |}
           actor = actorOf r
           ``object`` =
            {| id = r.ObjectId
               url = r.ObjectId
               ``type`` = "Note"
               content = r.Content
               summary = r.Summary
               sensitive = r.Sensitive
               language = "en"
               published = r.Published
               updated = r.Updated
               audience = r.Audience
               inReplyTo = r.InReplyTo
               thread = {| objectId = r.Thread; id = $"https://frunchy.test/ap/objects/{r.Thread}" |} |}
           extensions = noExtensions |}
    )

let receivedJson (r: Reply) = replyJson "reply.received" r
let updatedJson (r: Reply) = replyJson "reply.updated" r

/// `object.deleted` for the reply `r` describes
let deletedJson (eventId: string) (r: Reply) : string =
    JsonSerializer.Serialize(
        {| id = eventId
           version = 1
           ``type`` = "object.deleted"
           occurredAt = "2026-10-12T18:05:00Z"
           seq = 813
           user = {| id = "series-52991"; handle = "frieren" |}
           actor = actorOf r
           ``object`` = {| id = r.ObjectId; thread = {| objectId = r.Thread; id = $"https://frunchy.test/ap/objects/{r.Thread}" |} |}
           extensions = noExtensions |}
    )

/// An event of another type, with only an actor
let otherJson (eventId: string) (eventType: string) (r: Reply) : string =
    JsonSerializer.Serialize(
        {| id = eventId
           version = 1
           ``type`` = eventType
           occurredAt = "2026-10-12T18:00:00Z"
           seq = 1
           user = {| id = "series-52991"; handle = "frieren" |}
           actor = actorOf r
           extensions = noExtensions |}
    )

// ---------------------------------------------------------------------------
// Delivery
// ---------------------------------------------------------------------------

let bearer = $"Bearer {FakeMegafono.Token}"

let signedAt (now: DateTimeOffset) (body: string) =
    Fediverse.signBody FakeMegafono.WebhookSecret now (Encoding.UTF8.GetBytes body)

/// POST a body with exactly this Authorization and Megafono-Signature (None: the header is absent)
let sendRaw (app: TestApp) (authorization: string option) (signature: string option) (body: string) : Task<HttpStatusCode> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/megafono/webhooks")
        authorization |> Option.iter (fun v -> request.Headers.TryAddWithoutValidation("Authorization", v) |> ignore)
        signature |> Option.iter (fun v -> request.Headers.TryAddWithoutValidation("Megafono-Signature", v) |> ignore)
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use! response = app.Client.SendAsync request
        return response.StatusCode
    }

/// Deliver a body the way Megáfono does: the token, and a signature made now
let deliverStatus (env: Env) (body: string) : Task<HttpStatusCode> =
    sendRaw env.App (Some bearer) (Some(signedAt env.Now body)) body

/// Deliver and expect the 204 every handled event gets
let deliver (env: Env) (body: string) : Task =
    task {
        let! status = deliverStatus env body
        Assert.Equal(HttpStatusCode.NoContent, status)
    }

/// "id|type|outcome" of every event the log holds
let eventLog (env: Env) = Db.dump env.App.Db "SELECT id, type, outcome FROM megafono_events ORDER BY id"

/// One remote comment by its object id: the columns the rules decide
let remoteRow (env: Env) (objectId: string) : Task<string list> =
    Db.dump
        env.App.Db
        $"""SELECT remote_actor, format, audience, coalesce(in_reply_to, '-'), content, coalesce(summary, '-'), sensitive,
                   to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                   coalesce(to_char(edited_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), '-'),
                   deleted_at IS NOT NULL, cr_episode_id
            FROM comments WHERE object_id = '{objectId}'"""

/// Everything a webhook can write
let private stored (env: Env) =
    task {
        let! events = eventLog env
        let! comments = rawCommentRows env.App.Db
        let! actors = Db.dump env.App.Db "SELECT id, handle, name, coalesce(profile_url, '-'), coalesce(icon_url, '-') FROM remote_actors ORDER BY id"
        return events, comments, actors
    }

/// The comments the episode page shows to a visitor
let pageComments (env: Env) : Task<PageComment list> =
    task {
        let! status, html = episodePage env.App "en" env.Ep.Series env.Ep.Episode
        Assert.Equal(HttpStatusCode.OK, status)
        return comments html
    }

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

[<Fact>]
let ``a missing or wrong token, and a missing, wrong, old or future signature are all 401 and store nothing`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! before = stored env
        let body = receivedJson (reply env.ThreadId 1)
        let good = signedAt env.Now body

        let attempts =
            [ "no token", None, Some good
              "a wrong token", Some "Bearer nope", Some good
              "the token without Bearer", Some FakeMegafono.Token, Some good
              "no signature", Some bearer, None
              "a garbage signature", Some bearer, Some "garbage"
              "a wrong secret", Some bearer, Some(Fediverse.signBody "another-secret" env.Now (Encoding.UTF8.GetBytes body))
              "a signature of another body", Some bearer, Some(signedAt env.Now (body + " "))
              "a signature 6 minutes old", Some bearer, Some(signedAt (env.Now.AddMinutes -6.0) body)
              "a signature 6 minutes ahead", Some bearer, Some(signedAt (env.Now.AddMinutes 6.0) body) ]

        for (what, authorization, signature) in attempts do
            let! status = sendRaw env.App authorization signature body
            Assert.True((status = HttpStatusCode.Unauthorized), $"{what}: expected 401, got {int status}")

        let! after = stored env
        Assert.Equal(before, after)

        // the good one still works, and a signature a few minutes either side is within the window
        let! status = sendRaw env.App (Some bearer) (Some(signedAt (env.Now.AddMinutes -4.0) body)) body
        Assert.Equal(HttpStatusCode.NoContent, status)
    }

[<Fact>]
let ``with federation off the webhook route does not exist`` () =
    task {
        use! app = newApp ()
        let body = receivedJson (reply "x" 1)
        let! status = sendRaw app (Some bearer) (Some(signedAt DateTimeOffset.UtcNow body)) body
        Assert.Equal(HttpStatusCode.NotFound, status)
    }

// ---------------------------------------------------------------------------
// Repeats, unknown threads and types
// ---------------------------------------------------------------------------

[<Fact>]
let ``the same event id again is 204 and changes nothing, even with a different body`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (receivedJson r)
        let! once = stored env
        let events, _, _ = once
        Assert.Equal<string list>([ "evt-1|reply.received|applied" ], events)

        do! deliver env (receivedJson r)
        do! deliver env (receivedJson { r with Content = "<p>another text</p>"; Updated = "2026-10-12T19:00:00Z" })
        let! again = stored env
        Assert.Equal(once, again)

        let! row = remoteRow env r.ObjectId
        Assert.Equal<string list>([ "https://mastodon.example/users/bob|html|public|" + r.InReplyTo + "|<p>that ending!</p>|-|False|2026-10-12T17:59:58Z|-|False|" + env.Ep.Episode ], row)
    }

[<Fact>]
let ``a reply to an unknown thread is 204, recorded as unknown_thread, and stores nothing else`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! beforeComments = rawCommentRows env.App.Db
        do! deliver env (receivedJson (reply "no-such-thread" 1))
        do! deliver env (updatedJson { reply "no-such-thread" 2 with Updated = "2026-10-12T19:00:00Z" })
        do! deliver env (deletedJson "evt-3" (reply "no-such-thread" 3))

        let! log = eventLog env
        Assert.Equal<string list>([ "evt-1|reply.received|unknown_thread"; "evt-2|reply.updated|unknown_thread"; "evt-3|object.deleted|unknown_thread" ], log)
        let! afterComments = rawCommentRows env.App.Db
        Assert.Equal<string list>(beforeComments, afterComments)
        let! actors = Db.count env.App.Db "remote_actors"
        Assert.Equal(0L, actors)
    }

[<Fact>]
let ``an unknown or unused event type is 204 and only recorded`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (otherJson "evt-a" "follow.requested" r)
        do! deliver env (otherJson "evt-b" "follow.accepted" r)
        do! deliver env (otherJson "evt-c" "delivery.failed" r)
        do! deliver env (otherJson "evt-d" "like.received" r)
        do! deliver env (otherJson "evt-e" "something.from.the.future" r)

        let! log = eventLog env
        Assert.Equal<string list>(
            [ "evt-a|follow.requested|ignored"
              "evt-b|follow.accepted|ignored"
              "evt-c|delivery.failed|ignored"
              "evt-d|like.received|ignored"
              "evt-e|something.from.the.future|ignored" ],
            log
        )

        // nothing else was written: not the actor, not a comment
        let! actors = Db.count env.App.Db "remote_actors"
        Assert.Equal(0L, actors)
        let! rows = rawCommentRows env.App.Db
        Assert.Equal(1, rows.Length)
    }

[<Fact>]
let ``actor.updated upserts the remote actor, and only https profile and icon URLs are kept`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (otherJson "evt-a" "actor.updated" r)

        let! row = Db.dump env.App.Db "SELECT id, handle, name, profile_url, icon_url FROM remote_actors"
        Assert.Equal<string list>([ "https://mastodon.example/users/bob|@bob@mastodon.example|Bob|https://mastodon.example/@bob|https://files.mastodon.example/bob.png" ], row)

        do! deliver env (otherJson "evt-b" "actor.updated" { r with Name = "Robert"; ProfileUrl = "http://mastodon.example/@bob" })
        let! row = Db.dump env.App.Db "SELECT name, coalesce(profile_url, '-'), icon_url FROM remote_actors"
        Assert.Equal<string list>([ "Robert|-|https://files.mastodon.example/bob.png" ], row)

        let! log = eventLog env
        Assert.Equal<string list>([ "evt-a|actor.updated|applied"; "evt-b|actor.updated|applied" ], log)
    }

// ---------------------------------------------------------------------------
// reply.received, followers and direct, replies to Frunchy comments
// ---------------------------------------------------------------------------

[<Fact>]
let ``reply.received stores the comment on the thread's episode and upserts the author`` () =
    task {
        let! env = newEnv ()
        use env = env

        let r =
            { reply env.ThreadId 1 with
                Summary = "Spoilers: S2 E5"
                Sensitive = true
                Updated = "2026-10-12T18:30:00Z" }

        do! deliver env (receivedJson r)

        let! row = remoteRow env r.ObjectId
        Assert.Equal<string list>(
            [ $"https://mastodon.example/users/bob|html|public|{r.InReplyTo}|<p>that ending!</p>|Spoilers: S2 E5|True|2026-10-12T17:59:58Z|2026-10-12T18:30:00Z|False|{env.Ep.Episode}" ],
            row
        )

        let! id = Db.scalar<string> env.App.Db $"SELECT id FROM comments WHERE object_id = '{r.ObjectId}'" []
        Assert.Equal(Fediverse.remoteCommentId r.ObjectId, id)

        let! actors = Db.dump env.App.Db "SELECT id, handle, name, profile_url, icon_url FROM remote_actors"
        Assert.Equal<string list>([ "https://mastodon.example/users/bob|@bob@mastodon.example|Bob|https://mastodon.example/@bob|https://files.mastodon.example/bob.png" ], actors)

        // a second reply by the same actor with a new name updates the actor row, not a second one
        do! deliver env (receivedJson { reply env.ThreadId 2 with Name = "Bob B." })
        let! actors = Db.dump env.App.Db "SELECT name FROM remote_actors"
        Assert.Equal<string list>([ "Bob B." ], actors)
    }

[<Fact>]
let ``followers and direct replies are stored but never shown, public and unlisted are`` () =
    task {
        let! env = newEnv ()
        use env = env

        for (n, audience) in [ 1, "public"; 2, "unlisted"; 3, "followers"; 4, "direct" ] do
            do! deliver env (receivedJson { reply env.ThreadId n with Audience = audience; Content = $"<p>{audience} words</p>" })

        let! audiences = Db.dump env.App.Db "SELECT audience FROM comments WHERE remote_actor IS NOT NULL ORDER BY audience"
        Assert.Equal<string list>([ "direct"; "followers"; "public"; "unlisted" ], audiences)

        let! shown = pageComments env
        let texts = shown |> List.map _.Text
        Assert.Contains(texts, fun t -> t.Contains "public words")
        Assert.Contains(texts, fun t -> t.Contains "unlisted words")
        Assert.DoesNotContain(texts, fun t -> t.Contains "followers words")
        Assert.DoesNotContain(texts, fun t -> t.Contains "direct words")
        Assert.Equal(3, shown.Length) // alice's and the two public ones
    }

[<Fact>]
let ``a reply to a Frunchy comment lands on the same episode and keeps in_reply_to`` () =
    task {
        let! env = newEnv ()
        use env = env

        let r =
            { reply env.ThreadId 1 with
                InReplyTo = env.AliceObjectId
                Content = "<p>replying to alice</p>" }

        do! deliver env (receivedJson r)

        let! row = remoteRow env r.ObjectId
        let fields = row.Head.Split '|'
        Assert.Equal(env.AliceObjectId, fields[3])
        Assert.Equal(env.Ep.Episode, fields[10])

        let! shown = pageComments env
        Assert.Equal<string list>([ "@bob@mastodon.example"; "alice" ], shown |> List.map _.Author)
    }

// ---------------------------------------------------------------------------
// reply.updated and object.deleted: not ordered
// ---------------------------------------------------------------------------

[<Fact>]
let ``reply.updated with a newer updated changes the text and warning, an older or equal one does not`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (receivedJson r)

        let edited =
            { r with
                EventId = "evt-2"
                Content = "<p>second thoughts</p>"
                Summary = "Spoilers: S2 E5"
                Sensitive = true
                Updated = "2026-10-12T18:10:00Z" }

        do! deliver env (updatedJson edited)
        let! row = remoteRow env r.ObjectId
        Assert.Equal<string list>([ $"https://mastodon.example/users/bob|html|public|{r.InReplyTo}|<p>second thoughts</p>|Spoilers: S2 E5|True|2026-10-12T17:59:58Z|2026-10-12T18:10:00Z|False|{env.Ep.Episode}" ], row)

        // older, then equal: both stale, the row stays
        do! deliver env (updatedJson { edited with EventId = "evt-3"; Content = "<p>older</p>"; Updated = "2026-10-12T18:05:00Z" })
        do! deliver env (updatedJson { edited with EventId = "evt-4"; Content = "<p>same instant</p>" })
        // the original copy arriving late is older than the edit too
        do! deliver env (receivedJson { r with EventId = "evt-5" })
        let! after = remoteRow env r.ObjectId
        Assert.Equal<string list>(row, after)

        // a still newer one does update
        do! deliver env (updatedJson { edited with EventId = "evt-6"; Content = "<p>third</p>"; Summary = null; Sensitive = false; Updated = "2026-10-12T18:20:00Z" })
        let! last = remoteRow env r.ObjectId
        Assert.Equal<string list>([ $"https://mastodon.example/users/bob|html|public|{r.InReplyTo}|<p>third</p>|-|False|2026-10-12T17:59:58Z|2026-10-12T18:20:00Z|False|{env.Ep.Episode}" ], last)

        let! log = eventLog env
        Assert.Equal<string list>(
            [ "evt-1|reply.received|applied"
              "evt-2|reply.updated|applied"
              "evt-3|reply.updated|stale"
              "evt-4|reply.updated|stale"
              "evt-5|reply.received|stale"
              "evt-6|reply.updated|applied" ],
            log
        )
    }

[<Fact>]
let ``reply.updated before reply.received creates the comment, and the late reply.received does not undo it`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        let edited = { r with EventId = "evt-1"; Content = "<p>edited text</p>"; Updated = "2026-10-12T18:10:00Z" }

        do! deliver env (updatedJson edited)
        let! row = remoteRow env r.ObjectId
        Assert.Equal<string list>([ $"https://mastodon.example/users/bob|html|public|{r.InReplyTo}|<p>edited text</p>|-|False|2026-10-12T17:59:58Z|2026-10-12T18:10:00Z|False|{env.Ep.Episode}" ], row)

        do! deliver env (receivedJson { r with EventId = "evt-2" })
        let! after = remoteRow env r.ObjectId
        Assert.Equal<string list>(row, after)
        let! remotes = Db.scalar<int64> env.App.Db "SELECT count(*) FROM comments WHERE remote_actor IS NOT NULL" []
        Assert.Equal(1L, remotes)
    }

[<Fact>]
let ``object.deleted tombstones the comment, and nothing later revives it`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (receivedJson r)
        let! shown = pageComments env
        Assert.Equal(2, shown.Length)

        do! deliver env (deletedJson "evt-2" r)
        let! row = remoteRow env r.ObjectId
        // the text is gone, as it is for a local delete
        Assert.Equal<string list>([ $"https://mastodon.example/users/bob|html|public|{r.InReplyTo}||-|False|2026-10-12T17:59:58Z|-|True|{env.Ep.Episode}" ], row)
        let! shown = pageComments env
        Assert.Equal<string list>([ "alice" ], shown |> List.map _.Author)

        // a repeat of the deletion (a new event id), a late edit and a late original change nothing
        do! deliver env (deletedJson "evt-3" r)
        do! deliver env (updatedJson { r with EventId = "evt-4"; Content = "<p>back?</p>"; Updated = "2026-10-12T20:00:00Z" })
        do! deliver env (receivedJson { r with EventId = "evt-5" })
        let! after = remoteRow env r.ObjectId
        Assert.Equal<string list>(row, after)
        let! shown = pageComments env
        Assert.Equal(1, shown.Length)

        let! log = eventLog env
        Assert.Equal<string list>(
            [ "evt-1|reply.received|applied"
              "evt-2|object.deleted|applied"
              "evt-3|object.deleted|stale"
              "evt-4|reply.updated|stale"
              "evt-5|reply.received|stale" ],
            log
        )
    }

[<Fact>]
let ``object.deleted before reply.received leaves a tombstone, so the late reply stays deleted`` () =
    task {
        let! env = newEnv ()
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (deletedJson "evt-1" r)

        let! row = remoteRow env r.ObjectId
        Assert.Equal(1, row.Length)
        Assert.EndsWith($"|True|{env.Ep.Episode}", row.Head)

        do! deliver env (receivedJson { r with EventId = "evt-2" })
        do! deliver env (updatedJson { r with EventId = "evt-3"; Updated = "2026-10-12T20:00:00Z" })
        let! after = remoteRow env r.ObjectId
        Assert.Equal<string list>(row, after)
        let! shown = pageComments env
        Assert.Equal<string list>([ "alice" ], shown |> List.map _.Author)

        let! log = eventLog env
        Assert.Equal<string list>([ "evt-1|object.deleted|applied"; "evt-2|reply.received|stale"; "evt-3|reply.updated|stale" ], log)
    }

[<Fact>]
let ``a local comment is never changed or deleted by an event naming its object id`` () =
    task {
        let! env = newEnv ()
        use env = env
        let! beforeRows = Db.dump env.App.Db $"SELECT content, deleted_at IS NULL, edited_at IS NULL FROM comments WHERE id = '{env.AliceCommentId}'"

        // an event whose object id is the id Megáfono gave Alice's comment
        let r = { reply env.ThreadId 1 with ObjectId = env.AliceObjectId; Content = "<p>hijacked</p>"; Updated = "2026-10-12T20:00:00Z" }
        do! deliver env (updatedJson r)
        do! deliver env (deletedJson "evt-2" r)

        let! afterRows = Db.dump env.App.Db $"SELECT content, deleted_at IS NULL, edited_at IS NULL FROM comments WHERE id = '{env.AliceCommentId}'"
        Assert.Equal<string list>(beforeRows, afterRows)
    }

// ---------------------------------------------------------------------------
// Malformed requests
// ---------------------------------------------------------------------------

/// A body of unknown length: sent chunked, with no Content-Length header
type ChunkedContent(bytes: byte[]) =
    inherit HttpContent()

    override _.SerializeToStreamAsync(stream: IO.Stream, _context: TransportContext) : Task =
        stream.WriteAsync(bytes, 0, bytes.Length)

    override _.TryComputeLength(length: byref<int64>) =
        length <- 0L
        false

[<Fact>]
let ``a body that is not JSON, not an object, or has no id or type is 400 and records nothing`` () =
    task {
        let! env = newEnv ()
        use env = env

        let bodies =
            [ "not json at all"
              ""
              "{\"id\":"
              "[1,2,3]"
              "\"text\""
              "{}"
              "{\"type\":\"reply.received\"}"
              "{\"id\":\"evt-1\"}"
              "{\"id\":42,\"type\":\"reply.received\"}"
              "{\"id\":\"\",\"type\":\"x\"}" ]

        for body in bodies do
            let! status = deliverStatus env body
            Assert.True((status = HttpStatusCode.BadRequest), $"{body}: expected 400, got {int status}")

        let! log = eventLog env
        Assert.Empty log
    }

[<Fact>]
let ``a body over 1 MB is 413 before anything is read, but the token is checked first`` () =
    task {
        let! env = newEnv ()
        use env = env
        let big = "{\"id\":\"evt-1\",\"type\":\"reply.received\",\"pad\":\"" + String('a', 1_100_000) + "\"}"

        let! status = deliverStatus env big
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status)

        // without the token it is the 401, never the size answer
        let! status = sendRaw env.App None (Some(signedAt env.Now big)) big
        Assert.Equal(HttpStatusCode.Unauthorized, status)

        // no Content-Length (chunked): the cap is enforced while reading
        use request = new HttpRequestMessage(HttpMethod.Post, "/megafono/webhooks")
        request.Headers.TryAddWithoutValidation("Authorization", bearer) |> ignore
        request.Headers.TryAddWithoutValidation("Megafono-Signature", signedAt env.Now big) |> ignore
        request.Content <- new ChunkedContent(Encoding.UTF8.GetBytes big)
        use! response = env.App.Client.SendAsync request
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode)

        // a body just under the limit is read and handled
        let nearly = "{\"id\":\"evt-2\",\"type\":\"x\",\"pad\":\"" + String('a', 1_000_000) + "\"}"
        let! status = deliverStatus env nearly
        Assert.Equal(HttpStatusCode.NoContent, status)

        let! log = eventLog env
        Assert.Equal<string list>([ "evt-2|x|ignored" ], log)
    }
