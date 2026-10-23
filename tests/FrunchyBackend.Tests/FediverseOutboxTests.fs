/// Feature 015 (US2, T007 and T009): comments and account changes go to Megáfono through the outbox worker.
/// The tests drive `FediverseOutbox.processNext` directly (`drainOutbox`) against the fake Megáfono, the way the
/// import tests drive `ImportProcessor.processNext`: the hosted worker stays off.
module FrunchyBackend.Tests.FediverseOutboxTests

open System
open System.Net
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.FakeMegafono
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers

// The enriched 2026 sample with federation on. "alice" and "bob" comment; every app stays under the
// `comment-write` limit (10 POSTs a minute per IP).

let private numberedEpisode (db: FrunchyBackend.Infrastructure.Database.DbConfig) =
    pickEpisode db "e.episode_number IS NOT NULL AND sea.season_number IS NOT NULL AND e.title IS NOT NULL"

/// The federated app with the 2026 sample imported and enriched
let private federatedSample () : Task<TestApp> =
    task {
        let! app = newFederatedApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app
        return app
    }

let private userId (app: TestApp) (username: string) =
    Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box username ]

let private commentId (app: TestApp) (content: string) =
    Db.scalar<string> app.Db "SELECT id FROM comments WHERE content = @c ORDER BY created_at LIMIT 1" [ "c", box content ]

let private objectOf (r: Recorded) = r.Json.GetProperty "object"

let private text (json: JsonElement) (name: string) = json.GetProperty(name).GetString()

let private isNull' (json: JsonElement) (name: string) = json.GetProperty(name).ValueKind = JsonValueKind.Null

let private publishes (app: TestApp) = app.Megafono.RequestsTo "POST /internal/actors/*/objects"

let private outbox (app: TestApp) =
    Db.dump app.Db "SELECT kind, state, attempts, coalesce(last_error, '-') FROM fediverse_outbox ORDER BY id"

let private edit (browser: Browser.Browser) (id: string) (content: string) (spoiler: bool) =
    browser.PostForm($"/comments/{id}/edit", $"/comments/{id}/edit", [ "content", content ] @ (if spoiler then [ "spoiler", "on" ] else []))

let private delete (browser: Browser.Browser) (id: string) =
    browser.PostForm($"/comments/{id}/edit", $"/comments/{id}/delete", [])

let private nextAttemptAt (app: TestApp) (kind: string) =
    Db.scalar<DateTime> app.Db "SELECT next_attempt_at FROM fediverse_outbox WHERE kind = @k ORDER BY id LIMIT 1" [ "k", box kind ]

let private within (expected: DateTimeOffset) (actual: DateTime) =
    Assert.True(abs ((DateTime.SpecifyKind(actual, DateTimeKind.Utc) - expected.UtcDateTime).TotalSeconds) < 1.0, $"{actual:O} is not {expected:O}")

// ---------------------------------------------------------------------------
// Publishing
// ---------------------------------------------------------------------------

