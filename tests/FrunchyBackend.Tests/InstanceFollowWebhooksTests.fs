/// Feature 028 (T007): the events of a followed Frunchy instance. `follow.*` move our series follows, `post.*` bring
/// the other instance's episode threads and (the payload shape Megáfono will send once it announces replies) the
/// comments under them. Bodies are signed like MegafonoWebhooksTests'; the world is its env (a federated app with the
/// 2026 sample and one local thread), plus a series follow of the env's series on `other.test`.
module FrunchyBackend.Tests.InstanceFollowWebhooksTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.MegafonoWebhooksTests
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let domain = "other.test"

// ---------------------------------------------------------------------------
// The world
// ---------------------------------------------------------------------------

let private seriesActor (env: Env) = $"https://{domain}/ap/actors/series-{env.Ep.Series}"

/// The env with our series account following the same series on other.test
let private followedEnv (state: string) : Task<Env> =
    task {
        let! env = newEnv ()

        do!
            Db.execute
                env.App.Db
                $"""INSERT INTO series_follows (series_id, domain, remote_actor, source, state)
                    VALUES ('{env.Ep.Series}', '{domain}', '{seriesActor env}', 'series', '{state}')"""

        return env
    }

// ---------------------------------------------------------------------------
// Payloads
// ---------------------------------------------------------------------------

let actorJson (id: string) (handle: string) =
    {| id = id; handle = handle; name = "Frieren"; iconUrl = null; profileUrl = null |}

/// `follow.accepted|rejected|removed` for the user `series-X`
let private followJson (eventId: string) (eventType: string) (userId: string) (direction: string) (actor: string) =
    JsonSerializer.Serialize(
        {| id = eventId
           version = 1
           ``type`` = eventType
           occurredAt = "2026-10-12T18:00:00Z"
           seq = 5
           user = {| id = userId; handle = "frieren" |}
           actor = actorJson actor "@frieren@other.test"
           follow = {| id = "follow-77"; direction = direction; status = "accepted" |}
           extensions = null |}
    )

type Post =
    { EventId: string
      ObjectId: string
      ActorId: string
      Handle: string
      InReplyTo: string
      Url: string
      Content: string
      Summary: string
      Sensitive: bool
      Audience: string
      Published: string
      Updated: string
      /// The whole `extensions` value, or null
      Extensions: obj }

/// What Megáfono sends for a thread, with the ids under our namespace
let ours (series: string) (episode: string) (groupKey: string) : obj =
    box
        {| ``namespace`` = Fediverse.ExtensionNamespace
           prefix = "frunchy"
           data = {| episode = {| seriesId = series; episodeId = episode; groupKey = groupKey |} |} |}

/// A thread of `series` by the other instance's series actor; `n` makes the ids
let threadOf (series: string) (n: int) : Post =
    { EventId = $"p-{n}"
      ObjectId = $"https://{domain}/ap/objects/t{n}"
      ActorId = $"https://{domain}/ap/actors/series-{series}"
      Handle = "@frieren@other.test"
      InReplyTo = null
      Url = $"https://{domain}/anime/{series}/episode/REMOTEEP{n}"
      Content = "<p>Frieren: Episode 5</p>"
      Summary = null
      Sensitive = false
      Audience = "public"
      Published = "2026-10-12T17:00:00Z"
      Updated = null
      Extensions = ours series $"REMOTEEP{n}" null }

let private thread (env: Env) (n: int) : Post = threadOf env.Ep.Series n

/// A comment by Bob (on mastodon.example) replying to `inReplyTo`
let comment (n: int) (inReplyTo: string) : Post =
    { EventId = $"p-c{n}"
      ObjectId = $"https://mastodon.example/users/bob/statuses/{n}"
      ActorId = "https://mastodon.example/users/bob"
      Handle = "@bob@mastodon.example"
      InReplyTo = inReplyTo
      Url = $"https://mastodon.example/@bob/{n}"
      Content = "<p>that ending!</p>"
      Summary = null
      Sensitive = false
      Audience = "public"
      Published = "2026-10-12T18:00:00Z"
      Updated = null
      Extensions = null }

