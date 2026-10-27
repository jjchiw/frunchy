module FrunchyBackend.Tests.IdentityTests

open Xunit
open FrunchyBackend.Domains.Identity

[<Theory>]
[<InlineData("animegolden", "animegolden")>]
[<InlineData("AnimeGolden", "animegolden")>]
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

// Feature 013: new names can't end in a dash; lookups keep the old rule so existing accounts resolve

[<Theory>]
[<InlineData("abc-", false, true)>]
[<InlineData("ABC-", false, true)>]
[<InlineData("abc_", true, true)>]
[<InlineData("a-b", true, true)>]
[<InlineData("a1b", true, true)>]
[<InlineData("a--", false, true)>]
[<InlineData("-ab", false, false)>]
[<InlineData("abc", true, true)>]
let ``new names refuse a trailing dash while lookups still accept it`` (raw: string, newOk: bool, lookupOk: bool) =
    Assert.Equal(newOk, (Username.tryCreateNew raw).IsSome)
    Assert.Equal(lookupOk, (Username.tryCreate raw).IsSome)

[<Theory>]
[<InlineData(2, false)>]
[<InlineData(3, true)>]
[<InlineData(30, true)>]
[<InlineData(31, false)>]
let ``new names are 3 to 30 characters, like lookups`` (length: int, ok: bool) =
    let name = System.String('a', length)
    Assert.Equal(ok, (Username.tryCreateNew name).IsSome)
    Assert.Equal(ok, (Username.tryCreate name).IsSome)

[<Theory>]
[<InlineData("admin")>]
[<InlineData("Account")>]
[<InlineData("signin")>]
[<InlineData("frunchy")>]
let ``reserved names are refused by both rules`` (raw: string) =
    Assert.Equal(None, Username.tryCreateNew raw)
    Assert.Equal(None, Username.tryCreate raw)

[<Fact>]
let ``new names are trimmed and lowercased, and null is refused`` () =
    Assert.Equal(Some "jj_chiw-01", Username.tryCreateNew "  JJ_Chiw-01 ")
    Assert.Equal(None, Username.tryCreateNew null)

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
