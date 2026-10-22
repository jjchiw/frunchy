/// Feature 023: the setup invite. While `users` is empty, `SetupInvite.ensure` (called at web start) mints a
/// one-time invite, stores only its hash and hands the banner to the writer it is given (the console in the real
/// app); the account that signs up with it becomes an operator in the same transaction.
module FrunchyBackend.Tests.SetupInviteTests

open System
open System.Collections.Concurrent
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Logging
open Xunit
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// What the console would show: every string the banner writer received
type private Console() =
    let lines = ConcurrentQueue<string>()
    member _.Write(text: string) = lines.Enqueue text
    member _.All = lines |> Seq.toList
    member _.Text = String.Join("\n", lines)

/// Every message the app's logger receives
type private CapturedLogs() =
    let lines = ConcurrentQueue<string>()
    member _.Text = String.Join("\n", lines)

    interface ILoggerProvider with
        member _.CreateLogger(_: string) =
            { new ILogger with
                member _.BeginScope<'S>(_: 'S) = Unchecked.defaultof<IDisposable>
                member _.IsEnabled(_: LogLevel) = true

                member _.Log<'S>(_: LogLevel, _: EventId, state: 'S, ex: exn, formatter: Func<'S, exn, string>) =
                    lines.Enqueue(formatter.Invoke(state, ex)) }

        member _.Dispose() = ()

let private origin = Some "https://frunchy.example"

let private ensureOn (db: DbConfig) (console: Console) = SetupInvite.ensure db true origin console.Write

let private isMinted (o: SetupInvite.Outcome) = match o with SetupInvite.Minted _ -> true | _ -> false
let private isRecent (o: SetupInvite.Outcome) = match o with SetupInvite.RecentElsewhere _ -> true | _ -> false

let private codeOf (outcome: SetupInvite.Outcome) =
    match outcome with
    | SetupInvite.Minted code -> code
    | other -> failwith $"expected a minted invite, got {other}"

/// The rows of the setup invites, oldest first, as text: state | is_setup | days to expiry
let private setupRows (db: DbConfig) =
    Db.dump
        db
        """SELECT CASE WHEN used_at IS NOT NULL THEN 'used' WHEN revoked_at IS NOT NULL THEN 'revoked' ELSE 'active' END
                  || '|' || is_setup || '|' || round(extract(epoch FROM expires_at - created_at) / 86400)
           FROM invites WHERE is_setup ORDER BY created_at, id"""

let private activeSetupCount (db: DbConfig) =
    Db.scalar<int64> db "SELECT count(*) FROM invites WHERE is_setup AND used_at IS NULL AND revoked_at IS NULL" []

let private userCount (db: DbConfig) = Db.count db "users"

let private asRecent (db: DbConfig) (minutes: int) =
    Db.execute db $"UPDATE invites SET created_at = now() - interval '{minutes} minutes' WHERE is_setup"

/// An app on the given (empty) database, with every log line captured
let private appOn (db: DbConfig) (logs: CapturedLogs) =
    createAppWith db [] (fun (b: WebApplicationBuilder) -> b.Logging.AddProvider logs |> ignore)

let private body (response: Net.Http.HttpResponseMessage) =
    task {
        let! raw = response.Content.ReadAsStringAsync()
        return WebUtility.HtmlDecode raw
    }

// ---------------------------------------------------------------------------
// Scenario 1: an empty database
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty database mints one setup invite and prints the banner with its code`` () =
    task {
        let! db = newDatabase ()
        let console = Console()
        let! outcome = ensureOn db console
        let code = codeOf outcome

        Assert.StartsWith("inv_", code)
        Assert.Single(console.All) |> ignore

        // the exact banner (lines joined with the platform's newline)
        let expected =
            String.concat
                Environment.NewLine
                [ ""
                  String.replicate 64 "="
                  "  Frunchy has no accounts yet."
                  ""
                  "  Sign in   https://frunchy.example/signin"
                  $"  Invite    {code}"
                  ""
                  "  The invite works once and makes its account an operator."
                  "  A restart prints a new invite and cancels this one."
                  String.replicate 64 "="
                  "" ]

        Assert.Equal(expected, console.Text)

        // one unused, unrevoked row, seven days, setup, and only the hash
        let! rows = setupRows db
        Assert.Equal<string list>([ "active|true|7" ], rows)
        let! all = Db.count db "invites"
        Assert.Equal(1L, all)
        let! stored = Db.dump db "SELECT i::text FROM invites i"
        Assert.DoesNotContain(code, String.Join("\n", stored))
        let! storedHash = Db.scalar<string> db "SELECT code_hash FROM invites" []
        Assert.Equal(InviteCode.hash code, storedHash)
    }