let postJson (eventType: string) (p: Post) =
    JsonSerializer.Serialize(
        {| id = p.EventId
           version = 1
           ``type`` = eventType
           occurredAt = "2026-10-12T18:00:00Z"
           seq = 9
           user = {| id = "series-ANYTHING"; handle = "x" |}
           actor = actorJson p.ActorId p.Handle
           ``object`` =
            {| id = p.ObjectId
               url = p.Url
               ``type`` = "Note"
               content = p.Content
               summary = p.Summary
               sensitive = p.Sensitive
               language = "en"
               published = p.Published
               updated = p.Updated
               audience = p.Audience
               inReplyTo = p.InReplyTo
               attachment = [||] |}
           extensions = p.Extensions |}
    )

let received = postJson "post.received"
let updated = postJson "post.updated"

let deletedJson (eventId: string) (objectId: string) =
    JsonSerializer.Serialize(
        {| id = eventId
           version = 1
           ``type`` = "post.deleted"
           occurredAt = "2026-10-12T19:00:00Z"
           seq = 10
           user = {| id = "series-ANYTHING"; handle = "x" |}
           actor = actorJson "https://mastodon.example/users/bob" "@bob@mastodon.example"
           ``object`` = {| id = objectId |}
           extensions = null |}
    )

// ---------------------------------------------------------------------------
// Reading what was stored
// ---------------------------------------------------------------------------

let private threads (env: Env) =
    Db.dump
        env.App.Db
        """SELECT object_id, domain, series_id, remote_episode_id, coalesce(remote_group_key, '-'), coalesce(cr_episode_id, '-'),
                  coalesce(url, '-'), deleted_at IS NOT NULL
           FROM remote_threads ORDER BY object_id"""

let private follow (env: Env) =
    Db.dump env.App.Db "SELECT state, coalesce(follow_id, '-') FROM series_follows"

let private remoteComments (env: Env) =
    Db.dump
        env.App.Db
        """SELECT object_id, remote_actor, cr_episode_id, coalesce(remote_thread, '-'), coalesce(in_reply_to, '-'), content,
                  deleted_at IS NOT NULL
           FROM comments WHERE remote_actor IS NOT NULL ORDER BY object_id"""

let private outcomeOf (env: Env) (eventId: string) =
    Db.scalar<string> env.App.Db "SELECT outcome FROM megafono_events WHERE id = @id" [ "id", box eventId ]

let private seriesUser (env: Env) = $"series-{env.Ep.Series}"

// ---------------------------------------------------------------------------
// follow.*
// ---------------------------------------------------------------------------

[<Fact>]
let ``follow events of ours set the state and the follow id, and the actor's handle is kept`` () =
    task {
        let! env = followedEnv "pending"
        use env = env

        for eventType, state in [ "follow.accepted", "accepted"; "follow.rejected", "rejected"; "follow.removed", "removed" ] do
            do! deliver env (followJson $"f-{state}" eventType (seriesUser env) "outbound" (seriesActor env))
            let! now = follow env
            Assert.Equal<string list>([ $"{state}|follow-77" ], now)
            let! outcome = outcomeOf env $"f-{state}"
            Assert.Equal("applied", outcome)

        let! actors = Db.dump env.App.Db "SELECT id, handle FROM remote_actors"
        Assert.Equal<string list>([ $"{seriesActor env}|@frieren@other.test" ], actors)
    }

[<Fact>]
let ``a follow event that is not ours is ignored: another direction, another user, an actor we don't follow`` () =
    task {
        let! env = followedEnv "pending"
        use env = env

        do! deliver env (followJson "f-1" "follow.accepted" (seriesUser env) "inbound" (seriesActor env))
        do! deliver env (followJson "f-2" "follow.accepted" "series-OTHERSER1" "outbound" (seriesActor env))
        do! deliver env (followJson "f-3" "follow.accepted" "some-user-id" "outbound" (seriesActor env))
        do! deliver env (followJson "f-4" "follow.accepted" (seriesUser env) "outbound" $"https://{domain}/ap/actors/series-OTHERSER1")
        do! deliver env (followJson "f-5" "follow.accepted" (seriesUser env) "outbound" "https://third.test/users/x")

        let! now = follow env
        Assert.Equal<string list>([ "pending|-" ], now)

        for id in [ "f-1"; "f-2"; "f-3"; "f-4"; "f-5" ] do
            let! outcome = outcomeOf env id
            Assert.Equal("ignored", outcome)

        let! actors = Db.count env.App.Db "remote_actors"
        Assert.Equal(0L, actors)
    }

