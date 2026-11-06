/// Feature 030 (T007): weekly recap posts on the fediverse. A public user who turned them on before last week and
/// watched last week gets one unlisted Note with the week's figures at their first sync after it, sent through the
/// outbox to the fake Megáfono. The import worker queues it (`processAllFor`), the outbox worker sends it
/// (`drainOutbox`); the hosted workers stay off. Dates are relative to the database's `now()`: the week is the last
/// completed UTC week, so entries are placed from that week's Monday.
module FrunchyBackend.Tests.RecapPostsTests

open System
open System.Globalization
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.FakeMegafono
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Workers.FediverseOutbox

type private World =
    { App: TestApp
      Browser: Browser.Browser
      Key: string
      UserId: Guid
      /// Monday 00:00 UTC of the last completed week, by the database's clock
      LastMonday: DateTime }

let private appWith (extraConfig: (string * string) list) : Task<TestApp> =
    task {
        let! db = newDatabase ()
        return! createFederatedAppWith db extraConfig ignore
    }

/// A signed-up user with a sync key; `on` turns recap posts on, as of 14 days ago (before last week began)
let private join (app: TestApp) (name: string) (on: bool) : Task<World> =
    task {
        let! browser = signedUp app $"sub-{name}" name
        let! key = connectExtension browser
        let! userId = Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box name ]

        let! mondayText =
            Db.scalar<string> app.Db "SELECT to_char(date_trunc('week', now() AT TIME ZONE 'UTC') - interval '7 days', 'YYYY-MM-DD')" []

        let monday =
            DateTime.SpecifyKind(DateTime.ParseExact(mondayText, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc)

        if on then
            let! response = browser.PostForm("/account", "/account/recap-posts", [ "enabled", "on" ])
            Assert.Equal(302, int response.StatusCode)
            do! Db.execute app.Db $"UPDATE recap_post_settings SET enabled_at = now() - interval '14 days' WHERE user_id = '{userId}'"

        let! _ = drainOutbox app
        app.Megafono.Clear()

        return
            { App = app
              Browser = browser
              Key = key
              UserId = userId
              LastMonday = monday }
    }

let private mondayText (w: World) = w.LastMonday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

/// A moment of last week: day 0 is Monday, 6 Sunday
let private lastWeek (w: World) (day: int) (hour: int) = w.LastMonday.AddDays(float day).AddHours(float hour)

/// A moment of this week (the minute after its Monday began, so never in the future of a Monday morning clock)
let private thisWeek (w: World) = w.LastMonday.AddDays(7.0).AddMinutes 1.0

let private entryAt (episode: string) (series: string) (number: int) (at: DateTime) (fully: bool) =
    datedEntry episode series $"{series}S1" number None at fully

let private importAt (w: World) (entries: string list) =
    task {
        let! _ = submitAccepted w.App w.Key (envelopeOf [ historyUrl RealAccountId, pageOf entries ])
        let! _ = processAllFor w.App
        ()
    }

/// A typical last week: SERIESA three episodes on Tuesday (two) and Thursday, SERIESB one on Thursday, and a partly
/// watched SERIESC episode that counts for nothing but minutes
let private typicalWeek (w: World) =
    [ entryAt "EPA00001" "SERIESA" 1 (lastWeek w 1 10) true
      entryAt "EPA00002" "SERIESA" 2 (lastWeek w 1 11) true
      entryAt "EPA00003" "SERIESA" 3 (lastWeek w 3 9) true
      entryAt "EPB00001" "SERIESB" 1 (lastWeek w 3 20) true
      entryAt "EPC00001" "SERIESC" 1 (lastWeek w 4 8) false ]

let private publishes (app: TestApp) = app.Megafono.RequestsTo "POST /internal/actors/*/objects"

let private deletes (app: TestApp) = app.Megafono.RequestsTo "DELETE /internal/objects/*"

let private recaps (app: TestApp) =
    Db.dump app.Db "SELECT state, coalesce(object_id, '-') FROM recap_posts ORDER BY week_start"

let private states (app: TestApp) =
    Db.dump app.Db "SELECT state FROM recap_posts ORDER BY week_start"

