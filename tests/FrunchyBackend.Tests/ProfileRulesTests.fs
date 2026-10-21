/// Feature 017 (T004): the pure profile rules, no database and no app (the grant cookie uses an ephemeral protector)
module FrunchyBackend.Tests.ProfileRulesTests

open System
open Microsoft.AspNetCore.DataProtection
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Profiles

let private owner = Guid.NewGuid()
let private link = Guid.NewGuid()

let private access visibility (linkId: Guid option) =
    { UserId = owner
      Choice = None
      Visibility = visibility
      LinkId = linkId }

let private grant (userId: Guid) (linkId: Guid) (at: int64) =
    { UserId = userId
      LinkId = linkId
      At = at }

// ---------------------------------------------------------------------------
// canView
// ---------------------------------------------------------------------------

[<Fact>]
let ``a public profile is visible to anyone`` () =
    Assert.True(canView None [] (access Public None))
    Assert.True(canView (Some(Guid.NewGuid())) [] (access Public None))

[<Fact>]
let ``a private profile is hidden from strangers and signed-in others`` () =
    Assert.False(canView None [] (access Private None))
    Assert.False(canView (Some(Guid.NewGuid())) [] (access Private (Some link)))

[<Fact>]
let ``the owner sees their private profile`` () =
    Assert.True(canView (Some owner) [] (access Private None))

[<Fact>]
let ``a grant naming the owner and the current link opens a private profile`` () =
    Assert.True(canView None [ grant owner link 1L ] (access Private (Some link)))

[<Fact>]
let ``a grant with an old link id counts for nothing`` () =
    Assert.False(canView None [ grant owner (Guid.NewGuid()) 1L ] (access Private (Some link)))

[<Fact>]
let ``a grant does nothing once the link is turned off`` () =
    Assert.False(canView None [ grant owner link 1L ] (access Private None))

[<Fact>]
let ``a grant for another user does not open this profile`` () =
    Assert.False(canView None [ grant (Guid.NewGuid()) link 1L ] (access Private (Some link)))

// ---------------------------------------------------------------------------
// The instance setting
// ---------------------------------------------------------------------------

[<Fact>]
let ``parseDefault: missing and blank mean members`` () =
    Assert.Equal(Members, parseDefault null)
    Assert.Equal(Members, parseDefault "")
    Assert.Equal(Members, parseDefault "   ")

[<Fact>]
let ``parseDefault is case-insensitive and trimmed`` () =
    Assert.Equal(Public, parseDefault "PUBLIC")
    Assert.Equal(Public, parseDefault "public")
    Assert.Equal(Private, parseDefault " private ")
    Assert.Equal(Members, parseDefault " Members ")

[<Fact>]
let ``parseDefault refuses anything else, naming the key`` () =
    let ex = Assert.Throws<InvalidOperationException>(fun () -> parseDefault "friends" |> ignore)
    Assert.Contains(DefaultVisibilityKey, ex.Message)
    Assert.Contains("members", ex.Message)

[<Fact>]
let ``a members profile is visible to a signed-in viewer only`` () =
    Assert.True(canView (Some(Guid.NewGuid())) [] (access Members None))
    Assert.False(canView None [] (access Members None))
    Assert.True(canView None [ grant owner link 1L ] (access Members (Some link)))

[<Fact>]
let ``effective prefers the owner's choice over the default`` () =
    Assert.Equal(Private, effective Public (Some Private))
    Assert.Equal(Public, effective Private (Some Public))
    Assert.Equal(Public, effective Public None)
    Assert.Equal(Private, effective Private None)

// ---------------------------------------------------------------------------
// The grant cookie
// ---------------------------------------------------------------------------

let private protector () =
    EphemeralDataProtectionProvider().CreateProtector "Frunchy.ShareGrants.v1"

let private now = DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)

[<Fact>]
let ``the grant cookie round trips`` () =
    let p = protector ()
    let grants = [ grant owner link (now.ToUnixTimeSeconds()) ]
    Assert.Equal<Grant list>(grants, unprotectGrants p now (protectGrants p grants))

[<Fact>]
let ``21 grants keep the newest 20`` () =
    let p = protector ()
    let start = now.ToUnixTimeSeconds()
    let users = [ for i in 0..20 -> Guid.NewGuid(), start + int64 i ]

    let grants =
        users
        |> List.fold (fun acc (user, at) -> addGrant acc (grant user (Guid.NewGuid()) at)) []

    Assert.Equal(maxGrants, grants.Length)
    // the oldest (the first user) was dropped, the newest stays
    Assert.DoesNotContain(grants, fun g -> g.UserId = fst users[0])
    Assert.Contains(grants, fun g -> g.UserId = fst users[20])

    // and the cookie holds exactly those
    let later = now.AddSeconds 60.0
    Assert.Equal<Grant list>(grants, unprotectGrants p later (protectGrants p grants))

[<Fact>]
let ``a newer grant for the same user replaces the old one`` () =
    let second = Guid.NewGuid()
    let grants = addGrant [ grant owner link 1L ] (grant owner second 2L)
    Assert.Equal<Grant list>([ grant owner second 2L ], grants)

[<Fact>]
let ``a tampered or foreign cookie is no grants`` () =
    let p = protector ()
    let value = protectGrants p [ grant owner link (now.ToUnixTimeSeconds()) ]
    Assert.Empty(unprotectGrants p now (value.Substring(0, value.Length - 4) + "AAAA"))
    Assert.Empty(unprotectGrants p now "not a protected value")
    // protected by another instance's keys
    Assert.Empty(unprotectGrants (protector ()) now value)
    // protected, but not the expected shape
    Assert.Empty(unprotectGrants p now (p.Protect "{\"u\":1}"))

[<Fact>]
let ``grants older than 30 days are dropped`` () =
    let p = protector ()
    let old = (now - TimeSpan.FromDays 31.0).ToUnixTimeSeconds()
    Assert.Empty(unprotectGrants p now (protectGrants p [ grant owner link old ]))

// ---------------------------------------------------------------------------
// The share token
// ---------------------------------------------------------------------------

[<Fact>]
let ``a share token has the frs_ shape and a lowercase hex SHA-256 hash`` () =
    let token = ShareToken.generate ()
    Assert.StartsWith("frs_", token)
    Assert.True(ShareToken.isWellFormed token)
    Assert.Matches("^[0-9a-f]{64}$", ShareToken.hash token)
    Assert.False((token = ShareToken.generate ()))

[<Theory>]
[<InlineData("")>]
[<InlineData("frs_short")>]
[<InlineData("frk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")>]
[<InlineData("frs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")>]
let ``malformed tokens are not well formed`` (token: string) =
    Assert.False(ShareToken.isWellFormed token)