// ---------------------------------------------------------------------------
// Threads
// ---------------------------------------------------------------------------

[<Fact>]
let ``a thread with our extensions is stored against the episode it names`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let! facts = Db.dump env.App.Db $"SELECT season_id, episode_number FROM cr_episodes WHERE id = '{env.Ep.Episode}'"
        let parts = facts.Head.Split '|'
        let groupKey = $"{parts[0]}#{parts[1]}"

        // an episode id we have
        let t1 = { thread env 1 with Extensions = ours env.Ep.Series env.Ep.Episode groupKey; Updated = null }
        do! deliver env (received t1)

        // an episode id we don't have, mapped by its group key
        let t2 = { thread env 2 with Extensions = ours env.Ep.Series "REMOTEEP2" groupKey }
        do! deliver env (received t2)

        // no local match at all: stored, with no episode yet
        let t3 = { thread env 3 with Extensions = ours env.Ep.Series "REMOTEEP3" "NOSEASON1#4" }
        do! deliver env (received t3)

        let! stored = threads env

        Assert.Equal<string list>(
            [ $"{t1.ObjectId}|{domain}|{env.Ep.Series}|{env.Ep.Episode}|{groupKey}|{env.Ep.Episode}|{t1.Url}|False"
              $"{t2.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP2|{groupKey}|{env.Ep.Episode}|{t2.Url}|False"
              $"{t3.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP3|NOSEASON1#4|-|{t3.Url}|False" ],
            stored
        )

        let! outcome = outcomeOf env t1.EventId
        Assert.Equal("applied", outcome)
    }

[<Fact>]
let ``a thread without extensions is read from its url when that is the actor's host, series and an episode id`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let t = { thread env 1 with Extensions = null }
        do! deliver env (received t)

        let! stored = threads env
        Assert.Equal<string list>([ $"{t.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP1|-|-|{t.Url}|False" ], stored)

        // the url of the episode we do have maps to it
        let known = { thread env 2 with Extensions = null; Url = $"https://{domain}/anime/{env.Ep.Series}/episode/{env.Ep.Episode}" }
        do! deliver env (received known)
        let! now = threads env
        Assert.Contains($"{known.ObjectId}|{domain}|{env.Ep.Series}|{env.Ep.Episode}|-|{env.Ep.Episode}|{known.Url}|False", now)
    }

[<Fact>]
let ``what isn't a thread of a followed series account is acknowledged and ignored`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let t = thread env

        let ignored =
            [ "an actor we don't follow", { t 1 with ActorId = "https://third.test/ap/actors/series-OTHERSER1" }
              "another series id in the extensions", { t 2 with Extensions = ours "OTHERSER1" "REMOTEEP2" null }
              "a bad episode id", { t 3 with Extensions = ours env.Ep.Series "bad id" null }
              "a bad group key", { t 4 with Extensions = ours env.Ep.Series "REMOTEEP4" "x#y" }
              "another namespace and a url on another host",
              { t 5 with
                  Extensions = box {| ``namespace`` = "https://elsewhere.test/ns#"; prefix = "x"; data = {| episode = {| seriesId = env.Ep.Series; episodeId = "REMOTEEP5" |} |} |}
                  Url = $"https://third.test/anime/{env.Ep.Series}/episode/REMOTEEP5" }
              "a url of another series",
              { t 6 with Extensions = null; Url = $"https://{domain}/anime/OTHERSER1/episode/REMOTEEP6" }
              "a followers-only post", { t 7 with Audience = "followers" }
              "a direct post", { t 8 with Audience = "direct" }
              "a reply to something we don't hold", { t 9 with InReplyTo = "https://third.test/objects/1" } ]

        for what, post in ignored do
            do! deliver env (received post)
            let! outcome = outcomeOf env post.EventId
            Assert.True((outcome = "ignored"), $"{what}: {outcome}")

        let! stored = threads env
        Assert.Empty stored
    }