let private outboxKinds (app: TestApp) (kind: string) =
    Db.scalar<int64> app.Db "SELECT count(*) FROM fediverse_outbox WHERE kind = @k" [ "k", box kind ]

let private events (app: TestApp) =
    Db.dump app.Db "SELECT event FROM security_events WHERE event LIKE 'recap_posts_%' ORDER BY id"

let private bodyOf (r: Recorded) = r.Json.GetProperty "object"

let private text (json: JsonElement) (name: string) = json.GetProperty(name).GetString()

let private isNull' (json: JsonElement) (name: string) = json.GetProperty(name).ValueKind = JsonValueKind.Null

/// The post's HTML as the worker builds it from the week's figures, read with `Recap` over the same window
let private expectedContent (w: World) : Task<string> =
    task {
        use connection = getConnection w.App.Db
        do! connection.OpenAsync()
        let from = DateTimeOffset(w.LastMonday)
        let until = from.AddDays 7.0
        let! week = Recap.loadBetween connection w.UserId from (Some until)
        let! days = Recap.loadDaysWatched connection w.UserId from until
        let! most = Recap.loadMostWatched connection w.UserId from until 3

        return
            recapPostHtml
                "frunchy.test"
                (DateOnly.FromDateTime w.LastMonday)
                { Week = week
                  DaysWatched = days
                  MostWatched = most }
    }

// ---------------------------------------------------------------------------
// The post
// ---------------------------------------------------------------------------

[<Fact>]
let ``a public user who opted in before last week and watched last week gets one unlisted Note with its figures`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        // queued in the import's transaction, nothing sent yet
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(1L, kindCount)
        Assert.Equal(0, (publishes app).Length)

        let! processed = drainOutbox app
        Assert.Equal(1, processed)

        let calls = publishes app
        Assert.Equal(1, calls.Length)
        let call = calls[0]
        let monday = mondayText w

        Assert.Equal($"/internal/actors/{w.UserId}/objects", call.Path)
        Assert.Equal(Some $"recap:{w.UserId}:{monday}", call.IdempotencyKey)
        Assert.Equal("unlisted", text call.Json "audience")
        let note = bodyOf call
        Assert.Equal("Note", text note "type")
        Assert.True(isNull' note "inReplyTo")
        Assert.True(isNull' note "summary")
        Assert.False(note.GetProperty("sensitive").GetBoolean())
        Assert.Equal($"https://frunchy.test/u/alice#recap-{monday}", text note "url")

        // the figures are the recap's, read over the same completed week
        let content = text note "content"
        let! expected = expectedContent w
        Assert.Equal(expected, content)
        Assert.Contains("4 episodes", content)
        Assert.Contains("2 series (2 new, 0 continued). Watched on 2 of 7 days.", content)

        Assert.Contains(
            "Most watched: <a href=\"https://frunchy.test/anime/SERIESA\">Series SERIESA</a> (3) and <a href=\"https://frunchy.test/anime/SERIESB\">Series SERIESB</a> (1).",
            content
        )

        let! rows = recaps app
        Assert.Equal(1, rows.Length)
        Assert.StartsWith("published|obj-", rows[0])

        // a second import (this week's watching, and a later sync of the same history) posts nothing more
        do! importAt w ((typicalWeek w) @ [ entryAt "EPNOW001" "SERIESD" 1 (thisWeek w) true ])
        let! again = drainOutbox app
        Assert.Equal(0, again)
        Assert.Equal(1, (publishes app).Length)
        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(1L, count)
        let! kindCount2 = outboxKinds app "post_recap"
        Assert.Equal(1L, kindCount2)
    }

[<Fact>]
let ``a series watched before the week is continued, not new`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        do!
            importAt
                w
                [ entryAt "EPOLD001" "SERIESA" 1 (w.LastMonday.AddDays -3.0) true
                  entryAt "EPA00002" "SERIESA" 2 (lastWeek w 2 10) true
                  entryAt "EPB00001" "SERIESB" 1 (lastWeek w 2 11) true ]

        let! _ = drainOutbox app
        let content = text (bodyOf (publishes app).Head) "content"
        Assert.Contains("2 series (1 new, 1 continued)", content)
    }

