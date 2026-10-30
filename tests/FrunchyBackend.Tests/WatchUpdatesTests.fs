/// Feature 028 (T007): watch updates on the fediverse. A public user who turned them on gets one unlisted Note a day
/// listing what they finished recently, sent through the outbox to the fake Megáfono. The import worker queues it
/// (`processAllFor`), the outbox worker sends it (`drainOutbox`); the hosted workers stay off. Watched dates are relative
/// to the clock (hours ago) because the window is relative to the database's `now()`.
module FrunchyBackend.Tests.WatchUpdatesTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.FakeMegafono
open FrunchyBackend.Tests.Infrastructure.PostgresFixture
open FrunchyBackend.Tests.TestHelpers

type private World =
    { App: TestApp
      Browser: Browser.Browser
      Key: string
      UserId: Guid }

let private appWith (extraConfig: (string * string) list) : Task<TestApp> =
    task {
        let! db = newDatabase ()
        return! createFederatedAppWith db extraConfig ignore
    }

/// A signed-up user with a sync key; `on` turns watch updates on, as of a day ago (so recent watching counts)
let private join (app: TestApp) (name: string) (on: bool) : Task<World> =
    task {
        let! browser = signedUp app $"sub-{name}" name
        let! key = connectExtension browser
        let! userId = Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box name ]

        if on then
            let! response = browser.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
            Assert.Equal(302, int response.StatusCode)
            do! Db.execute app.Db $"UPDATE watch_update_settings SET enabled_at = now() - interval '1 day' WHERE user_id = '{userId}'"

        // sign-up leaves nothing for the outbox, but start every test from a clean record
        let! _ = drainOutbox app
        app.Megafono.Clear()
        return { App = app; Browser = browser; Key = key; UserId = userId }
    }

let private hoursAgo (hours: float) = DateTime.UtcNow.AddHours(-hours)

/// An episode of "Series {series}", season `{series}S1`, watched `hours` ago
let private entry (episode: string) (series: string) (number: int) (hours: float) =
    datedEntry episode series $"{series}S1" number None (hoursAgo hours) true

let private importAt (w: World) (entries: string list) =
    task {
        let! _ = submitAccepted w.App w.Key (envelopeOf [ historyUrl RealAccountId, pageOf entries ])
        let! _ = processAllFor w.App
        ()
    }

let private publishes (app: TestApp) = app.Megafono.RequestsTo "POST /internal/actors/*/objects"

let private deletes (app: TestApp) = app.Megafono.RequestsTo "DELETE /internal/objects/*"

let private today (app: TestApp) =
    Db.scalar<string> app.Db "SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD')" []

let private updates (app: TestApp) =
    Db.dump app.Db "SELECT state, coalesce(object_id, '-') FROM watch_updates ORDER BY day"

let private states (app: TestApp) =
    Db.dump app.Db "SELECT state FROM watch_updates ORDER BY day"

let private outboxKinds (app: TestApp) (kind: string) =
    Db.scalar<int64> app.Db "SELECT count(*) FROM fediverse_outbox WHERE kind = @k" [ "k", box kind ]

let private events (app: TestApp) =
    Db.dump app.Db "SELECT event FROM security_events WHERE event LIKE 'watch_updates_%' ORDER BY id"

let private bodyOf (r: Recorded) = r.Json.GetProperty "object"

let private text (json: JsonElement) (name: string) = json.GetProperty(name).GetString()

let private isNull' (json: JsonElement) (name: string) = json.GetProperty(name).ValueKind = JsonValueKind.Null

let private episodeLink (series: string) (episode: string) (number: int) =
    $"<a href=\"https://frunchy.test/anime/{series}/episode/{episode}\">{number}</a>"

// ---------------------------------------------------------------------------
// The post
// ---------------------------------------------------------------------------