[<Fact>]
let ``the banner without a known origin says the sign-in page is on this site`` () =
    task {
        let! db = newDatabase ()
        let console = Console()
        let! outcome = SetupInvite.ensure db true None console.Write
        let code = codeOf outcome
        Assert.Contains("  Sign in   /signin on this site", console.Text)
        Assert.Contains($"  Invite    {code}", console.Text)
    }

[<Fact>]
let ``the origin's trailing slash is not doubled`` () =
    task {
        let! db = newDatabase ()
        let console = Console()
        let! _ = SetupInvite.ensure db true (Some "https://frunchy.example/") console.Write
        Assert.Contains("  Sign in   https://frunchy.example/signin", console.Text)
    }

// ---------------------------------------------------------------------------
// Scenarios 3 and 6: nothing to do
// ---------------------------------------------------------------------------

[<Fact>]
let ``with an account nothing is printed and no invite is created`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! _ = signedUp app "sub-first" "first"
        do! Db.execute db "DELETE FROM invites"

        let console = Console()
        let! outcome = ensureOn db console

        Assert.Equal(SetupInvite.HasUsers 1L, outcome) // the count is what the start logs
        Assert.Empty console.All
        let! invites = Db.count db "invites"
        Assert.Equal(0L, invites)
    }

[<Fact>]
let ``when the setting is off nothing is printed or created on an empty database`` () =
    task {
        let! db = newDatabase ()
        let console = Console()
        let! outcome = SetupInvite.ensure db false origin console.Write

        Assert.Equal(SetupInvite.Disabled, outcome)
        Assert.Empty console.All
        let! invites = Db.count db "invites"
        Assert.Equal(0L, invites)
    }

// ---------------------------------------------------------------------------
// Scenarios 4 and 5: a restart and another copy
// ---------------------------------------------------------------------------

[<Fact>]
let ``a restart revokes the earlier invite, whose code is then refused, and mints a new one`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let console = Console()
        let! first = ensureOn db console
        let oldCode = codeOf first

        // the restart comes later than the two-minute window
        do! asRecent db 3
        let! second = ensureOn db console
        let newCode = codeOf second
        Assert.NotEqual<string>(oldCode, newCode)

        let! rows = setupRows db
        Assert.Equal<string list>([ "revoked|true|7"; "active|true|7" ], rows)
        let! active = activeSetupCount db
        Assert.Equal(1L, active)

        // the old code is refused at sign-up, no account is made
        let browser = app.NewBrowser()
        let! refused = submitSignUp browser "sub-old" oldCode "olduser" "Old"
        let! refusedHtml = body refused
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode)
        Assert.Contains("aria-invalid", refusedHtml)
        let! users = userCount db
        Assert.Equal(0L, users)

        // the new one works
        let other = app.NewBrowser()
        let! accepted = submitSignUp other "sub-new" newCode "newuser" "New"
        Assert.Equal(HttpStatusCode.Found, accepted.StatusCode)
        let! users = userCount db
        Assert.Equal(1L, users)
    }