[<Fact>]
let ``an unlisted thread is kept, a non-https url is not stored but the thread is`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let t = { thread env 1 with Audience = "unlisted"; Extensions = ours env.Ep.Series "REMOTEEP1" null; Url = "http://other.test/anime/x" }
        do! deliver env (received t)
        let! stored = threads env
        Assert.Equal<string list>([ $"{t.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP1|-|-|-|False" ], stored)
    }

[<Fact>]
let ``post.updated edits a thread, a copy that is not newer changes nothing, a deleted thread stays deleted`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let t = thread env 1
        do! deliver env (received t)

        // newer: the url and the ids follow the edit
        let edited = { t with EventId = "p-u1"; Updated = "2026-10-12T18:00:00Z"; Extensions = ours env.Ep.Series "REMOTEEP1B" null }
        do! deliver env (updated edited)
        let! afterEdit = threads env
        Assert.Contains($"{t.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP1B|-|-|{t.Url}|False", afterEdit)
        let! outcome = outcomeOf env "p-u1"
        Assert.Equal("applied", outcome)

        // older and the same: stale
        do! deliver env (updated { t with EventId = "p-u2"; Updated = "2026-10-12T17:30:00Z"; Extensions = ours env.Ep.Series "REMOTEEP1C" null })
        do! deliver env (updated { edited with EventId = "p-u3" })
        let! afterStale = threads env
        Assert.Equal<string list>(afterEdit, afterStale)
        let! stale = outcomeOf env "p-u2"
        Assert.Equal("stale", stale)

        // deleted, then received again: it stays deleted
        do! deliver env (deletedJson "p-d1" t.ObjectId)
        do! deliver env (updated { edited with EventId = "p-u4"; Updated = "2026-10-13T00:00:00Z" })
        let! afterDelete = threads env
        Assert.Contains($"{t.ObjectId}|{domain}|{env.Ep.Series}|REMOTEEP1B|-|-|{t.Url}|True", afterDelete)
        let! deleted = outcomeOf env "p-d1"
        Assert.Equal("applied", deleted)
    }

// ---------------------------------------------------------------------------
// Comments under a remote thread
// ---------------------------------------------------------------------------

/// A thread of ours for the env's episode, stored; returns it
let private storedThread (env: Env) : Task<Post> =
    task {
        let t = { thread env 1 with Extensions = ours env.Ep.Series env.Ep.Episode null }
        do! deliver env (received t)
        return t
    }

[<Fact>]
let ``a comment on the thread, and a reply to that comment, are stored on the thread's episode with the thread`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let! t = storedThread env

        let c1 = comment 1 t.ObjectId
        let c2 = { comment 2 c1.ObjectId with Content = "<p>replying to bob</p>" }
        do! deliver env (received c1)
        do! deliver env (received c2)

        let! stored = remoteComments env
        let expected (c: Post) text = $"{c.ObjectId}|{c.ActorId}|{env.Ep.Episode}|{t.ObjectId}|{c.InReplyTo}|{text}|False"
        Assert.Equal<string list>([ expected c1 "<p>that ending!</p>"; expected c2 "<p>replying to bob</p>" ], stored)

        let! outcome = outcomeOf env c2.EventId
        Assert.Equal("applied", outcome)

        // the author is known by name and handle
        let! actors = Db.dump env.App.Db "SELECT id, handle FROM remote_actors WHERE id LIKE 'https://mastodon.example/%'"
        Assert.Equal<string list>([ "https://mastodon.example/users/bob|@bob@mastodon.example" ], actors)
    }