[<Fact>]
let ``a public user who turned updates on and synced recent watching gets one unlisted Note with links`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        do!
            importAt
                w
                [ entry "EPA00006" "SERIESA" 6 3.0
                  entry "EPA00005" "SERIESA" 5 4.0
                  entry "EPA00007" "SERIESA" 7 2.0
                  entry "EPB00012" "SERIESB" 12 1.0
                  // never posted: partly watched
                  datedEntry "EPC00001" "SERIESC" "SERIESCS1" 1 None (hoursAgo 1.5) false ]

        // queued in the import's transaction, nothing sent yet
        let! kindCount = outboxKinds app "post_watch_update"
        Assert.Equal(1L, kindCount)
        Assert.Equal(0, (publishes app).Length)

        let! processed = drainOutbox app
        Assert.Equal(1, processed)

        let calls = publishes app
        Assert.Equal(1, calls.Length)
        let call = calls[0]
        let! day = today app

        Assert.Equal($"/internal/actors/{w.UserId}/objects", call.Path)
        Assert.Equal(Some $"watch:{w.UserId}:{day}", call.IdempotencyKey)
        Assert.Equal("unlisted", text call.Json "audience")
        let note = bodyOf call
        Assert.Equal("Note", text note "type")
        Assert.True(isNull' note "inReplyTo")
        Assert.True(isNull' note "summary")
        Assert.False(note.GetProperty("sensitive").GetBoolean())
        Assert.Equal($"https://frunchy.test/u/alice#watched-{day}", text note "url")

        // the series with more episodes first, numbers ascending, links to the public pages, nothing else
        let expected =
            "<p>Watched 3 episodes of <a href=\"https://frunchy.test/anime/SERIESA\">Series SERIESA</a> ("
            + episodeLink "SERIESA" "EPA00005" 5
            + ", "
            + episodeLink "SERIESA" "EPA00006" 6
            + ", "
            + episodeLink "SERIESA" "EPA00007" 7
            + ") and 1 episode of <a href=\"https://frunchy.test/anime/SERIESB\">Series SERIESB</a> ("
            + episodeLink "SERIESB" "EPB00012" 12
            + ").</p>"

        Assert.Equal(expected, text note "content")

        let! rows = updates app
        Assert.Equal(1, rows.Length)
        Assert.StartsWith("published|obj-", rows[0])

        let! url = Db.scalar<string> app.Db "SELECT object_url FROM watch_updates" []
        Assert.StartsWith("https://frunchy.test/ap/objects/obj-", url)

        // a repeated run sends nothing
        let! again = drainOutbox app
        Assert.Equal(0, again)
        Assert.Equal(1, (publishes app).Length)
    }

[<Fact>]
let ``a recent sync of 300 episodes gives one post, a second import the same day gives none`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        let page (first: int) =
            [ for n in first .. first + 99 ->
                  entry $"EPS{n:D5}" $"SER{n / 20:D3}" (n % 20 + 1) (1.0 + float n * 0.07) ]

        let! _ = submitAccepted w.App w.Key (envelopeOf [ for first in [ 0; 100; 200 ] -> historyUrl RealAccountId, pageOf (page first) ])
        let! _ = processAllFor w.App
        let! _ = drainOutbox app

        let calls = publishes app
        Assert.Equal(1, calls.Length)
        // 15 series: five named, ten counted
        let content = text (bodyOf calls[0]) "content"
        Assert.True(content.EndsWith " and 10 more series.</p>", content.Substring(content.Length - 100))

        // a later import the same day, with another recent episode: no second row, no second post
        do! importAt w [ entry "EPLATER1" "SERLATER" 1 0.5 ]
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)
        let! count = Db.count app.Db "watch_updates"
        Assert.Equal(1L, count)
        let! kindCount = outboxKinds app "post_watch_update"
        Assert.Equal(1L, kindCount)
    }