[<Fact>]
let ``opting in during last week posts nothing about it`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! Db.execute app.Db $"UPDATE recap_post_settings SET enabled_at = '{mondayText w}'::date + interval '1 day' WHERE user_id = '{w.UserId}'"

        do! importAt w (typicalWeek w)
        let! _ = drainOutbox app

        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(0L, kindCount)
        Assert.Equal(0, (publishes app).Length)
    }

[<Fact>]
let ``watching only this week posts nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w [ entryAt "EPNOW001" "SERIESA" 1 (thisWeek w) true ]
        let! _ = drainOutbox app

        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(0L, count)
        Assert.Equal(0, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``only partial watches last week post nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w [ entryAt "EPPART01" "SERIESA" 1 (lastWeek w 2 10) false; entryAt "EPPART02" "SERIESA" 2 (lastWeek w 3 10) false ]
        let! _ = drainOutbox app

        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(0L, count)
        Assert.Equal(0, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``without turning recap posts on nothing is queued`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" false
        do! importAt w (typicalWeek w)
        let! _ = drainOutbox app

        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(0L, kindCount)
        Assert.Equal(0, (publishes app).Length)
    }

[<Theory>]
[<InlineData("")>]
[<InlineData("private")>]
let ``a profile that is for members or private posts nothing`` (defaultVisibility: string) =
    task {
        use! app = appWith [ "Frunchy:Profiles:DefaultVisibility", defaultVisibility ]
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)
        let! _ = drainOutbox app

        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(0L, kindCount)
        Assert.Equal(0, (publishes app).Length)
    }

[<Fact>]
let ``a post queued and then the profile made members before the worker runs publishes nothing and the row is skipped`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(1L, kindCount)

        // the profile changes outside the account page (the worker's own re-check has to catch it)
        do! Db.execute app.Db $"INSERT INTO profile_settings (user_id, visibility) VALUES ('{w.UserId}', 'members')"
        let! _ = drainOutbox app

        Assert.Equal(0, (publishes app).Length)
        Assert.Equal(0, (deletes app).Length)
        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
    }

[<Fact>]
let ``making the profile members through the account page before the worker runs skips the post and deletes nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        let! response = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "members" ])
        Assert.Equal(302, int response.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(0, (publishes app).Length)
        Assert.Equal(0, (deletes app).Length)
        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
    }

[<Fact>]
let ``a post queued and then the opt-in moved into the week is skipped by the worker's own check`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        do! Db.execute app.Db $"UPDATE recap_post_settings SET enabled_at = now() WHERE user_id = '{w.UserId}'"
        let! _ = drainOutbox app

        Assert.Equal(0, (publishes app).Length)
        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
    }

[<Fact>]
let ``a week with nothing left at build time is skipped, not posted`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        // the history changed between queueing and sending
        do! Db.execute app.Db "DELETE FROM watch_events"
        let! _ = drainOutbox app

        Assert.Equal(0, (publishes app).Length)
        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
    }

[<Fact>]
let ``a permanent refusal skips that row and is never retried`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        app.Megafono.Route("POST /internal/actors/*/objects", [ Status 400 ])
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)

        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
        let! outbox = Db.dump app.Db "SELECT state FROM fediverse_outbox WHERE kind = 'post_recap'"
        Assert.Equal<string list>([ "failed" ], outbox)

        let! _ = drainOutboxAt app (DateTimeOffset.UtcNow.AddHours 3.0)
        Assert.Equal(1, (publishes app).Length)
    }

// ---------------------------------------------------------------------------
// The request is frozen at the first attempt
// ---------------------------------------------------------------------------

