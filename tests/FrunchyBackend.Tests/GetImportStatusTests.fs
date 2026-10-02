module FrunchyBackend.Tests.GetImportStatusTests

open System
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private status (app: TestApp) (key: string) (importId: Guid) =
    task {
        let! response = getWithKey app.Client (Some key) $"/api/imports/{importId}"
        let! body = readJson response
        return int response.StatusCode, body
    }

[<Fact>]
let ``status is pending until processed, then completed with counts and issues`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! importId = submitAccepted app key (load2022Envelope ())

        let! code, pending = status app key importId
        Assert.Equal(200, code)
        Assert.Equal("pending", pending.GetProperty("status").GetString())

        let! _ = processAll app.Db
        let! _, done' = status app key importId
        Assert.Equal("completed", done'.GetProperty("status").GetString())
        let counts = done'.GetProperty("counts")
        Assert.Equal(101, counts.GetProperty("received").GetInt32())
        Assert.Equal(100, counts.GetProperty("processed").GetInt32())
        Assert.Equal(1, counts.GetProperty("unresolved").GetInt32())
        Assert.Equal(0, counts.GetProperty("unsupported").GetInt32())
        Assert.Equal(0, counts.GetProperty("failed").GetInt32())

        let issues = done'.GetProperty("issues")
        Assert.Equal(1, issues.GetArrayLength())
        let issue = issues[0]
        Assert.Equal("unresolved", issue.GetProperty("outcome").GetString())
        Assert.Equal("G31UXDEJN", issue.GetProperty("episodeId").GetString())
        Assert.Equal(0, issue.GetProperty("page").GetInt32())
        Assert.Equal(0, issue.GetProperty("index").GetInt32())
        Assert.Contains("panel", issue.GetProperty("reason").GetString())
        Assert.EndsWith("Z", done'.GetProperty("completedAt").GetString())
    }

[<Fact>]
let ``2026 sample completes with 400 = 309 + 91`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! importId = submitAccepted app key (load2026Sample ())
        let! _ = processAll app.Db
        let! _, body = status app key importId
        let counts = body.GetProperty("counts")
        Assert.Equal(400, counts.GetProperty("received").GetInt32())
        Assert.Equal(309, counts.GetProperty("processed").GetInt32())
        Assert.Equal(91, counts.GetProperty("unresolved").GetInt32())
        Assert.Equal(91, body.GetProperty("issues").GetArrayLength())
    }

[<Fact>]
let ``another user's import is not disclosed`` () =
    task {
        use! app = newApp ()
        let! keyA = createUser app.Db "usera"
        let! keyB = createUser app.Db "userb"
        let! importId = submitAccepted app keyA (load2022Envelope ())
        let! code, body = status app keyB importId
        Assert.Equal(404, code)
        Assert.Equal("not_found", body.GetProperty("error").GetString())
    }

[<Fact>]
let ``unknown import is not found and a bad key is unauthorized`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! code, _ = status app key (Guid.NewGuid())
        Assert.Equal(404, code)
        let! response = getWithKey app.Client (Some "frk_wrong") $"/api/imports/{Guid.NewGuid()}"
        Assert.Equal(401, int response.StatusCode)
    }
