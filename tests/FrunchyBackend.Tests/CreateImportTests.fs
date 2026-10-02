module FrunchyBackend.Tests.CreateImportTests

open System
open System.Text
open Xunit
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.TestHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

let private otherAccount = "11111111-2222-3333-4444-555555555555"

[<Fact>]
let ``accepted submission returns 202 and stores the page verbatim`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! status, body = submit app key (load2022Envelope ())

        Assert.Equal(202, status)
        Assert.Equal("pending", body.GetProperty("status").GetString())
        Assert.Equal(101, body.GetProperty("entriesReceived").GetInt32())
        let importId = body.GetProperty("importId").GetGuid()
        Assert.Equal($"/api/imports/{importId}", body.GetProperty("statusUrl").GetString())

        let! stored = Db.scalar<string> app.Db "SELECT body::text FROM cr_import_pages WHERE import_id = @id" [ "id", box importId ]
        Assert.Equal(load2022PageBody().Trim(), stored)

        let! url = Db.scalar<string> app.Db "SELECT url FROM cr_import_pages WHERE import_id = @id" [ "id", box importId ]
        Assert.Equal(historyUrl RealAccountId, url)
    }

[<Fact>]
let ``first accepted submission binds the user to its history owner`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2026Sample ())
        let! account = Db.scalar<string> app.Db "SELECT cr_account_id FROM users WHERE username = 'animenerd'" []
        Assert.Equal(RealAccountId, account)
    }

/// Asserts the status/code and that nothing was stored or bound
let private assertRejected (app: TestApp) (key: string option) (json: string) (expectedStatus: int) (expectedCode: string) =
    task {
        let! before = snapshot app.Db
        let! response = postImport app.Client key json
        let! body = readJson response
        let! after = snapshot app.Db
        Assert.Equal(expectedStatus, int response.StatusCode)
        Assert.Equal(expectedCode, body.GetProperty("error").GetString())
        Assert.False(String.IsNullOrWhiteSpace(body.GetProperty("message").GetString()))
        Assert.Equal(before, after)
    }

[<Fact>]
let ``missing or unknown key is unauthorized and stores nothing`` () =
    task {
        use! app = newApp ()
        let! _ = createUser app.Db "animenerd"
        do! assertRejected app None (load2022Envelope ()) 401 "unauthorized"
        do! assertRejected app (Some "frk_not-a-real-key") (load2022Envelope ()) 401 "unauthorized"
    }

[<Fact>]
let ``malformed submissions are invalid and store nothing`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let url = historyUrl RealAccountId
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        let hundredAndOne = envelopeOf [ for _ in 1..101 -> url, page ]

        for json in
            [ "this is not json"
              "[]"
              "{}"
              """{"pages":[]}"""
              """{"pages":"nope"}"""
              hundredAndOne
              $"""{{"pages":[{{"body":{page}}}]}}"""
              $"""{{"pages":[{{"url":"{url}"}}]}}"""
              $"""{{"pages":[{{"url":"{url}","body":{{"total":1}}}}]}}"""
              $"""{{"profileId":"p","pages":[{{"url":"{url}","body":{page},"capturedAt":"yesterday"}}]}}"""
              $"""{{"profileId":42,"pages":[{{"url":"{url}","body":{page}}}]}}""" ] do
            do! assertRejected app (Some key) json 400 "invalid_submission"
    }

[<Fact>]
let ``pages without an identifiable history owner are rejected`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        do! assertRejected app (Some key) (envelopeOf [ "https://www.crunchyroll.com/history", page ]) 400 "history_owner_missing"
        do! assertRejected app (Some key) (envelopeOf [ historyUrl RealAccountId, page; historyUrl otherAccount, page ]) 400 "history_owner_missing"
    }

[<Fact>]
let ``a bound user can't submit another history owner`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! _ = submitAccepted app key (load2022Envelope ())
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        do! assertRejected app (Some key) (envelopeOf [ historyUrl otherAccount, page ]) 409 "history_owner_mismatch"
    }

[<Fact>]
let ``a history owner bound to one user can't be used by another`` () =
    task {
        use! app = newApp ()
        let! keyA = createUser app.Db "usera"
        let! keyB = createUser app.Db "userb"
        let! _ = submitAccepted app keyA (load2022Envelope ())
        do! assertRejected app (Some keyB) (load2026Sample ()) 409 "history_owner_taken"
    }

[<Fact>]
let ``bodies over 30,000,000 bytes are too large and store nothing`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let padding = String('x', 30_000_001)
        let json = $"""{{"pages":[{{"url":"{historyUrl RealAccountId}","body":{{"data":[],"pad":"{padding}"}}}}]}}"""
        do! assertRejected app (Some key) json 413 "payload_too_large"
    }

[<Fact>]
let ``re-sending an accepted page is accepted again (dedup happens in processing)`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let! first = submitAccepted app key (load2022Envelope ())
        let! second = submitAccepted app key (load2022Envelope ())
        Assert.NotEqual(first, second)
    }

[<Fact>]
let ``a submission without profileId is rejected because the url doesn't identify the profile`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "animenerd"
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        do! assertRejected app (Some key) (envelopeWithoutProfile [ historyUrl RealAccountId, page ]) 400 "history_owner_missing"
        do! assertRejected app (Some key) (load2026SampleRaw ()) 400 "history_owner_missing"
        do! assertRejected app (Some key) (envelopeForProfile " " [ historyUrl RealAccountId, page ]) 400 "history_owner_missing"
    }

[<Fact>]
let ``two profiles of one Crunchyroll account are different history owners`` () =
    task {
        use! app = newApp ()
        let! keyA = createUser app.Db "usera"
        let! keyB = createUser app.Db "userb"
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        // same account id in the URL, as in the real second-profile capture
        let! _ = submitAccepted app keyA (envelopeForProfile "profile-a" [ historyUrl RealAccountId, page ])
        let! _ = submitAccepted app keyB (envelopeForProfile "profile-b" [ historyUrl RealAccountId, page ])
        let! owners = Db.dump app.Db "SELECT username, cr_account_id, cr_profile_id FROM users ORDER BY username"
        Assert.Equal<string list>([ $"usera|{RealAccountId}|profile-a"; $"userb|{RealAccountId}|profile-b" ], owners)
    }

[<Fact>]
let ``another profile's history can't be mixed into a bound user`` () =
    task {
        use! app = newApp ()
        let! key = createUser app.Db "usera"
        let page = pageOf [ syntheticEntry "E1" "S1" DateTime.UtcNow true 1400 ]
        let! _ = submitAccepted app key (envelopeForProfile "profile-a" [ historyUrl RealAccountId, page ])
        do! assertRejected app (Some key) (envelopeForProfile "profile-b" [ historyUrl RealAccountId, page ]) 409 "history_owner_mismatch"
    }
