module FrunchyBackend.Tests.OperationsTests

open System
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private otherAccount = "11111111-2222-3333-4444-555555555555"

/// The dashboard's figures and recent list, as comparable text
let private dashboardSummary (app: TestApp) (username: string) =
    task {
        let! response = getWithLanguage app.Client "en" $"/u/{username}"
        let! html = readHtml response
        let stats = Regex.Matches(html, "data-stat=\"(\\w+)\" data-value=\"([^\"]+)\"") |> Seq.map _.Value |> List.ofSeq
        let recent = Regex.Split(html, "data-recent-item") |> Array.skip 1 |> List.ofArray
        return response.StatusCode, stats, recent
    }

let private userRows (db) (table: string) (username: string) =
    let sql =
        match table with
        | "users" -> "SELECT count(*) FROM users WHERE username = @u"
        | "cr_imports" -> "SELECT count(*) FROM cr_imports i JOIN users u ON u.id = i.user_id WHERE u.username = @u"
        | "watch_events" -> "SELECT count(*) FROM watch_events w JOIN users u ON u.id = w.user_id WHERE u.username = @u"
        | other -> failwith other

    Db.scalar<int64> db sql [ "u", box username ]

[<Fact>]
let ``rebuild reproduces identical figures and leaves raw pages untouched`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db

        let! beforeCode, beforeStats, beforeRecent = dashboardSummary app "animenerd"
        let! beforeEvents = watchEvents app.Db
        let! beforePages = Db.dump app.Db "SELECT import_id, page_index, md5(body::text), url FROM cr_import_pages ORDER BY 1, 2"

        let! queued = RebuildUser.rebuildUser app.Db "animenerd"
        Assert.Equal(Some 2, queued)
        let! cleared = Db.count app.Db "watch_events"
        Assert.Equal(0L, cleared)
        let! _ = processAll app.Db

        let! afterCode, afterStats, afterRecent = dashboardSummary app "animenerd"
        let! afterEvents = watchEvents app.Db
        let! afterPages = Db.dump app.Db "SELECT import_id, page_index, md5(body::text), url FROM cr_import_pages ORDER BY 1, 2"
        Assert.Equal(HttpStatusCode.OK, beforeCode)
        Assert.Equal(beforeCode, afterCode)
        Assert.Equal(4, beforeStats.Length)
        Assert.Equal<string list>(beforeStats, afterStats)
        Assert.Equal<string list>(beforeRecent, afterRecent)
        Assert.Equal<string list>(beforeEvents, afterEvents)
        Assert.Equal<string list>(beforePages, afterPages)
    }

[<Fact>]
let ``regenerating a key revokes the old one`` () =
    task {
        use! app = newApp ()
        let! oldKey = createUser app.Db "animenerd"
        let! newKey = RegenerateKey.regenerateKey app.Db "AnimeNerd"
        Assert.True newKey.IsSome
        let! oldResponse = getWithKey app.Client (Some oldKey) "/api/me"
        let! newResponse = getWithKey app.Client newKey "/api/me"
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode)
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode)
    }

[<Fact>]
let ``resetting the binding lets another history owner bind and keeps history`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = processAll app.Db
        let other = envelopeOf (syntheticPages otherAccount 1 5)
        let! status, _ = submit app key other
        Assert.Equal(409, status)

        let! reset = ResetBinding.resetBinding app.Db "animenerd"
        Assert.True reset
        let! _ = submitAccepted app key other
        let! _ = processAll app.Db
        let! account = Db.scalar<string> app.Db "SELECT cr_account_id FROM users" []
        let! events = Db.count app.Db "watch_events"
        Assert.Equal(otherAccount, account)
        Assert.Equal(101L + 5L, events)
    }