[<Fact>]
let ``importing an old history posts nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        // opted in long ago, so only the 48 hour window can hold these back
        do! Db.execute app.Db $"UPDATE watch_update_settings SET enabled_at = now() - interval '10 days' WHERE user_id = '{w.UserId}'"

        do! importAt w [ entry "EPOLD001" "SEROLD" 1 72.0; entry "EPOLD002" "SEROLD" 2 100.0 ]
        let! _ = drainOutbox app

        let! count = Db.count app.Db "watch_updates"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_watch_update"
        Assert.Equal(0L, kindCount)
        Assert.Equal(0, app.Megafono.Requests.Length)
    }

[<Fact>]
let ``episodes watched before turning updates on are not in the post`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! Db.execute app.Db $"UPDATE watch_update_settings SET enabled_at = now() - interval '3 hours' WHERE user_id = '{w.UserId}'"

        do! importAt w [ entry "EPBEFORE" "SERIESA" 1 5.0; entry "EPAFTER1" "SERIESA" 2 1.0 ]
        let! _ = drainOutbox app

        let content = text (bodyOf (publishes app).Head) "content"
        Assert.Contains("1 episode of", content)
        Assert.Contains("EPAFTER1", content)
        Assert.DoesNotContain("EPBEFORE", content)
    }

[<Fact>]
let ``without turning updates on nothing is queued`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" false

        do! importAt w [ entry "EPNOTON1" "SERIESA" 1 1.0 ]
        let! _ = drainOutbox app

        let! count = Db.count app.Db "watch_updates"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_watch_update"
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

        do! importAt w [ entry "EPMEMBER" "SERIESA" 1 1.0 ]
        let! _ = drainOutbox app

        let! count = Db.count app.Db "watch_updates"
        Assert.Equal(0L, count)
        let! kindCount = outboxKinds app "post_watch_update"
        Assert.Equal(0L, kindCount)
        Assert.Equal(0, (publishes app).Length)
    }

[<Fact>]
let ``a user's own choice outranks the default: public posts even when the instance default is private`` () =
    task {
        use! app = appWith [ "Frunchy:Profiles:DefaultVisibility", "private" ]
        let! w = join app "alice" true
        let! response = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "public" ])
        Assert.Equal(302, int response.StatusCode)

        do! importAt w [ entry "EPCHOICE" "SERIESA" 1 1.0 ]
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)
    }

[<Fact>]
let ``a post queued and then the profile made members before the worker runs publishes nothing and the row is skipped`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w [ entry "EPRACE001" "SERIESA" 1 1.0 ]
        let! kindCount = outboxKinds app "post_watch_update"
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
let ``making the profile members through the account page before the worker runs skips the update and deletes nothing`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w [ entry "EPRACE002" "SERIESA" 1 1.0 ]

        let! response = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "members" ])
        Assert.Equal(302, int response.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(0, (publishes app).Length)
        Assert.Equal(0, (deletes app).Length)
        let! rows = states app
        Assert.Equal<string list>([ "skipped" ], rows)
    }