[<Fact>]
let ``the first comment on a group publishes the thread from the series actor, then the reply from the user, and stores both ids`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        let! facts = episodeFacts app.Db ep
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Alice's take" true

        // queued in the comment's transaction: the thread, then the comment
        let! queued = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|0|-"; "publish_comment|pending|0|-" ], queued)
        Assert.Equal(0, app.Megafono.Requests.Length)

        let! processed = drainOutbox app
        Assert.Equal(2, processed)

        let calls = publishes app
        Assert.Equal(2, calls.Length)
        let thread, reply = calls[0], calls[1]

        // the thread: the series actor, the key, the Note
        Assert.Equal($"/internal/actors/series-{ep.Series}/objects", thread.Path)
        Assert.StartsWith("Bearer ", thread.Authorization.Value)
        let! groupKey =
            Db.scalar<string> app.Db "SELECT group_key FROM episode_threads" []
        Assert.Equal(Some $"thread:{ep.Series}:{groupKey}", thread.IdempotencyKey)
        Assert.Equal("public", text thread.Json "audience")
        let note = objectOf thread
        Assert.Equal("Note", text note "type")
        Assert.Equal($"https://frunchy.test/anime/{ep.Series}/episode/{ep.Episode}", text note "url")
        Assert.True(isNull' note "inReplyTo")
        Assert.False(note.GetProperty("sensitive").GetBoolean())
        let! seriesTitle = Db.scalar<string> app.Db "SELECT coalesce(s.title, e.series_title, e.series_id) FROM cr_episodes e LEFT JOIN cr_series s ON s.id = e.series_id WHERE e.id = @e" [ "e", box ep.Episode ]
        let expectedTitle = $"{seriesTitle}: Episode {facts.Number} {facts.Title}".TrimEnd()
        Assert.Equal("<p>" + WebUtility.HtmlEncode expectedTitle + "</p>", text note "content")

        // the reply: the user's actor, `comment:{id}`, inReplyTo = the thread's URL, the spoiler warning
        let! alicesId = userId app "alice"
        let! id = commentId app "Alice's take"
        Assert.Equal($"/internal/actors/{alicesId}/objects", reply.Path)
        Assert.Equal(Some $"comment:{id}", reply.IdempotencyKey)
        Assert.Equal("public", text reply.Json "audience")
        let replyNote = objectOf reply
        Assert.Equal("<p>Alice&#39;s take</p>", text replyNote "content")
        Assert.Equal($"https://frunchy.test/anime/{ep.Series}/episode/{ep.Episode}#comment-{id}", text replyNote "url")
        Assert.True(replyNote.GetProperty("sensitive").GetBoolean())
        Assert.StartsWith("Spoilers: ", text replyNote "summary")

        // both object ids are stored, the reply points at the thread's URL
        let! threadRow = Db.dump app.Db "SELECT object_id, object_url, cr_episode_id, series_id FROM episode_threads"
        Assert.Equal<string list>([ $"obj-1|https://frunchy.test/ap/objects/obj-1|{ep.Episode}|{ep.Series}" ], threadRow)
        Assert.Equal("https://frunchy.test/ap/objects/obj-1", text replyNote "inReplyTo")
        let! stored = Db.scalar<string> app.Db "SELECT object_id FROM comments WHERE id = @id" [ "id", box id ]
        Assert.Equal("obj-2", stored)

        // the series actor has a handle by now (the lookup needs it)
        let! handles = Db.count app.Db "series_handles"
        Assert.Equal(1L, handles)

        let! done' = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|0|-"; "publish_comment|done|0|-" ], done')
    }

[<Fact>]
let ``a repeated worker run sends nothing`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Once" false
        let! _ = drainOutbox app
        let sent = app.Megafono.Requests.Length

        let! again = drainOutbox app
        let! later = drainOutboxAt app (DateTimeOffset.UtcNow.AddHours 3.0)
        Assert.Equal(0, again)
        Assert.Equal(0, later)
        Assert.Equal(sent, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``a second comment on the group sends only a reply`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        use! bob = signedUp app "sub-b" "bob"
        let! _ = postComment alice ep.Series ep.Episode "First" false
        let! _ = drainOutbox app
        let! _ = postComment bob ep.Series ep.Episode "Second" false

        let! queued = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|0|-"; "publish_comment|done|0|-"; "publish_comment|pending|0|-" ], queued)

        app.Megafono.Clear()
        let! processed = drainOutbox app
        Assert.Equal(1, processed)

        let! bobsId = userId app "bob"
        let calls = publishes app
        Assert.Equal<string list>([ $"/internal/actors/{bobsId}/objects" ], calls |> List.map _.Path)
        Assert.Equal("https://frunchy.test/ap/objects/obj-1", text (objectOf calls[0]) "inReplyTo")
        Assert.True(isNull' (objectOf calls[0]) "summary")
        Assert.False((objectOf calls[0]).GetProperty("sensitive").GetBoolean())
        let! threads = Db.count app.Db "episode_threads"
        Assert.Equal(1L, threads)
    }

[<Fact>]
let ``a dub and its original share one thread`` () =
    task {
        use! app = federatedSample ()

        let! rows =
            Db.dump
                app.Db
                """SELECT dub.series_id, dub.id, orig.id
                   FROM cr_episodes dub
                   JOIN cr_season_matches m ON m.season_id = dub.season_id AND m.status = 'matched' AND m.method = 'dub'
                   JOIN cr_episodes orig ON orig.season_id = m.shared_from AND orig.episode_number = dub.episode_number
                   WHERE m.shared_from = 'GRK5CNJ14' AND dub.duration_ms IS NOT NULL AND orig.duration_ms IS NOT NULL
                     AND dub.series_id = orig.series_id
                   ORDER BY dub.id, orig.id LIMIT 1"""

        let p = (List.exactlyOne rows).Split '|'
        let series, dub, orig = p[0], p[1], p[2]
        use! alice = signedUp app "sub-a" "alice"
        use! bob = signedUp app "sub-b" "bob"
        let! _ = postComment alice series orig "on the original" false
        let! _ = postComment bob series dub "on the dub" false

        let! _ = drainOutbox app

        // one thread, from the series actor, and both replies hang from it
        Assert.Equal(1, app.Megafono.Count "POST /internal/actors/series-*/objects")
        let replies = publishes app |> List.filter (fun r -> not (r.Path.Contains "series-"))
        Assert.Equal(2, replies.Length)
        Assert.All(replies, fun r -> Assert.Equal("https://frunchy.test/ap/objects/obj-1", text (objectOf r) "inReplyTo"))
        let! threads = Db.count app.Db "episode_threads"
        Assert.Equal(1L, threads)

        // each comment links to the episode it was written on
        Assert.Contains($"/episode/{orig}#comment-", text (objectOf replies[0]) "url")
        Assert.Contains($"/episode/{dub}#comment-", text (objectOf replies[1]) "url")
    }

[<Fact>]
let ``a 503 twice then a 201: the waits grow and nothing is sent twice`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Persistent" false

        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        app.Megafono.FailNext 2

        // the thread fails, the comment waits for it
        let! first = drainOutboxAt app t0
        Assert.Equal(2, first)
        let! afterFirst = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|1|503 app_unavailable"; "publish_comment|pending|1|waiting for thread" ], afterFirst)
        let! firstWait = nextAttemptAt app "ensure_thread"
        within (t0.AddMinutes 1.0) firstWait

        // nothing is due before the wait is over
        let! early = drainOutboxAt app (t0.AddSeconds 30.0)
        Assert.Equal(0, early)
        Assert.Equal(1, app.Megafono.Requests.Length)

        // the second failure waits twice as long
        let t1 = t0.AddMinutes 2.0
        let! second = drainOutboxAt app t1
        Assert.Equal(2, second)
        let! afterSecond = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|2|503 app_unavailable"; "publish_comment|pending|2|waiting for thread" ], afterSecond)
        let! secondWait = nextAttemptAt app "ensure_thread"
        within (t1.AddMinutes 2.0) secondWait
        Assert.Equal(2, app.Megafono.Requests.Length)

        // then it goes through: the thread, then the comment
        let! third = drainOutboxAt app (t1.AddMinutes 10.0)
        Assert.Equal(2, third)
        let! finalRows = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|2|-"; "publish_comment|done|2|-" ], finalRows)

        // three calls for the thread (two 503s and the 201), one for the comment, and nothing after
        Assert.Equal(3, app.Megafono.Count "POST /internal/actors/series-*/objects")
        Assert.Equal(1, publishes app |> List.filter (fun r -> not (r.Path.Contains "series-")) |> List.length)
        let sent = app.Megafono.Requests.Length
        let! again = drainOutboxAt app (t1.AddHours 5.0)
        Assert.Equal(0, again)
        Assert.Equal(sent, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``a comment edited before it is published goes out once, with the latest text, and no update`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "First draft" false
        let! id = commentId app "First draft"
        let! response = edit alice id "Final text" false
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        // the pending, unclaimed publish will read the latest text: no update is queued
        let! queued = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|0|-"; "publish_comment|pending|0|-" ], queued)

        let! _ = drainOutbox app

        let replies = publishes app |> List.filter (fun r -> not (r.Path.Contains "series-"))
        Assert.Equal(1, replies.Length)
        Assert.Equal("<p>Final text</p>", text (objectOf replies[0]) "content")
        Assert.Equal(0, app.Megafono.Count "PUT /internal/objects/*")
        let! rows = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|0|-"; "publish_comment|done|0|-" ], rows)
    }

[<Fact>]
let ``an edit while the publish is claimed still queues its update`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "First draft" false
        let! id = commentId app "First draft"

        // the publish is in flight (claimed, not yet done) when the edit arrives
        do! Db.execute app.Db $"UPDATE fediverse_outbox SET claimed_at = now() WHERE kind = 'publish_comment' AND subject = '{id}'"

        let! _ = edit alice id "Final text" false
        let! queued = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|0|-"; "publish_comment|pending|0|-"; "update_comment|pending|0|-" ], queued)
    }

[<Fact>]
let ``a comment deleted before it is published is never published`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Regretted" false
        let! id = commentId app "Regretted"
        let! response = delete alice id
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        let! _ = drainOutbox app

        // no reply, no delete: only the thread went out, and the comment row has no object id
        Assert.Equal<string list>([ $"/internal/actors/series-{ep.Series}/objects" ], publishes app |> List.map _.Path)
        Assert.Equal(0, app.Megafono.Count "DELETE /internal/objects/*")
        let! objectId = Db.scalar<obj> app.Db "SELECT object_id FROM comments WHERE id = @id" [ "id", box id ]
        Assert.True(objectId :? DBNull)
        let! rows = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|0|-"; "publish_comment|done|0|-"; "delete_comment|done|0|-" ], rows)
    }

[<Fact>]
let ``an edit after publishing is a PUT of the stored object`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Before" false
        let! _ = drainOutbox app
        let! id = commentId app "Before"
        let! _ = edit alice id "After" true
        app.Megafono.Clear()

        let! processed = drainOutbox app
        Assert.Equal(1, processed)

        let puts = app.Megafono.RequestsTo "PUT /internal/objects/*"
        let put = List.exactlyOne puts
        Assert.Equal("/internal/objects/obj-2", put.Path)
        Assert.Equal("Note", text put.Json "type")
        Assert.Equal("<p>After</p>", text put.Json "content")
        Assert.Equal($"https://frunchy.test/anime/{ep.Series}/episode/{ep.Episode}#comment-{id}", text put.Json "url")
        Assert.True(put.Json.GetProperty("sensitive").GetBoolean())
        Assert.StartsWith("Spoilers: ", text put.Json "summary")
        Assert.Equal(0, (publishes app).Length)
    }

