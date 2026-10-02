module FrunchyBackend.Tests.WhoAmITests

open System.Text.Json
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``who am I before and after the first sync`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"

        let! response = getWithKey app.Client (Some key) "/api/me"
        let! me = readJson response
        Assert.Equal(200, int response.StatusCode)
        Assert.Equal("animenerd", me.GetProperty("username").GetString())
        Assert.Equal("ANIMENERD", me.GetProperty("displayName").GetString())
        Assert.EndsWith("/u/animenerd", me.GetProperty("dashboardUrl").GetString())
        Assert.StartsWith("http", me.GetProperty("dashboardUrl").GetString())
        Assert.Equal(JsonValueKind.Null, me.GetProperty("lastSyncAt").ValueKind)
        Assert.False(me.GetProperty("historyOwnerBound").GetBoolean())

        let! _ = submitAccepted app key (load2022Envelope ())
        let! afterResponse = getWithKey app.Client (Some key) "/api/me"
        let! after = readJson afterResponse
        Assert.EndsWith("Z", after.GetProperty("lastSyncAt").GetString())
        Assert.True(after.GetProperty("historyOwnerBound").GetBoolean())
    }

[<Fact>]
let ``unknown key is unauthorized`` () =
    task {
        use! app = newApp ()
        let! response = getWithKey app.Client (Some "frk_unknown") "/api/me"
        let! body = readJson response
        Assert.Equal(401, int response.StatusCode)
        Assert.Equal("unauthorized", body.GetProperty("error").GetString())
        let! noKey = getWithKey app.Client None "/api/me"
        Assert.Equal(401, int noKey.StatusCode)
    }