[<Fact>]
let ``a dub and its original count once`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        // the same episode number in the original season (S1) and in its dub (S2), watched twice
        do!
            importAt
                w
                [ datedEntry "EPORIG01" "SERDUB1" "SERDUB1S1" 5 None (hoursAgo 5.0) true
                  datedEntry "EPDUB001" "SERDUB1" "SERDUB1S2" 5 None (hoursAgo 3.0) true ]

        do!
            Db.execute
                app.Db
                """INSERT INTO mal_anime (mal_id, url, title, raw, data_hash, fetched_at, checked_at) VALUES
                     (900002, 'https://myanimelist.net/anime/900002', 'Dubbed', '{}', 'x', now(), now());
                   INSERT INTO cr_season_matches (season_id, status, mal_id, method, shared_from) VALUES
                     ('SERDUB1S1', 'matched', 900002, 'search', NULL),
                     ('SERDUB1S2', 'matched', 900002, 'dub', 'SERDUB1S1')"""

        let! _ = drainOutbox app

        let content = text (bodyOf (publishes app).Head) "content"
        Assert.Contains("Watched 1 episode of", content)
        Assert.Contains("EPORIG01", content)
        Assert.DoesNotContain("EPDUB001", content)
    }

// ---------------------------------------------------------------------------
// The request is frozen at the first attempt
// ---------------------------------------------------------------------------

[<Fact>]
let ``a retry sends exactly the first attempt's content and url even when the window's episodes changed`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true
        do! importAt w [ entry "EPFIRST1" "SERIESA" 1 3.0 ]

        // the first publish fails transiently
        app.Megafono.FailNext 1
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)

        let! state = Db.dump app.Db "SELECT state, attempts, last_error FROM fediverse_outbox WHERE kind = 'post_watch_update'"
        Assert.Equal<string list>([ "pending|1|503 app_unavailable" ], state)
        let! rowState = states app
        Assert.Equal<string list>([ "pending" ], rowState)

        // a later import adds an episode inside the window (watched before the window closed): the same row, the same day
        do! importAt w [ entry "EPSECOND" "SERIESA" 2 1.0 ]

        let! _ = drainOutboxAt app (DateTimeOffset.UtcNow.AddMinutes 5.0)
        let calls = publishes app
        Assert.Equal(2, calls.Length)

        // identical body (content and url) and key; the fake answers 409 for a different body under the same key
        Assert.Equal(calls[0].Body, calls[1].Body)
        Assert.Equal(calls[0].IdempotencyKey, calls[1].IdempotencyKey)
        let content = text (bodyOf calls[1]) "content"
        Assert.Contains("EPFIRST1", content)
        Assert.DoesNotContain("EPSECOND", content)

        let! stored = Db.scalar<string> app.Db "SELECT content FROM watch_updates" []
        Assert.Equal(content, stored)
        let! rows = states app
        Assert.Equal<string list>([ "published" ], rows)
    }

// ---------------------------------------------------------------------------
// Turning off, leaving Public, rebuilding
// ---------------------------------------------------------------------------

/// A user with one published update
let private withPublishedUpdate (app: TestApp) : Task<World * string> =
    task {
        let! w = join app "alice" true
        do! importAt w [ entry "EPPOSTED" "SERIESA" 1 2.0 ]
        let! _ = drainOutbox app
        let! objectId = Db.scalar<string> app.Db "SELECT object_id FROM watch_updates" []
        return w, objectId
    }

[<Fact>]
let ``turning updates off after a post deletes it on Megafono, marks the row deleted and records the event once`` () =
    task {
        use! app = appWith []
        let! w, objectId = withPublishedUpdate app

        let! off = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "off" ])
        Assert.Equal(302, int off.StatusCode)
        Assert.Equal("/account?msg=watch-updates-off#watch-updates", string off.Headers.Location)
        let! _ = drainOutbox app

        let calls = deletes app
        Assert.Equal(1, calls.Length)
        Assert.Equal($"/internal/objects/{objectId}", calls[0].Path)
        let! rows = updates app
        Assert.Equal<string list>([ $"deleted|{objectId}" ], rows)
        let! setting = Db.count app.Db "watch_update_settings"
        Assert.Equal(0L, setting)

        // a repeat changes nothing: no second event, no second removal
        let! again = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "off" ])
        Assert.Equal(302, int again.StatusCode)
        let! _ = drainOutbox app
        Assert.Equal(1, (deletes app).Length)
        let! kindCount = outboxKinds app "delete_watch_updates"
        Assert.Equal(1L, kindCount)

        let! recorded = events app
        Assert.Equal<string list>([ "watch_updates_enabled"; "watch_updates_disabled" ], recorded)

        // and no new post while it is off
        do! importAt w [ entry "EPAFTER2" "SERIESA" 2 1.0 ]
        let! _ = drainOutbox app
        Assert.Equal(1, (publishes app).Length)
    }