[<Fact>]
let ``a delete after publishing is a DELETE of the stored object`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Short lived" false
        let! _ = drainOutbox app
        let! id = commentId app "Short lived"
        let! _ = delete alice id
        app.Megafono.Clear()

        let! processed = drainOutbox app
        Assert.Equal(1, processed)
        Assert.Equal<string list>([ "DELETE /internal/objects/obj-2" ], app.Megafono.Requests |> List.map _.Key)
        let! rows = outbox app
        Assert.Equal("delete_comment|done|0|-", List.last rows)
    }

// ---------------------------------------------------------------------------
// Accounts
// ---------------------------------------------------------------------------

[<Fact>]
let ``deleting an account on the web sends DELETE for its actor`` () =
    task {
        use! app = newFederatedApp ()
        use! alice = signedUp app "sub-a" "alice"
        let! id = userId app "alice"

        let! response = alice.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "alice" ])
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)

        // the item names the user id and outlives the user row
        let! rows = Db.dump app.Db "SELECT kind, subject, state FROM fediverse_outbox"
        Assert.Equal<string list>([ $"delete_actor|{id}|pending" ], rows)

        let! _ = drainOutbox app
        Assert.Equal<string list>([ $"DELETE /internal/actors/{id}" ], app.Megafono.Requests |> List.map _.Key)
        let! done' = outbox app
        Assert.Equal<string list>([ "delete_actor|done|0|-" ], done')
    }

[<Fact>]
let ``deleting an account from the command line sends DELETE, and a 404 counts as done`` () =
    task {
        use! app = newFederatedApp ()
        let! _ = createUser app.Db "bob"
        let! id = userId app "bob"
        app.Megafono.Route("DELETE /internal/actors/*", [ Status 404 ])

        let! deleted = DeleteUser.run app.Db true [ "bob"; "--yes" ]
        Assert.Equal(0, deleted)

        let! _ = drainOutbox app
        Assert.Equal<string list>([ $"DELETE /internal/actors/{id}" ], app.Megafono.Requests |> List.map _.Key)
        let! rows = outbox app
        Assert.Equal<string list>([ "delete_actor|done|0|404 not_found" ], rows)
    }

[<Fact>]
let ``changing the display name sends a profile refresh`` () =
    task {
        use! app = newFederatedApp ()
        use! alice = signedUp app "sub-a" "alice"
        let! id = userId app "alice"

        let! response = alice.PostForm("/account", "/account/display-name", [ "displayName", "Alice Cooper" ])
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        // loading the account page (for the form's token) listed the user's blocks: not what is under test
        app.Megafono.Clear()
        let! _ = drainOutbox app
        Assert.Equal<string list>([ $"PUT /internal/actors/{id}" ], app.Megafono.Requests |> List.map _.Key)
        let! rows = outbox app
        Assert.Equal<string list>([ "refresh_profile|done|0|-" ], rows)
    }

[<Fact>]
let ``a user whose username can't be a handle has no comment sent`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! old = signedUp app "sub-o" "oldtimer"
        // an account from before the no-trailing-dash rule
        do! Db.execute app.Db "UPDATE users SET username = 'oldtimer-' WHERE username = 'oldtimer'"

        let! response = postComment old ep.Series ep.Episode "From an old account" false
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        let! queued = outbox app
        Assert.Empty queued
        let! threads = Db.count app.Db "episode_threads"
        Assert.Equal(0L, threads)
        let! _ = drainOutbox app
        Assert.Empty app.Megafono.Requests
    }

