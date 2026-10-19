/// Feature 015 (US1, T005): Megáfono's actor lookup, by handle and by id, for users and series
module FrunchyBackend.Tests.MegafonoActorsTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private bearer = $"Bearer {FakeMegafono.Token}"

/// GET with an Authorization header (None: none at all)
let private get (app: TestApp) (authorization: string option) (path: string) : Task<HttpStatusCode * JsonElement option> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        authorization |> Option.iter (fun value -> request.Headers.TryAddWithoutValidation("Authorization", value) |> ignore)
        use! response = app.Client.SendAsync request
        let! body = response.Content.ReadAsStringAsync()

        if response.StatusCode = HttpStatusCode.OK then
            use doc = JsonDocument.Parse body
            return response.StatusCode, Some(doc.RootElement.Clone())
        else
            return response.StatusCode, None
    }

let private lookup (app: TestApp) (path: string) = get app (Some bearer) path

let private text (json: JsonElement) (name: string) = json.GetProperty(name).GetString()

let private ok (status: HttpStatusCode, json: JsonElement option) =
    Assert.Equal(HttpStatusCode.OK, status)
    json.Value

/// A federated app with a user "animenerd" and the 2026 sample imported
let private sampleApp () : Task<TestApp> =
    task {
        let! app = newFederatedApp ()
        let! key = TestHelpers.createUser app.Db "animenerd"
        let! _ = TestHelpers.submitAccepted app key (load2026Sample ())
        let! _ = TestHelpers.processAll app.Db
        return app
    }

let private enrich (app: TestApp) =
    task {
        MalFixtures.routeSampleCatalog app.Mal
        let! _ = MalFixtures.enrichAll app
        return ()
    }

/// A series of the sample with a slug, and the facts the actor must show
let private pickSeries (app: TestApp) (extra: string) : Task<string * string * string> =
    task {
        let! rows =
            Db.dump
                app.Db
                $"""SELECT s.id, s.slug, s.title FROM cr_series s
                    WHERE s.slug IS NOT NULL AND s.title IS NOT NULL AND ({extra})
                    ORDER BY s.id LIMIT 1"""

        let p = (List.exactlyOne rows).Split '|'
        return p[0], p[1], p[2]
    }

let private userId (app: TestApp) (username: string) =
    Db.scalar<Guid> app.Db "SELECT id FROM users WHERE username = @u" [ "u", box username ]

let private handleOf (app: TestApp) (seriesId: string) =
    Db.scalar<string> app.Db "SELECT handle FROM series_handles WHERE series_id = @s" [ "s", box seriesId ]

// ---------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------

[<Fact>]
let ``a user is found by handle with every field`` () =
    task {
        use! app = sampleApp ()
        let! id = userId app "animenerd"
        let! result = lookup app "/megafono/actors?handle=animenerd"
        let json = ok result

        Assert.Equal(string id, text json "id")
        Assert.Equal("animenerd", text json "handle")
        Assert.Equal("ANIMENERD", text json "name")
        Assert.Equal("https://frunchy.test/u/animenerd", text json "profileUrl")
        Assert.Equal("Person", text json "type")
        Assert.False(json.GetProperty("locked").GetBoolean())
        Assert.False(json.TryGetProperty("iconUrl") |> fst)
    }

[<Fact>]
let ``a user is found by id and the answer is the same`` () =
    task {
        use! app = sampleApp ()
        let! id = userId app "animenerd"
        let! byHandle = lookup app "/megafono/actors?handle=animenerd"
        let! byId = lookup app $"/megafono/actors/{id}"
        Assert.Equal((ok byHandle).GetRawText(), (ok byId).GetRawText())
    }

[<Fact>]
let ``the handle is matched without regard to case`` () =
    task {
        use! app = sampleApp ()
        let! result = lookup app "/megafono/actors?handle=AnimeNerd"
        Assert.Equal("animenerd", text (ok result) "handle")
    }

[<Fact>]
let ``unknown handles and ids are 404`` () =
    task {
        use! app = sampleApp ()

        for path in
            [ "/megafono/actors?handle=nobody"
              "/megafono/actors?handle="
              "/megafono/actors"
              "/megafono/actors?handle=has.dot"
              "/megafono/actors?handle=a%20b"
              $"/megafono/actors/{Guid.NewGuid()}"
              "/megafono/actors/not-a-guid"
              "/megafono/actors/series-"
              "/megafono/actors/series-lowercase"
              "/megafono/actors/series-ZZZZZZZZ" ] do
            let! status, _ = lookup app path
            Assert.True((status = HttpStatusCode.NotFound), $"{path} answered {status}")
    }

[<Fact>]
let ``a username with a trailing dash cannot federate`` () =
    task {
        use! app = sampleApp ()
        let id = Guid.NewGuid()
        do! Db.execute app.Db $"INSERT INTO users (id, username, display_name) VALUES ('{id}', 'legacy-', 'Legacy')"

        let! byHandle, _ = lookup app "/megafono/actors?handle=legacy-"
        let! byId, _ = lookup app $"/megafono/actors/{id}"
        Assert.Equal(HttpStatusCode.NotFound, byHandle)
        Assert.Equal(HttpStatusCode.NotFound, byId)
    }