[<Fact>]
let ``a profile that leaves Public deletes its posted updates, and becoming public again deletes nothing more`` () =
    task {
        use! app = appWith []
        let! w, objectId = withPublishedUpdate app

        let! members = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "members" ])
        Assert.Equal(302, int members.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(1, (deletes app).Length)
        let! rows = updates app
        Assert.Equal<string list>([ $"deleted|{objectId}" ], rows)

        // members -> private leaves nothing to delete; private -> public starts posting new updates only
        let! priv = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "private" ])
        Assert.Equal(302, int priv.StatusCode)
        let! pub = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "public" ])
        Assert.Equal(302, int pub.StatusCode)
        let! _ = drainOutbox app

        Assert.Equal(1, (deletes app).Length)
        let! kindCount = outboxKinds app "delete_watch_updates"
        Assert.Equal(1L, kindCount)
        let! after = updates app
        Assert.Equal<string list>([ $"deleted|{objectId}" ], after)
    }

[<Fact>]
let ``the removal treats a missing object as deleted and retries a transient answer`` () =
    task {
        use! app = appWith []
        let! w, _ = withPublishedUpdate app

        app.Megafono.Route("DELETE /internal/objects/*", [ Status 503; Status 404 ])
        let! _ = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "off" ])
        let! _ = drainOutbox app
        Assert.Equal(1, (deletes app).Length)
        let! first = states app
        Assert.Equal<string list>([ "published" ], first)

        let! _ = drainOutboxAt app (DateTimeOffset.UtcNow.AddMinutes 5.0)
        Assert.Equal(2, (deletes app).Length)
        let! second = states app
        Assert.Equal<string list>([ "deleted" ], second)
    }

[<Fact>]
let ``user-rebuild posts nothing that was already posted`` () =
    task {
        use! app = appWith []
        let! w, _ = withPublishedUpdate app

        // move the row to yesterday so the one-a-day rule can't hide a window that covers the episode again
        do! Db.execute app.Db "UPDATE watch_updates SET day = day - 1"

        let! queued = RebuildUser.rebuildUser app.Db "alice"
        Assert.Equal(Some 1, queued)
        let! _ = processAllFor app
        let! _ = drainOutbox app

        Assert.Equal(1, (publishes app).Length)
        let! count = Db.count app.Db "watch_updates"
        Assert.Equal(1L, count)
        let! kindCount = outboxKinds app "post_watch_update"
        Assert.Equal(1L, kindCount)
        ignore w
    }