[<Fact>]
let ``with federation off nothing is queued`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Local only" false
        let! id = commentId app "Local only"
        let! _ = edit alice id "Edited" false
        let! _ = delete alice id
        let! _ = alice.PostForm("/account", "/account/display-name", [ "displayName", "Renamed" ])
        let! _ = alice.PostForm("/account/delete", "/account/delete", [ "confirmUsername", "alice" ])
        let! _ = DeleteUser.deleteUser app.Db false "animenerd"

        let! queued = Db.count app.Db "fediverse_outbox"
        Assert.Equal(0L, queued)
        let! threads = Db.count app.Db "episode_threads"
        Assert.Equal(0L, threads)
    }

// ---------------------------------------------------------------------------
// Failures and ordering
// ---------------------------------------------------------------------------

[<Fact>]
let ``no secret reaches last_error, and a network failure is recorded as such`` () =
    task {
        use! app = newFederatedApp ()
        let! _ = createUser app.Db "bob"
        let! id = userId app "bob"

        // a refusal whose message repeats the secrets: only the status and the error code are kept
        let leaky = $$"""{"error":"app_unavailable","message":"{{Token}} {{WebhookSecret}} Bearer {{Token}}"}"""
        app.Megafono.Route("PUT /internal/actors/*", [ Body(503, leaky) ])
        let! _ = DeleteUser.deleteUser app.Db true "bob"
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject) VALUES ('refresh_profile', '{id}')"

        // refresh_profile (queued second) and delete_actor share the subject: delete_actor goes first
        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        let! _ = drainOutboxAt app t0
        let! afterRefusal = Db.dump app.Db "SELECT kind, coalesce(last_error, '') FROM fediverse_outbox ORDER BY id"
        Assert.Equal<string list>([ "delete_actor|"; "refresh_profile|503 app_unavailable" ], afterRefusal)

        // a transport failure
        app.Megafono.Route("PUT /internal/actors/*", [ Throw(Net.Http.HttpRequestException "boom") ])
        let! _ = drainOutboxAt app (t0.AddMinutes 2.0)
        let! afterThrow = Db.scalar<string> app.Db "SELECT last_error FROM fediverse_outbox WHERE kind = 'refresh_profile'" []
        Assert.Equal("unreachable", afterThrow)

        for line in [ afterRefusal |> String.concat "\n"; afterThrow ] do
            Assert.DoesNotContain(Token, line)
            Assert.DoesNotContain(WebhookSecret, line)
    }

[<Fact>]
let ``a permanent refusal is failed with its reason, a retry never repeats a failed item`` () =
    task {
        use! app = newFederatedApp ()
        let id = Guid.NewGuid()
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject) VALUES ('refresh_profile', '{id}')"
        app.Megafono.Route("PUT /internal/actors/*", [ Status 400 ])

        let! _ = drainOutbox app
        let! rows = outbox app
        Assert.Equal<string list>([ "refresh_profile|failed|1|400 invalid_request" ], rows)
        let! again = drainOutboxAt app (DateTimeOffset.UtcNow.AddHours 2.0)
        Assert.Equal(0, again)
        Assert.Equal(1, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``items of one subject run in order: a failing first item holds back the second`` () =
    task {
        use! app = newFederatedApp ()
        let id = Guid.NewGuid()
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject) VALUES ('refresh_profile', '{id}'), ('delete_actor', '{id}')"
        app.Megafono.FailNext 1

        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        let! first = drainOutboxAt app t0
        Assert.Equal(1, first)
        Assert.Equal<string list>([ $"PUT /internal/actors/{id}" ], app.Megafono.Requests |> List.map _.Key)

        // once the first goes through, the second follows
        let! second = drainOutboxAt app (t0.AddMinutes 2.0)
        Assert.Equal(2, second)
        Assert.Equal<string list>(
            [ $"PUT /internal/actors/{id}"; $"PUT /internal/actors/{id}"; $"DELETE /internal/actors/{id}" ],
            app.Megafono.Requests |> List.map _.Key
        )
    }

[<Fact>]
let ``a claim older than ten minutes is retried, a fresh one is left alone`` () =
    task {
        use! app = newFederatedApp ()
        let id = Guid.NewGuid()
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject, claimed_at) VALUES ('refresh_profile', '{id}', now())"

        let! fresh = drainOutbox app
        Assert.Equal(0, fresh)

        do! Db.execute app.Db "UPDATE fediverse_outbox SET claimed_at = now() - interval '11 minutes'"
        let! stale = drainOutbox app
        Assert.Equal(1, stale)
        Assert.Equal(1, app.Megafono.Requests.Length)
    }

// ---------------------------------------------------------------------------
// The episode page points to its thread (T009, research R8)
// ---------------------------------------------------------------------------

let private alternateLinks (html: string) =
    [ for m in Regex.Matches(html, "<link\\b[^>]*\\brel=\"alternate\"[^>]*>") -> m.Value ]

let private linkHeader (response: Net.Http.HttpResponseMessage) =
    match response.Headers.TryGetValues "Link" with
    | true, values -> Some(String.Join(",", values))
    | _ -> None

[<Fact>]
let ``the episode page advertises its thread once the thread is published`` () =
    task {
        use! app = federatedSample ()
        let! ep = numberedEpisode app.Db
        use! alice = signedUp app "sub-a" "alice"

        // no thread yet: no link, no header
        let! before = getWithLanguage app.Client "en" (episodeUrl ep.Series ep.Episode)
        let! beforeHtml = readHtml before
        Assert.Empty(alternateLinks beforeHtml)
        Assert.Equal(None, linkHeader before)

        // queued but not published yet: still none
        let! _ = postComment alice ep.Series ep.Episode "Hello fediverse" false
        let! queued = getWithLanguage app.Client "en" (episodeUrl ep.Series ep.Episode)
        Assert.Equal(None, linkHeader queued)

        let! _ = drainOutbox app
        let! after = getWithLanguage app.Client "en" (episodeUrl ep.Series ep.Episode)
        let! html = readHtml after
        let url = "https://frunchy.test/ap/objects/obj-1"
        let links = alternateLinks html
        Assert.Equal(1, links.Length)
        Assert.Contains("type=\"application/activity+json\"", links[0])
        Assert.Contains($"href=\"{url}\"", links[0])
        Assert.Equal(Some $"<{url}>; rel=\"alternate\"; type=\"application/activity+json\"", linkHeader after)

        // the link sits in the head
        Assert.True(html.IndexOf links[0] < html.IndexOf "</head>")
    }

[<Fact>]
let ``with federation off the episode page has no alternate link`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = numberedEpisode app.Db
        let! response = getWithLanguage app.Client "en" (episodeUrl ep.Series ep.Episode)
        let! html = readHtml response
        Assert.Empty(alternateLinks html)
        Assert.Equal(None, linkHeader response)
    }