[<Fact>]
let ``a deleted user is 404 by handle and by id`` () =
    task {
        use! app = sampleApp ()
        let! id = userId app "animenerd"
        let! before, _ = lookup app "/megafono/actors?handle=animenerd"
        Assert.Equal(HttpStatusCode.OK, before)

        let! deleted = AccountActions.deleteUser app.Db false id
        Assert.True deleted.IsSome

        let! byHandle, _ = lookup app "/megafono/actors?handle=animenerd"
        let! byId, _ = lookup app $"/megafono/actors/{id}"
        Assert.Equal(HttpStatusCode.NotFound, byHandle)
        Assert.Equal(HttpStatusCode.NotFound, byId)
    }

// ---------------------------------------------------------------------------
// Series
// ---------------------------------------------------------------------------

[<Fact>]
let ``a series is a Group found by series id, its handle created lazily and stable`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, slug, title = pickSeries app "true"
        let! none = Db.count app.Db "series_handles"
        Assert.Equal(0L, none)

        let! first = lookup app $"/megafono/actors/series-{seriesId}"
        let json = ok first
        let expectedHandle = Fediverse.baseSeriesHandle (Some slug) seriesId

        Assert.Equal($"series-{seriesId}", text json "id")
        Assert.Equal(expectedHandle, text json "handle")
        Assert.Equal(title, text json "name")
        Assert.Equal($"https://frunchy.test/anime/{seriesId}", text json "profileUrl")
        Assert.Equal("Group", text json "type")
        Assert.False(json.GetProperty("locked").GetBoolean())

        let! stored = handleOf app seriesId
        Assert.Equal(expectedHandle, stored)

        // The slug changing later doesn't change the handle, and asking again changes nothing
        do! Db.execute app.Db $"UPDATE cr_series SET slug = 'something-else' WHERE id = '{seriesId}'"
        let! second = lookup app $"/megafono/actors/series-{seriesId}"
        Assert.Equal(expectedHandle, text (ok second) "handle")
        let! rows = Db.count app.Db "series_handles"
        Assert.Equal(1L, rows)
    }

[<Fact>]
let ``a series is found by its handle once it has one`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, slug, _ = pickSeries app "true"
        let handle = Fediverse.baseSeriesHandle (Some slug) seriesId

        // A handle that nothing created yet isn't an account
        let! before, _ = lookup app $"/megafono/actors?handle={handle}"
        Assert.Equal(HttpStatusCode.NotFound, before)

        let! _ = lookup app $"/megafono/actors/series-{seriesId}"
        let! byHandle = lookup app $"/megafono/actors?handle={handle}"
        let! byId = lookup app $"/megafono/actors/series-{seriesId}"
        Assert.Equal((ok byId).GetRawText(), (ok byHandle).GetRawText())
        Assert.Equal($"series-{seriesId}", text (ok byHandle) "id")
    }

[<Fact>]
let ``a series whose handle a user already holds gets a suffix`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, slug, _ = pickSeries app "true"
        let wanted = Fediverse.baseSeriesHandle (Some slug) seriesId
        Assert.True(wanted.Length >= 3, "the sample's slug is too short for a username")
        let! _ = TestHelpers.createUser app.Db wanted

        let! result = lookup app $"/megafono/actors/series-{seriesId}"
        Assert.Equal(wanted + "-2", text (ok result) "handle")

        // The user is still the user
        let! user = lookup app $"/megafono/actors?handle={wanted}"
        Assert.Equal("Person", text (ok user) "type")
    }

[<Fact>]
let ``two series that would share a handle get different ones`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, _, _ = pickSeries app "true"
        let! other, _, _ = pickSeries app $"s.id <> '{seriesId}'"
        do! Db.execute app.Db $"UPDATE cr_series SET slug = 'same-slug' WHERE id IN ('{seriesId}', '{other}')"

        let! a = lookup app $"/megafono/actors/series-{seriesId}"
        let! b = lookup app $"/megafono/actors/series-{other}"
        Assert.Equal("same-slug", text (ok a) "handle")
        Assert.Equal("same-slug-2", text (ok b) "handle")
    }