[<Fact>]
let ``a watch update that fails never fails the import: the history is stored and nothing is queued`` () =
    task {
        use! app = appWith []
        let! w = join app "alice" true

        // queueAfterImport's statement now fails (its table is gone)
        do! Db.execute app.Db "ALTER TABLE watch_updates RENAME TO watch_updates_gone"

        do! importAt w [ entry "EPA00005" "SERIESA" 5 2.0 ]

        let! status = Db.scalar<string> app.Db "SELECT status FROM cr_imports" []
        Assert.Equal("completed", status)
        let! watched = Db.count app.Db "watch_events"
        Assert.Equal(1L, watched)
        let! kindCount = outboxKinds app "post_watch_update"
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

        let! response = browser.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
        Assert.Equal(404, int response.StatusCode)

        let! page = browser.Get "/account"
        Assert.DoesNotContain("data-account=\"watch-updates\"", page.Html)
        Assert.DoesNotContain("data-watch-updates-state", page.Html)

        // even an opted-in row (put there by hand) queues nothing without federation
        do! Db.execute app.Db $"INSERT INTO watch_update_settings (user_id, enabled_at) VALUES ('{userId}', now() - interval '1 day')"
        let! _ = submitAccepted app key (envelopeOf [ historyUrl RealAccountId, pageOf [ entry "EPOFF001" "SERIESA" 1 1.0 ] ])
        let! _ = processAllFor app
        let! updatesCount = Db.count app.Db "watch_updates"
        let! outboxCount = Db.count app.Db "fediverse_outbox"
        Assert.Equal(0L, updatesCount)
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
        Assert.Contains("data-account=\"watch-updates\"", off.Html)
        Assert.Contains("id=\"watch-updates\"", off.Html)
        Assert.Contains("data-watch-updates-state=\"off\"", off.Html)
        // the default here is public: nothing to warn about
        Assert.DoesNotContain("data-watch-updates-not-public", off.Html)

        let! on = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
        Assert.Equal(302, int on.StatusCode)
        Assert.Equal("/account?msg=watch-updates-on#watch-updates", string on.Headers.Location)
        let! onPage = w.Browser.Follow on
        Assert.Contains("data-account-notice=\"watch-updates-on\"", onPage.Html)
        Assert.Contains("data-watch-updates-state=\"on\"", onPage.Html)

        // turning on twice keeps the first moment and records one event
        let! firstAt = Db.scalar<DateTime> app.Db "SELECT enabled_at FROM watch_update_settings" []
        let! again = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
        Assert.Equal(302, int again.StatusCode)
        let! secondAt = Db.scalar<DateTime> app.Db "SELECT enabled_at FROM watch_update_settings" []
        Assert.Equal(firstAt, secondAt)
        let! recorded = events app
        Assert.Equal<string list>([ "watch_updates_enabled" ], recorded)

        // anything but on or off: the notice says so and nothing changes
        let! invalid = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "maybe" ])
        Assert.Equal("/account?msg=watch-updates-invalid#watch-updates", string invalid.Headers.Location)
        let! invalidPage = w.Browser.Follow invalid
        Assert.Contains("data-account-notice=\"watch-updates-invalid\"", invalidPage.Html)
        Assert.Contains("data-watch-updates-state=\"on\"", invalidPage.Html)

        let! missing = w.Browser.PostForm("/account", "/account/watch-updates", [])
        Assert.Equal("/account?msg=watch-updates-invalid#watch-updates", string missing.Headers.Location)

        let! offResponse = w.Browser.PostForm("/account", "/account/watch-updates", [ "enabled", "off" ])
        let! offPage = w.Browser.Follow offResponse
        Assert.Contains("data-account-notice=\"watch-updates-off\"", offPage.Html)
        Assert.Contains("data-watch-updates-state=\"off\"", offPage.Html)
    }

[<Fact>]
let ``a profile that isn't public says nothing is posted until it is`` () =
    task {
        use! app = appWith [ "Frunchy:Profiles:DefaultVisibility", "" ]
        let! w = join app "alice" false

        let! page = w.Browser.Get "/account"
        Assert.Contains("data-watch-updates-not-public", page.Html)

        let! _ = w.Browser.PostForm("/account", "/account/visibility", [ "visibility", "public" ])
        let! after = w.Browser.Get "/account"
        Assert.DoesNotContain("data-watch-updates-not-public", after.Html)
    }

[<Fact>]
let ``the signed-out route is refused and the section and notices speak Spanish`` () =
    task {
        use! app = appWith []

        // signed out: the sign-in redirect, nothing changes
        use anonymous = app.NewBrowser()
        let! refused = anonymous.PostFormWithoutToken("/account/watch-updates", [ "enabled", "on" ])
        Assert.NotEqual(200, int refused.StatusCode)
        let! none = Db.count app.Db "watch_update_settings"
        Assert.Equal(0L, none)

        let! invite = createInvite app
        use spanish = app.NewBrowser "es"
        let! signUp = submitSignUp spanish "sub-ana" invite "ana" "Ana"
        Assert.Equal(302, int signUp.StatusCode)

        let! page = spanish.Get "/account"
        Assert.Contains("Novedades de visionado", page.Html)

        let! on = spanish.PostForm("/account", "/account/watch-updates", [ "enabled", "on" ])
        let! onPage = spanish.Follow on
        Assert.Contains("Las novedades de visionado est", onPage.Html)
        Assert.Contains("data-watch-updates-state=\"on\"", onPage.Html)
    }