// ---------------------------------------------------------------------------
// Permanent refusals leave the queue (the thread's refusal reaches its comments)
// ---------------------------------------------------------------------------

/// alice's first comment on a numbered episode: an ensure_thread and a publish_comment are queued
let private aliceComments (app: TestApp) =
    task {
        let! ep = numberedEpisode app.Db
        let! alice = signedUp app "sub-a" "alice"
        let! _ = postComment alice ep.Series ep.Episode "Hello fediverse" false
        return ep, alice
    }

[<Fact>]
let ``a thread Megafono refuses for good fails, its comment fails with it, and neither is retried`` () =
    task {
        use! app = federatedSample ()
        let! _, alice = aliceComments app
        use _alice = alice
        app.Megafono.Route("POST /internal/actors/series-*/objects", [ Status 400 ])

        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        let! first = drainOutboxAt app t0
        Assert.Equal(2, first)

        let! rows = outbox app
        Assert.Equal<string list>([ "ensure_thread|failed|1|400 invalid_request"; "publish_comment|failed|1|thread refused" ], rows)

        // nothing is left waiting, however long we wait, and Megafono hears nothing more
        let! pending = Db.scalar<int64> app.Db "SELECT count(*) FROM fediverse_outbox WHERE state = 'pending'" []
        Assert.Equal(0L, pending)
        let! again = drainOutboxAt app (t0.AddHours 3.0)
        Assert.Equal(0, again)
        Assert.Equal(1, app.Megafono.Requests.Length)

        // the comment is still on Frunchy
        let! comments = Db.count app.Db "comments"
        Assert.Equal(1L, comments)
    }