[<Fact>]
let ``within two minutes another copy mints nothing and prints the short banner`` () =
    task {
        let! db = newDatabase ()
        let console = Console()
        let! first = ensureOn db console
        let code = codeOf first

        // 1 minute old: inside the window
        do! asRecent db 1
        let other = Console()
        let! outcome = ensureOn db other

        match outcome with
        | SetupInvite.RecentElsewhere mintedAt ->
            Assert.InRange(DateTime.UtcNow - mintedAt.ToUniversalTime(), TimeSpan.FromSeconds 30.0, TimeSpan.FromMinutes 2.0)
        | unexpected -> failwith $"expected RecentElsewhere, got {unexpected}"

        // nothing new: the same single active row, and the code did not leak into the second banner
        let! rows = setupRows db
        Assert.Equal<string list>([ "active|true|7" ], rows)
        Assert.Single(other.All) |> ignore
        Assert.Contains("Frunchy has no accounts yet.", other.Text)
        Assert.Contains("Another copy printed the setup invite at", other.Text)
        Assert.DoesNotContain("inv_", other.Text)
        Assert.DoesNotContain(code, other.Text)

        // the first code still works: the second copy did not revoke it
        let! app = createApp db
        let! accepted = submitSignUp (app.NewBrowser()) "sub-first" code "first" "First"
        Assert.Equal(HttpStatusCode.Found, accepted.StatusCode)
    }

[<Fact>]
let ``the two-minute window is measured on the invite's age: 119 seconds waits, 121 seconds mints`` () =
    task {
        let! db = newDatabase ()
        let! _ = ensureOn db (Console())

        do! Db.execute db "UPDATE invites SET created_at = now() - interval '119 seconds' WHERE is_setup"
        let! inside = ensureOn db (Console())
        Assert.True(isRecent inside, $"119 s: {inside}")

        do! Db.execute db "UPDATE invites SET created_at = now() - interval '121 seconds' WHERE is_setup"
        let! outside = ensureOn db (Console())
        Assert.True(isMinted outside, $"121 s: {outside}")

        let! active = activeSetupCount db
        Assert.Equal(1L, active)
    }

[<Fact>]
let ``a revoked setup invite does not hold the window, and a used one ends the story`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! first = ensureOn db (Console())
        let code = codeOf first

        // a revoked invite (by hand) is not "another copy's invite"
        do! Db.execute db "UPDATE invites SET revoked_at = now() WHERE is_setup"
        let! afterRevoke = ensureOn db (Console())
        Assert.True(isMinted afterRevoke)
        ignore code

        // an invite used by an account ends the story: there is a user now
        let! used = submitSignUp (app.NewBrowser()) "sub-x" (codeOf afterRevoke) "xuser" "X"
        Assert.Equal(HttpStatusCode.Found, used.StatusCode)
        let! afterUse = ensureOn db (Console())
        Assert.Equal(SetupInvite.HasUsers 1L, afterUse)
    }

[<Fact>]
let ``concurrent copies leave exactly one active setup invite`` () =
    task {
        let! db = newDatabase ()
        let consoles = [| for _ in 1..4 -> Console() |]
        let! outcomes = Task.WhenAll [| for c in consoles -> ensureOn db c |]

        let minted = outcomes |> Array.filter isMinted
        let waited = outcomes |> Array.filter isRecent
        Assert.Single minted |> ignore
        Assert.Equal(3, waited.Length)

        let! active = activeSetupCount db
        Assert.Equal(1L, active)
        let! all = Db.count db "invites"
        Assert.Equal(1L, all)

        // exactly one console shows a code, and it is the active one
        let withCode = consoles |> Array.filter (fun c -> c.Text.Contains "inv_")
        Assert.Single withCode |> ignore
        Assert.Contains(codeOf minted[0], withCode[0].Text)
    }

// ---------------------------------------------------------------------------
// Scenarios 2 and 7: the account the setup invite makes
// ---------------------------------------------------------------------------

[<Fact>]
let ``signing up with the setup invite makes the account an operator, with the event, and /review/invites opens`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! outcome = ensureOn db (Console())
        let code = codeOf outcome

        let browser = app.NewBrowser()
        let! response = submitSignUp browser "sub-first" code "first" "First"
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        // the invite is used by the account
        let! rows = setupRows db
        Assert.Equal<string list>([ "used|true|7" ], rows)
        let! usedBy = Db.scalar<string> db "SELECT u.username FROM invites i JOIN users u ON u.id = i.used_by_user_id" []
        Assert.Equal("first", usedBy)

        // an operators row with no granting root, and the event as the operator's doing (operator-add's shape)
        let! operators = Db.dump db "SELECT u.username || '|' || coalesce(o.added_by::text, '<none>') FROM operators o JOIN users u ON u.id = o.user_id"
        Assert.Equal<string list>([ "first|<none>" ], operators)
        let! events = Db.dump db "SELECT event || '|' || actor FROM security_events ORDER BY id"
        Assert.Equal<string list>([ "account_created|user"; "operator_added|operator" ], events)

        // the account page offers the operator links and the invites page opens
        let! account = browser.Get "/account"
        Assert.Contains("data-invites-link", account.Html)
        let! invites = browser.GetRaw "/review/invites"
        Assert.Equal(HttpStatusCode.OK, invites.StatusCode)
    }