[<Fact>]
let ``a retry after a failed first attempt sends exactly the stored content and url`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)

        // the first publish fails transiently
        app.Megafono.FailNext 1
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)

        let! state = Db.dump app.Db "SELECT state, attempts FROM fediverse_outbox WHERE kind = 'post_recap'"
        Assert.Equal<string list>([ "pending|1" ], state)
        let! rowState = states app
        Assert.Equal<string list>([ "pending" ], rowState)

        // the history changes inside the week before the retry: the stored body still goes out
        do! importAt w ((typicalWeek w) @ [ entryAt "EPLATE01" "SERIESE" 1 (lastWeek w 5 10) true ])

        let! _ = drainOutboxAt app (DateTimeOffset.UtcNow.AddMinutes 5.0)
        let calls = publishes app
        Assert.Equal(2, calls.Length)
        Assert.Equal(calls[0].Body, calls[1].Body)
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey)
        Assert.DoesNotContain("SERIESE", text (bodyOf calls[1]) "content")

        let! stored = Db.scalar<string> app.Db "SELECT content FROM recap_posts" []
        Assert.Equal(text (bodyOf calls[1]) "content", stored)
        let! rows = states app
        Assert.Equal<string list>([ "published" ], rows)
    }

// ---------------------------------------------------------------------------
// Turning off, leaving Public, independence from watch updates, rebuilding
// ---------------------------------------------------------------------------

/// A user with one published recap
let private withPublishedRecap (app: TestApp) : Task<World * string> =
    task {
        let! w = join app "alice" true
        do! importAt w (typicalWeek w)
        let! _ = drainOutbox app
        let! objectId = Db.scalar<string> app.Db "SELECT object_id FROM recap_posts" []
        return w, objectId
    }

[<Fact>]
let ``turning recaps off after a post deletes it on Megafono, marks the row deleted and records the event once`` () =
    task {
        use! app = appWith []
        let! w, objectId = withPublishedRecap app

        let! off = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "off" ])
        Assert.Equal(302, int off.StatusCode)
        Assert.Equal("/account?msg=recap-posts-off#recap-posts", string off.Headers.Location)
        let! _ = drainOutbox app

        let calls = deletes app
        Assert.Equal(1, calls.Length)
        Assert.Equal($"/internal/objects/{objectId}", calls[0].Path)
        let! rows = recaps app
        Assert.Equal<string list>([ $"deleted|{objectId}" ], rows)
        let! setting = Db.count app.Db "recap_post_settings"
        Assert.Equal(0L, setting)

        // a repeat changes nothing: no second event, no second removal
        let! again = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "off" ])
        Assert.Equal(302, int again.StatusCode)
        let! _ = drainOutbox app
        Assert.Equal(1, (deletes app).Length)
        let! kindCount = outboxKinds app "delete_recaps"
        Assert.Equal(1L, kindCount)

        let! recorded = events app
        Assert.Equal<string list>([ "recap_posts_enabled"; "recap_posts_disabled" ], recorded)
    }

[<Fact>]
let ``a profile that leaves Public deletes its posted recaps, and becoming public again deletes nothing more`` () =
    task {
        use! app = appWith []
        let! w, objectId = withPublishedRecap app

        let! members = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "members" ])
        Assert.Equal(302, int members.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(1, (deletes app).Length)
        let! rows = recaps app
        Assert.Equal<string list>([ $"deleted|{objectId}" ], rows)

        let! priv = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "private" ])
        Assert.Equal(302, int priv.StatusCode)
        let! pub = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "public" ])
        Assert.Equal(302, int pub.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(1, (deletes app).Length)
        let! kindCount = outboxKinds app "delete_recaps"
        Assert.Equal(1L, kindCount)
    }

[<Fact>]
let ``turning watch updates off deletes no recap and turning recaps off deletes no watch update`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        let! on = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
        Assert.Equal(302, int on.StatusCode)
        do! Db.execute app.Db $"UPDATE watch_update_settings SET enabled_at = now() - interval '14 days' WHERE user_id = '{w.UserId}'"
        app.Megafono.Clear()

        // one sync: last week's recap, and an update about something watched a moment ago
        do! importAt w ((typicalWeek w) @ [ datedEntry "EPRECENT" "SERIESR" "SERIESRS1" 1 None (DateTime.UtcNow.AddMinutes -5.0) true ])
        let! _ = drainOutbox app

        let objectsBy (prefix: string) =
            (publishes app) |> List.filter (fun r -> (r.IdempotencyKey |> Option.defaultValue "").StartsWith prefix)

        Assert.Equal(1, (objectsBy "recap:").Length)
        Assert.Equal(1, (objectsBy "watch:").Length)
        let! recapObject = Db.scalar<string> app.Db "SELECT object_id FROM recap_posts" []
        let! updateObject = Db.scalar<string> app.Db "SELECT object_id FROM watch_updates" []

        // watch updates off: only the update is deleted
        let! offWatch = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "off" ])
        Assert.Equal(302, int offWatch.StatusCode)
        let! _ = drainOutbox app
        Assert.Equal<string list>([ $"/internal/objects/{updateObject}" ], (deletes app) |> List.map (fun r -> r.Path))
        let! recapStates = states app
        Assert.Equal<string list>([ "published" ], recapStates)

        // recaps off: only the recap is deleted
        let! offRecap = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "off" ])
        Assert.Equal(302, int offRecap.StatusCode)
        let! _ = drainOutbox app
        Assert.Equal<string list>([ $"/internal/objects/{updateObject}"; $"/internal/objects/{recapObject}" ], (deletes app) |> List.map (fun r -> r.Path))
        let! recapAfter = states app
        Assert.Equal<string list>([ "deleted" ], recapAfter)
        let! updatesAfter = Db.dump app.Db "SELECT state FROM watch_updates"
        Assert.Equal<string list>([ "deleted" ], updatesAfter)
    }