[<Fact>]
let ``a comment's own permanent refusal fails it and is not retried`` () =
    task {
        use! app = federatedSample ()
        let! _, alice = aliceComments app
        use _alice = alice

        // the thread goes through (the series route is checked first and has no script), the comment is refused
        app.Megafono.Route("POST /internal/actors/series-*/objects", [])
        app.Megafono.Route("POST /internal/actors/*/objects", [ Status 400 ])
        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        let! _ = drainOutboxAt app t0
        let! rows = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|0|-"; "publish_comment|failed|1|400 invalid_request" ], rows)
        let! again = drainOutboxAt app (t0.AddHours 3.0)
        Assert.Equal(0, again)
    }

[<Fact>]
let ``a pending thread retry keeps the comment waiting after an older thread failed`` () =
    task {
        use! app = federatedSample ()
        let! _, alice = aliceComments app
        use _alice = alice

        // an older ensure_thread failed for good, but a newer one is pending: the comment keeps waiting
        do! Db.execute app.Db "UPDATE fediverse_outbox SET state = 'failed', last_error = '400 invalid_request' WHERE kind = 'ensure_thread'"
        let! groupKey = Db.scalar<string> app.Db "SELECT subject FROM fediverse_outbox WHERE kind = 'ensure_thread'" []
        do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject, next_attempt_at) VALUES ('ensure_thread', '{groupKey}', now() + interval '1 day')"

        let! _ = drainOutbox app
        let! rows = outbox app
        Assert.Equal<string list>(
            [ "ensure_thread|failed|0|400 invalid_request"; "publish_comment|pending|1|waiting for thread"; "ensure_thread|pending|0|-" ],
            rows
        )
    }