[<Fact>]
let ``post.updated edits a remote comment when newer, and a stale copy changes nothing`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let! t = storedThread env
        let c = comment 1 t.ObjectId
        do! deliver env (received c)

        do! deliver env (updated { c with EventId = "p-cu1"; Content = "<p>edited</p>"; Updated = "2026-10-12T18:30:00Z" })
        do! deliver env (updated { c with EventId = "p-cu2"; Content = "<p>older edit</p>"; Updated = "2026-10-12T18:10:00Z" })

        let! stored = remoteComments env
        Assert.Contains("|<p>edited</p>|False", stored.Head)
        let! stale = outcomeOf env "p-cu2"
        Assert.Equal("stale", stale)
    }

[<Fact>]
let ``post.deleted tombstones a remote comment and marks a remote thread deleted, and comments under a deleted thread are ignored`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let! t = storedThread env
        let c = comment 1 t.ObjectId
        do! deliver env (received c)

        do! deliver env (deletedJson "p-d1" c.ObjectId)
        let! afterComment = remoteComments env
        Assert.Equal<string list>([ $"{c.ObjectId}|{c.ActorId}|{env.Ep.Episode}|{t.ObjectId}|{t.ObjectId}||True" ], afterComment)

        do! deliver env (deletedJson "p-d2" t.ObjectId)
        let! afterThread = threads env
        Assert.EndsWith("|True", afterThread.Head)

        // a deleted thread takes no more comments
        do! deliver env (received (comment 2 t.ObjectId))
        let! outcome = outcomeOf env "p-c2"
        Assert.Equal("ignored", outcome)
        let! stored = remoteComments env
        Assert.Equal(1, stored.Length)

        // what we hold nothing of is ignored
        do! deliver env (deletedJson "p-d3" "https://nowhere.test/objects/9")
        let! unknown = outcomeOf env "p-d3"
        Assert.Equal("ignored", unknown)
    }

[<Fact>]
let ``post.deleted never touches a feature 015 reply or a local comment`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let r = reply env.ThreadId 1
        do! deliver env (receivedJson r)
        do! deliver env (deletedJson "p-d1" r.ObjectId)
        do! deliver env (deletedJson "p-d2" env.AliceObjectId)

        let! row = remoteRow env r.ObjectId
        Assert.Contains("|False|", String.Join("|", row) + "|")
        let! alice = Db.dump env.App.Db $"SELECT deleted_at IS NOT NULL FROM comments WHERE id = '{env.AliceCommentId}'"
        Assert.Equal<string list>([ "False" ], alice)
    }

[<Fact>]
let ``a comment on a thread with no local episode is unknown_thread and stores nothing, until the episode is known`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let t = { thread env 3 with Extensions = ours env.Ep.Series "REMOTEEP3" "NOSEASON1#4" }
        do! deliver env (received t)

        let c = comment 1 t.ObjectId
        do! deliver env (received c)
        let! outcome = outcomeOf env c.EventId
        Assert.Equal("unknown_thread", outcome)
        let! none = remoteComments env
        Assert.Empty none

        // our catalog learns the episode: the next comment finds it, and the thread remembers it
        do!
            Db.execute
                env.App.Db
                $"""INSERT INTO cr_episodes (id, series_id, season_id, episode_number, duration_ms, source_at)
                    VALUES ('REMOTEEP3', '{env.Ep.Series}', NULL, NULL, 1400000, now())"""

        let c2 = comment 2 t.ObjectId
        do! deliver env (received c2)
        let! stored = remoteComments env
        Assert.Equal<string list>([ $"{c2.ObjectId}|{c2.ActorId}|REMOTEEP3|{t.ObjectId}|{t.ObjectId}|<p>that ending!</p>|False" ], stored)
        let! stored = threads env
        Assert.Contains("|REMOTEEP3|", stored.Head)
    }

[<Fact>]
let ``a reply to something that isn't under a remote thread is ignored`` () =
    task {
        let! env = followedEnv "accepted"
        use env = env
        let c = comment 1 "https://third.test/objects/unrelated"
        do! deliver env (received c)
        let! outcome = outcomeOf env c.EventId
        Assert.Equal("ignored", outcome)
        let! none = remoteComments env
        Assert.Empty none
    }
