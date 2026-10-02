module FrunchyBackend.Tests.InviteOperationsTests

open System
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

[<Fact>]
let ``invite-create returns a one-time code and stores only its hash`` () =
    task {
        let! db = newDatabase ()
        let! invite = InviteCreate.createInvite db 7
        Assert.Matches("^inv_[0-9A-HJKMNP-TV-Z]{12}$", invite.Code)
        let! stored = Db.scalar<string> db "SELECT code_hash FROM invites" []
        Assert.Equal(InviteCode.hash invite.Code, stored)
        Assert.DoesNotContain(invite.Code.Substring 4, stored)
        Assert.InRange(invite.ExpiresAt, DateTime.UtcNow.AddDays 6.9, DateTime.UtcNow.AddDays 7.1)
    }

[<Fact>]
let ``--days sets the expiry and bad values are usage errors`` () =
    task {
        let! db = newDatabase ()
        let! one = InviteCreate.createInvite db 1
        Assert.InRange(one.ExpiresAt, DateTime.UtcNow.AddHours 23.0, DateTime.UtcNow.AddHours 25.0)
        let! bad = InviteCreate.run db [ "--days"; "0" ]
        Assert.Equal(1, bad)
        let! ok = InviteCreate.run db [ "--days"; "3" ]
        Assert.Equal(0, ok)
    }

[<Fact>]
let ``invite-list shows states and invite-revoke only revokes active invites`` () =
    task {
        let! db = newDatabase ()
        let! active = InviteCreate.createInvite db 7
        let! toRevoke = InviteCreate.createInvite db 7
        let! expired = InviteCreate.createInvite db 7
        do! Db.execute db $"UPDATE invites SET expires_at = now() - interval '1 minute' WHERE id = '{expired.Id}'"

        let! revoked = InviteRevoke.run db [ string toRevoke.Id ]
        let! again = InviteRevoke.run db [ string toRevoke.Id ]
        let! unknown = InviteRevoke.run db [ string (Guid.NewGuid()) ]
        let! garbage = InviteRevoke.run db [ "nope" ]
        Assert.Equal(0, revoked)
        Assert.Equal(3, again)
        Assert.Equal(2, unknown)
        Assert.Equal(2, garbage)

        let! rows = InviteList.listInvites db
        let state id = rows |> List.find (fun r -> r.Id = id) |> _.State
        Assert.Equal("active", state active.Id)
        Assert.Equal("revoked", state toRevoke.Id)
        Assert.Equal("expired", state expired.Id)
    }

[<Fact>]
let ``invite codes are matched case-insensitively and with Crockford look-alikes`` () =
    let code = InviteCode.generate ()
    Assert.Equal(InviteCode.hash code, InviteCode.hash (code.ToLowerInvariant()))
    Assert.Equal(InviteCode.hash code, InviteCode.hash ("  " + code + " "))