[<Fact>]
let ``a series without a catalog row but with episodes still answers`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, _, _ = pickSeries app "true"
        do! Db.execute app.Db $"UPDATE cr_series SET title = NULL, slug = NULL WHERE id = '{seriesId}'"

        let! result = lookup app $"/megafono/actors/series-{seriesId}"
        let json = ok result
        Assert.False(String.IsNullOrWhiteSpace(text json "name"))
        Assert.Equal($"series-{seriesId.ToLowerInvariant()}", text json "handle")
    }

[<Fact>]
let ``the icon is the MyAnimeList poster and only over https on MyAnimeList`` () =
    task {
        use! app = sampleApp ()

        // Not matched yet: no icon
        let! seriesId, _, _ = pickSeries app "true"
        let! plain = lookup app $"/megafono/actors/series-{seriesId}"
        Assert.False((ok plain).TryGetProperty("iconUrl") |> fst)

        do! enrich app

        let! matched =
            Db.dump
                app.Db
                """SELECT e.series_id, a.mal_id, a.image_url
                   FROM cr_episodes e
                   JOIN watch_events w ON w.cr_episode_id = e.id
                   JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                   JOIN mal_anime a ON a.mal_id = m.mal_id AND a.image_url IS NOT NULL
                   WHERE e.duration_ms IS NOT NULL
                   ORDER BY e.series_id, a.mal_id LIMIT 1"""

        let parts = (List.exactlyOne matched).Split '|'
        let series, malId, image = parts[0], parts[1], parts[2]
        Assert.StartsWith("https://", image)

        let! withIcon = lookup app $"/megafono/actors/series-{series}"
        Assert.Equal(image, text (ok withIcon) "iconUrl")

        for bad in [ "http://cdn.myanimelist.net/x.jpg"; "https://evil.test/x.jpg"; "https://myanimelist.net.evil.test/x.jpg"; "/relative.jpg" ] do
            do! Db.execute app.Db $"UPDATE mal_anime SET image_url = '{bad}' WHERE mal_id = {malId}"
            let! result = lookup app $"/megafono/actors/series-{series}"
            Assert.False((ok result).TryGetProperty("iconUrl") |> fst, $"accepted {bad}")
    }

// ---------------------------------------------------------------------------
// The bearer token
// ---------------------------------------------------------------------------

[<Fact>]
let ``a missing, wrong or padded token is 401 on every route, whatever is asked`` () =
    task {
        use! app = sampleApp ()
        let! id = userId app "animenerd"
        let token = FakeMegafono.Token

        let paths =
            [ "/megafono/actors?handle=animenerd"
              "/megafono/actors?handle=nobody"
              $"/megafono/actors/{id}"
              "/megafono/actors/series-ZZZZZZZZ" ]

        let wrong =
            [ None
              Some ""
              Some "Bearer "
              Some "Bearer wrong"
              Some token
              Some $"bearer {token}"
              // (white space around the value never reaches the app: the HTTP stack normalises the header)
              Some $"Bearer {token}0"
              Some $"Bearer 0{token}"
              Some $"Bearer {token}{token}"
              Some $"Bearer {token}x"
              Some $"Bearer {token.Substring(0, token.Length - 1)}"
              Some $"Basic {token}" ]

        for path in paths do
            for authorization in wrong do
                let! status, _ = get app authorization path
                Assert.True((status = HttpStatusCode.Unauthorized), $"{path} with {authorization} answered {status}")

        let! right, _ = get app (Some bearer) paths[0]
        Assert.Equal(HttpStatusCode.OK, right)
        let! unknown, _ = get app (Some bearer) paths[1]
        Assert.Equal(HttpStatusCode.NotFound, unknown)
    }

// ---------------------------------------------------------------------------
// Federation off
// ---------------------------------------------------------------------------

[<Fact>]
let ``with federation off every actor route is 404, even with the right header`` () =
    task {
        use! app = newApp ()
        let! key = TestHelpers.createUser app.Db "animenerd"
        ignore key
        let! id = userId app "animenerd"

        for path in [ "/megafono/actors?handle=animenerd"; $"/megafono/actors/{id}"; "/megafono/actors/series-ABCDEFGH" ] do
            for authorization in [ None; Some bearer ] do
                let! status, _ = get app authorization path
                Assert.True((status = HttpStatusCode.NotFound), $"{path} answered {status}")
    }

// ---------------------------------------------------------------------------
// Sign-up
// ---------------------------------------------------------------------------

[<Fact>]
let ``sign-up refuses a username equal to a series handle`` () =
    task {
        use! app = sampleApp ()
        let! seriesId, _, _ = pickSeries app "true"
        let! _ = lookup app $"/megafono/actors/series-{seriesId}"
        let! handle = handleOf app seriesId
        Assert.True(handle.Length >= 3 && Username.tryCreateNew handle |> Option.isSome, $"'{handle}' can't be a username")

        let! invite = createInvite app
        use browser = app.NewBrowser()
        let! before = Db.count app.Db "users"
        let! response = submitSignUp browser "sub-handle" invite handle "Handle"
        let! html = response.Content.ReadAsStringAsync()
        let! after = Db.count app.Db "users"

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        Assert.Contains("That username is already taken.", WebUtility.HtmlDecode html)
        Assert.Equal(before, after)

        // The refused attempt didn't consume the invite: another name works with it
        let! second = submitSignUp (app.NewBrowser()) "sub-handle-2" invite "someone-else" "Someone"
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode)
    }
