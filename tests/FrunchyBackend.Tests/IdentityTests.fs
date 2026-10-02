module FrunchyBackend.Tests.IdentityTests

open Xunit
open FrunchyBackend.Domains.Identity

[<Theory>]
[<InlineData("animenerd", "animenerd")>]
[<InlineData("AnimeNerd", "animenerd")>]
[<InlineData("  jj_chiw-01 ", "jj_chiw-01")>]
[<InlineData("abc", "abc")>]
let ``valid usernames are accepted and lowercased`` (raw: string, expected: string) =
    Assert.Equal(Some expected, Username.tryCreate raw)

[<Theory>]
[<InlineData("ab")>]
[<InlineData("_leading")>]
[<InlineData("-leading")>]
[<InlineData("has space")>]
[<InlineData("dot.name")>]
[<InlineData("ñandú")>]
[<InlineData("abcdefghijklmnopqrstuvwxyz12345")>]
[<InlineData("")>]
let ``invalid usernames are rejected`` (raw: string) = Assert.Equal(None, Username.tryCreate raw)

[<Fact>]
let ``null username is rejected`` () = Assert.Equal(None, Username.tryCreate null)

[<Fact>]
let ``sync keys are frk_ plus 43 base64url chars and unique`` () =
    let a = SyncKey.generate ()
    let b = SyncKey.generate ()
    Assert.StartsWith("frk_", a)
    Assert.Equal(4 + 43, a.Length)
    Assert.Matches("^frk_[A-Za-z0-9_-]{43}$", a)
    Assert.NotEqual<string>(a, b)

[<Fact>]
let ``key hash is deterministic lowercase hex and differs from the key`` () =
    let key = SyncKey.generate ()
    Assert.Equal(SyncKey.hash key, SyncKey.hash key)
    Assert.Matches("^[0-9a-f]{64}$", SyncKey.hash key)
    Assert.DoesNotContain(key, SyncKey.hash key)