[<Fact>]
let ``deleting a user removes all of their data and nothing else`` () =
    task {
        use! app = newApp ()
        let! keyA = createUser app.Db "usera"
        let! keyB = createUser app.Db "userb"
        let! _ = submitAccepted app keyA (load2026Sample ())
        let! _ = submitAccepted app keyB (envelopeOf (syntheticPages otherAccount 2 10))
        let! _ = processAll app.Db
        let! _, bStatsBefore, _ = dashboardSummary app "userb"
        let! bPagesBefore = Db.dump app.Db "SELECT md5(body::text) FROM cr_import_pages p JOIN cr_imports i ON i.id = p.import_id JOIN users u ON u.id = i.user_id WHERE u.username = 'userb' ORDER BY 1"

        let! deleted = DeleteUser.deleteUser app.Db false "usera"
        Assert.Equal(Some { DeleteUser.Imports = 1L; DeleteUser.WatchEvents = 400L }, deleted)

        for table in [ "users"; "cr_imports"; "watch_events" ] do
            let! remaining = userRows app.Db table "usera"
            Assert.Equal(0L, remaining)

        // only userb's pages and issues are left
        let! pages = Db.count app.Db "cr_import_pages"
        let! issues = Db.count app.Db "cr_import_entry_issues"
        Assert.Equal(2L, pages)
        Assert.Equal(0L, issues)

        let! codeA, _, _ = dashboardSummary app "usera"
        let! meA = getWithKey app.Client (Some keyA) "/api/me"
        Assert.Equal(HttpStatusCode.NotFound, codeA)
        Assert.Equal(HttpStatusCode.Unauthorized, meA.StatusCode)

        let! _, bStatsAfter, _ = dashboardSummary app "userb"
        let! bPagesAfter = Db.dump app.Db "SELECT md5(body::text) FROM cr_import_pages p JOIN cr_imports i ON i.id = p.import_id JOIN users u ON u.id = i.user_id WHERE u.username = 'userb' ORDER BY 1"
        Assert.Equal<string list>(bStatsBefore, bStatsAfter)
        Assert.Equal<string list>(bPagesBefore, bPagesAfter)
    }

[<Fact>]
let ``a deleted user's pending import is never processed`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "usera"
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = DeleteUser.deleteUser app.Db false "usera"
        let! processed = processAll app.Db
        let! events = Db.count app.Db "watch_events"
        let! imports = Db.count app.Db "cr_imports"
        Assert.Equal(0, processed)
        Assert.Equal(0L, events)
        Assert.Equal(0L, imports)
    }

[<Fact>]
let ``deleting frees the history owner and the username`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "usera"
        let! _ = submitAccepted app key (load2022Envelope ())
        let! _ = DeleteUser.deleteUser app.Db false "usera"
        let! again = createUser app.Db "usera"
        let! _ = submitAccepted app again (load2022Envelope ())
        ()
    }

[<Fact>]
let ``cli exit codes`` () =
    task {
        let! db = newDatabase ()
        let! created = CreateUser.run db [ "animenerd"; "AnimeNerd" ]
        let! taken = CreateUser.run db [ "AnimeNerd"; "Other" ]
        let! invalid = CreateUser.run db [ "a b"; "X" ]
        let! refuse = DeleteUser.run db false [ "animenerd" ]
        let! stillThere = Db.count db "users"
        let! unknownRegen = RegenerateKey.run db [ "nobody" ]
        let! unknownReset = ResetBinding.run db [ "nobody" ]
        let! unknownRebuild = RebuildUser.run db [ "nobody" ]
        let! unknownDelete = DeleteUser.run db false [ "nobody"; "--yes" ]
        let! deleted = DeleteUser.run db false [ "animenerd"; "--yes" ]
        let! gone = Db.count db "users"
        Assert.Equal(0, created)
        Assert.Equal(3, taken)
        Assert.Equal(3, invalid)
        Assert.Equal(1, refuse)
        Assert.Equal(1L, stillThere)
        Assert.Equal<int list>([ 2; 2; 2; 2 ], [ unknownRegen; unknownReset; unknownRebuild; unknownDelete ])
        Assert.Equal(0, deleted)
        Assert.Equal(0L, gone)
    }

[<Fact>]
let ``user-list shows binding and last sync`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "bound"
        let! _ = createUser app.Db "unbound"
        let! _ = submitAccepted app key (load2022Envelope ())
        let! rows = ListUsers.listUsers app.Db

        match rows with
        | [ b; u ] ->
            Assert.Equal("bound", b.Username)
            Assert.True b.Bound
            Assert.Equal(1L, b.Imports)
            Assert.True b.LastSyncAt.IsSome
            Assert.False u.Bound
            Assert.Equal(None, u.LastSyncAt)
        | other -> failwith $"unexpected {other}"
    }