[<Fact>]
let ``a 503 and a 429 keep the backoff, and any other 4xx refuses for good`` () =
    task {
        use! app = newFederatedApp ()
        let mutable now = DateTimeOffset.UtcNow.AddSeconds 5.0

        let stateAfter (status: int) =
            task {
                let id = Guid.NewGuid()
                do! Db.execute app.Db $"INSERT INTO fediverse_outbox (kind, subject) VALUES ('refresh_profile', '{id}')"
                app.Megafono.Route("PUT /internal/actors/*", [ Status status ])
                now <- now.AddHours 3.0
                let! _ = drainOutboxAt app now
                return! Db.dump app.Db $"SELECT state || '|' || attempts FROM fediverse_outbox WHERE subject = '{id}'"
            }

        for status in [ 401; 408; 409; 429; 500; 502; 503 ] do
            let! row = stateAfter status
            Assert.Equal<string list>([ "pending|1" ], row)

        for status in [ 400; 403; 422 ] do
            let! row = stateAfter status
            Assert.Equal<string list>([ "failed|1" ], row)
    }

[<Fact>]
let ``a retried 503 on a thread still goes through later`` () =
    task {
        use! app = federatedSample ()
        let! _, alice = aliceComments app
        use _alice = alice

        app.Megafono.Route("POST /internal/actors/series-*/objects", [ Status 503 ])
        let t0 = DateTimeOffset.UtcNow.AddSeconds 5.0
        let! _ = drainOutboxAt app t0
        let! afterFail = outbox app
        Assert.Equal<string list>([ "ensure_thread|pending|1|503 app_unavailable"; "publish_comment|pending|1|waiting for thread" ], afterFail)

        // Megafono recovers: the thread, then the comment
        app.Megafono.Route("POST /internal/actors/series-*/objects", [])
        let! _ = drainOutboxAt app (t0.AddMinutes 10.0)
        let! final = outbox app
        Assert.Equal<string list>([ "ensure_thread|done|1|-"; "publish_comment|done|1|-" ], final)
    }

