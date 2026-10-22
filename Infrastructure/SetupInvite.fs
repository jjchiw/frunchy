/// Feature 023: the setup invite. While there are no accounts at all, every start of the web app prints a one-time
/// invite in the console (like Umbral's setup token), so the owner of a fresh deploy can open the logs, sign in and
/// enter it: the account it creates is an operator (see `Handlers/Auth/SignUp.fs`).
///
/// The code appears only in the banner handed to `write` (the caller passes `Console.WriteLine`): it never goes to
/// a logger, a security event or the database, which keeps only its hash. One advisory lock makes the check and the
/// mint one step, so two copies starting together never print two competing codes.
module FrunchyBackend.Infrastructure.SetupInvite

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

/// Any number, the same for every copy of Frunchy (the schema lock is 7_021_014): the lock that makes two copies
/// starting together take turns
[<Literal>]
let private LockKey = 7_023_001L

/// How long an invite a copy minted keeps other copies from minting another
let recentMinutes = 2

/// How many days a setup invite lasts
let days = 7

type Outcome =
    /// `Frunchy:SetupInvite:Enabled` is false: nothing is read, printed or created
    | Disabled
    /// There is at least one account, so nothing is printed and no invite is created
    | HasUsers
    /// A new setup invite was made (every older unused one is revoked) and its banner written
    | Minted of code: string
    /// Another copy minted one less than `recentMinutes` ago: nothing is minted, a short banner is written
    | RecentElsewhere of mintedAt: DateTime

let private line = String.replicate 64 "="

/// The loud banner with the code. `origin` is the public origin without a trailing slash, when known.
let banner (origin: string option) (code: string) : string =
    let signIn =
        match origin with
        | Some o -> $"  Sign in   {o.TrimEnd('/')}/signin"
        | None -> "  Sign in   /signin on this site"

    String.concat
        Environment.NewLine
        [ ""
          line
          "  Frunchy has no accounts yet."
          ""
          signIn
          $"  Invite    {code}"
          ""
          "  The invite works once and makes its account an operator."
          "  A restart prints a new invite and cancels this one."
          line
          "" ]

/// The short banner of a copy that found a recent invite: no code, only where to look
let anotherCopyBanner (mintedAt: DateTime) : string =
    let at = mintedAt.ToUniversalTime().ToString("HH:mm:ss", Globalization.CultureInfo.InvariantCulture)

    String.concat
        Environment.NewLine
        [ ""
          line
          "  Frunchy has no accounts yet."
          ""
          $"  Another copy printed the setup invite at {at} UTC."
          "  Look for it in that copy's log."
          line
          "" ]

/// While `users` is empty (and `enabled`): revoke the unused setup invites and mint and print a new one, unless
/// another copy minted one less than two minutes ago. All of it is one transaction under one advisory lock.
/// The window is measured on the database's clock, which every copy shares.
let ensure (db: DbConfig) (enabled: bool) (origin: string option) (write: string -> unit) : Task<Outcome> =
    task {
        if not enabled then
            return Disabled
        else
            use connection = getConnection db
            do! connection.OpenAsync()
            use tx = connection.BeginTransaction()

            use lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, tx)
            lock.Parameters.AddWithValue("key", LockKey) |> ignore
            let! _ = lock.ExecuteNonQueryAsync()

            use anyUser = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", connection, tx)
            let! hasUsers = anyUser.ExecuteScalarAsync()

            if unbox<bool> hasUsers then
                do! tx.RollbackAsync()
                return HasUsers
            else
                use recent =
                    new NpgsqlCommand(
                        """
                        SELECT max(created_at) FROM invites
                        WHERE is_setup AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
                          AND created_at > now() - make_interval(mins => @minutes)
                        """,
                        connection,
                        tx
                    )

                recent.Parameters.AddWithValue("minutes", recentMinutes) |> ignore

                match! recent.ExecuteScalarAsync() with
                | :? DateTime as mintedAt ->
                    do! tx.RollbackAsync()
                    write (anotherCopyBanner mintedAt)
                    return RecentElsewhere mintedAt
                | _ ->
                    use revoke =
                        new NpgsqlCommand(
                            "UPDATE invites SET revoked_at = now() WHERE is_setup AND used_at IS NULL AND revoked_at IS NULL",
                            connection,
                            tx
                        )

                    let! _ = revoke.ExecuteNonQueryAsync()

                    let code = InviteCode.generate ()

                    use insert =
                        new NpgsqlCommand(
                            "INSERT INTO invites (id, code_hash, expires_at, is_setup) VALUES (@id, @hash, now() + make_interval(days => @days), true)",
                            connection,
                            tx
                        )

                    insert.Parameters.AddWithValue("id", Guid.NewGuid()) |> ignore
                    insert.Parameters.AddWithValue("hash", InviteCode.hash code) |> ignore
                    insert.Parameters.AddWithValue("days", days) |> ignore
                    let! _ = insert.ExecuteNonQueryAsync()

                    do! tx.CommitAsync()
                    write (banner origin code)
                    return Minted code
    }