[<Fact>]
let ``a failed sign-up with the setup invite leaves it usable and makes no operator`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! outcome = ensureOn db (Console())
        let code = codeOf outcome

        // a username that is not valid: the account is not created, so the invite is not consumed
        let! refused = submitSignUp (app.NewBrowser()) "sub-bad" code "Not Valid!" "Bad"
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode)
        let! rows = setupRows db
        Assert.Equal<string list>([ "active|true|7" ], rows)
        let! operators = Db.count db "operators"
        Assert.Equal(0L, operators)
        let! users = userCount db
        Assert.Equal(0L, users)
    }

[<Fact>]
let ``a normal invite grants no operator rights`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! outcome = ensureOn db (Console())
        let! _ = submitSignUp (app.NewBrowser()) "sub-first" (codeOf outcome) "first" "First"

        // the second account comes in with an ordinary invite
        let! second = signedUp app "sub-second" "second"
        let! operators = Db.dump db "SELECT u.username FROM operators o JOIN users u ON u.id = o.user_id"
        Assert.Equal<string list>([ "first" ], operators)

        let! account = second.Get "/account"
        Assert.DoesNotContain("data-invites-link", account.Html)
        let! invites = second.GetRaw "/review/invites"
        Assert.Equal(HttpStatusCode.NotFound, invites.StatusCode)
        let! events = Db.dump db "SELECT u.username || '|' || e.event FROM security_events e JOIN users u ON u.id = e.user_id WHERE u.username = 'second'"
        Assert.Equal<string list>([ "second|account_created" ], events)
    }

[<Fact>]
let ``an invite made with invite-create is never a setup invite`` () =
    task {
        let! db = newDatabase ()
        let! app = createApp db
        let! _ = createInvite app
        let! rows = setupRows db
        Assert.Empty rows
        let! flags = Db.dump db "SELECT is_setup FROM invites"
        Assert.Equal<string list>([ "False" ], flags)
    }

// ---------------------------------------------------------------------------
// FR-002: the code never reaches the logger, an event or the database
// ---------------------------------------------------------------------------

[<Fact>]
let ``the code is only in what the writer receives, not in logs, events or the database`` () =
    task {
        let! db = newDatabase ()
        let logs = new CapturedLogs()
        let! app = appOn db logs
        let console = Console()
        let! outcome = ensureOn db console
        let code = codeOf outcome

        let! response = submitSignUp (app.NewBrowser()) "sub-first" code "first" "First"
        Assert.Equal(HttpStatusCode.Found, response.StatusCode)

        Assert.Contains(code, console.Text)
        Assert.DoesNotContain(code, logs.Text)

        for table in [ "invites"; "security_events"; "users"; "user_identities"; "operators" ] do
            let! dump = Db.dump db $"SELECT t::text FROM {table} t"
            Assert.DoesNotContain(code, String.Join("\n", dump))
    }

[<Fact>]
let ``ensure takes a writer and no logger, so the code has no other way out`` () =
    let moduleType = typeof<SetupInvite.Outcome>.DeclaringType
    let parameters = moduleType.GetMethod("ensure").GetParameters() |> Array.map (fun p -> p.ParameterType)

    Assert.DoesNotContain(parameters, fun t -> typeof<ILogger>.IsAssignableFrom t || typeof<ILoggerFactory>.IsAssignableFrom t)
    Assert.Contains(parameters, fun t -> t = typeof<FSharp.Core.FSharpFunc<string, unit>>)