[<Fact>]
let ``only the author sees that a refused comment was not sent`` () =
    task {
        use! app = federatedSample ()
        let! ep, alice = aliceComments app
        use _alice = alice
        use! bob = signedUp app "sub-b" "bob"

        let page = episodeUrl ep.Series ep.Episode
        let! before = alice.Get page
        Assert.False(hasHook "comment-not-federated" before.Html)

        app.Megafono.Route("POST /internal/actors/series-*/objects", [ Status 400 ])
        let! _ = drainOutbox app

        let! mine = alice.Get page
        Assert.True(hasHook "comment-not-federated" mine.Html)
        Assert.Contains("Not sent to the fediverse; it's still here on Frunchy.", WebUtility.HtmlDecode mine.Html)

        let! others = bob.Get page
        Assert.False(hasHook "comment-not-federated" others.Html)
        Assert.Contains("Hello fediverse", others.Html)

        let! signedOut = getWithLanguage app.Client "en" page
        let! signedOutHtml = readHtml signedOut
        Assert.False(hasHook "comment-not-federated" signedOutHtml)
        Assert.Contains("Hello fediverse", signedOutHtml)
    }

[<Fact>]
let ``the not-sent note is in Spanish for a Spanish browser`` () =
    task {
        use! app = federatedSample ()
        let! ep, alice = aliceComments app
        use _alice = alice
        app.Megafono.Route("POST /internal/actors/series-*/objects", [ Status 400 ])
        let! _ = drainOutbox app

        let page = episodeUrl ep.Series ep.Episode
        let! _ = alice.Get($"/language/es?returnUrl={Uri.EscapeDataString page}")
        let! spanish = alice.Get page
        Assert.True(hasHook "comment-not-federated" spanish.Html)
        Assert.Contains("No se envió al fediverso; sigue aquí en Frunchy.", WebUtility.HtmlDecode spanish.Html)
    }

[<Fact>]
let ``a comment that was published has no not-sent note`` () =
    task {
        use! app = federatedSample ()
        let! ep, alice = aliceComments app
        use _alice = alice
        let! _ = drainOutbox app
        let! mine = alice.Get(episodeUrl ep.Series ep.Episode)
        Assert.False(hasHook "comment-not-federated" mine.Html)
    }