[<Fact>]
let ``user-rebuild posts nothing that was already posted`` () =
    task {
        use! app = appWith []
        let! _w, _ = withPublishedRecap app

        let! queued = RebuildUser.rebuildUser app.Db "alice"
        Assert.Equal(Some 1, queued)
        let! _ = processAllFor app
        let! _ = drainOutbox app

        Assert.Equal(1, (publishes app).Length)
        let! count = Db.count app.Db "recap_posts"
        Assert.Equal(1L, count)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(1L, kindCount)
    }

[<Fact>]
let ``a recap that fails never fails the import: the history is stored and nothing is queued`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        // queueAfterImport's statement now fails (its table is gone)
        do! Db.execute app.Db "ALTER TABLE recap_posts RENAME TO recap_posts_gone"

        do! importAt w (typicalWeek w)

        let! status = Db.scalar<string> app.Db "SELECT status FROM cr_imports" []
        Assert.Equal("completed", status)
        let! watched = Db.count app.Db "watch_events"
        Assert.Equal(5L, watched)
        let! kindCount = outboxKinds app "post_recap"
        Assert.Equal(0L, kindCount)
    }

// ---------------------------------------------------------------------------
// Federation off
// ---------------------------------------------------------------------------

[<Fact>]
let ``with federation off the route is the plain 404, the account page has no section and nothing is queued`` () =
    task {
        use! app = newApp ()
        let! browser = signedUp app "sub-alice" "alice"
        let! key = connectExtension browser
        let! userId = Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = 'alice'" []

        let! response = browser.PostForm("/account", "/account/recap-posts", [ "enabled", "on" ])
        Assert.Equal(404, int response.StatusCode)

        let! page = browser.Get "/account"
        Assert.DoesNotContain("data-account=\"recap-posts\"", page.Html)
        Assert.DoesNotContain("data-recap-posts-state", page.Html)

        // even an opted-in row (put there by hand) queues nothing without federation
        do! Db.execute app.Db $"INSERT INTO recap_post_settings (user_id, enabled_at) VALUES ('{userId}', now() - interval '30 days')"
        let lastWeekPlayed = DateTime.UtcNow.AddDays -8.0
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf [ entryAt "EPOFF001" "SERIESA" 1 lastWeekPlayed true ] ])
        let! _ = processAllFor app
        let! recapCount = Db.count app.Db "recap_posts"
        let! outboxCount = Db.count app.Db "fediverse_outbox"
        Assert.Equal(0L, recapCount)
        Assert.Equal(0L, outboxCount)
    }

// ---------------------------------------------------------------------------
// The account page
// ---------------------------------------------------------------------------

[<Fact>]
let ``the account page shows the switch, its state, the notices and an invalid value changes nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" false

        let! off = w.Browser.Get "/account"
        Assert.Contains("data-account=\"recap-posts\"", off.Html)
        Assert.Contains("id=\"recap-posts\"", off.Html)
        Assert.Contains("data-recap-posts-state=\"off\"", off.Html)
        Assert.DoesNotContain("data-recap-posts-not-public", off.Html)
        // right after the watch updates section
        Assert.True(off.Html.IndexOf "data-account=\"watch-updates\"" < off.Html.IndexOf "data-account=\"recap-posts\"")

        let! on = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "on" ])
        Assert.Equal(302, int on.StatusCode)
        Assert.Equal("/account?msg=recap-posts-on#recap-posts", string on.Headers.Location)
        let! onPage = w.Browser.Follow on
        Assert.Contains("data-account-notice=\"recap-posts-on\"", onPage.Html)
        Assert.Contains("data-recap-posts-state=\"on\"", onPage.Html)

        // turning on twice keeps the first moment and records one event
        let! firstAt = Db.scalar<DateTime> app.Db "SELECT enabled_at FROM recap_post_settings" []
        let! again = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "on" ])
        Assert.Equal(302, int again.StatusCode)
        let! secondAt = Db.scalar<DateTime> app.Db "SELECT enabled_at FROM recap_post_settings" []
        Assert.Equal(firstAt, secondAt)
        let! recorded = events app
        Assert.Equal<string list>([ "recap_posts_enabled" ], recorded)

        // anything but on or off: the notice says so and nothing changes
        let! invalid = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "maybe" ])
        Assert.Equal("/account?msg=recap-posts-invalid#recap-posts", string invalid.Headers.Location)
        let! invalidPage = w.Browser.Follow invalid
        Assert.Contains("data-account-notice=\"recap-posts-invalid\"", invalidPage.Html)
        Assert.Contains("data-recap-posts-state=\"on\"", invalidPage.Html)

        let! missing = w.Browser.PostForm("/account", "/account/recap-posts", [])
        Assert.Equal("/account?msg=recap-posts-invalid#recap-posts", string missing.Headers.Location)

        let! offResponse = w.Browser.PostForm("/account", "/account/recap-posts", [ "enabled", "off" ])
        let! offPage = w.Browser.Follow offResponse
        Assert.Contains("data-account-notice=\"recap-posts-off\"", offPage.Html)
        Assert.Contains("data-recap-posts-state=\"off\"", offPage.Html)
    }

[<Fact>]
let ``a profile that isn't public says nothing is posted until it is`` () =
    task {
        use! app = appWith [ "Frunchy:Profiles:DefaultVisibility", "" ]
        let! w = join app "alice" false

        let! page = w.Browser.Get "/account"
        Assert.Contains("data-recap-posts-not-public", page.Html)

        let! _ = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "public" ])
        let! after = w.Browser.Get "/account"
        Assert.DoesNotContain("data-recap-posts-not-public", after.Html)
    }

[<Fact>]
let ``the signed-out route is refused and the section, notices and event labels speak Spanish`` () =
    task {
        use! app = appWith []

        use anonymous = app.NewBrowser()
        let! refused = anonymous.PostFormWithoutToken("/account/recap-posts", [ "enabled", "on" ])
        Assert.NotEqual(200, int refused.StatusCode)
        let! none = Db.count app.Db "recap_post_settings"
        Assert.Equal(0L, none)

        let! invite = createInvite app
        use spanish = app.NewBrowser "es"
        let! signUp = submitSignUp spanish "sub-ana" invite "ana" "Ana"
        Assert.Equal(302, int signUp.StatusCode)

        let! page = spanish.Get "/account"
        Assert.Contains("Resumen semanal en el fediverso", page.Html)

        let! on = spanish.PostForm("/account", "/account/recap-posts", [ "enabled", "on" ])
        let! onPage = spanish.Follow on
        Assert.Contains("Los res", onPage.Html)
        Assert.Contains("data-recap-posts-state=\"on\"", onPage.Html)
        Assert.Contains("data-security-event=\"recap_posts_enabled\"", onPage.Html)
        Assert.Contains("activados", onPage.Html)
    }
