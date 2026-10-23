/// The start waits for the database instead of exiting when it is not ready yet (Caravana starts the app and its
/// Postgres together). `Frunchy:Database:StartupWaitSeconds`, default 60, 0 = fail at once.
module FrunchyBackend.Tests.StartupWaitTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Net
open System.Net.Sockets
open Microsoft.Extensions.Configuration
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// A connection string to a local port nothing listens on (connection refused at once)
let private closedPortDb () : DbConfig =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()

    { connectionString = $"Host=127.0.0.1;Port={port};Database=nope;Username=u;Password=secret-pw;Pooling=false" }

[<Fact>]
let ``a reachable database returns at once without logging`` () =
    task {
        let! db = newDatabase ()
        let log = ResizeArray<string>()
        let clock = Stopwatch.StartNew()
        do! waitForDatabase db (TimeSpan.FromSeconds 30.0) log.Add
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds 5.0)
        Assert.Empty log
    }

[<Fact>]
let ``an unreachable database is retried until the limit, then throws`` () =
    task {
        let db = closedPortDb ()
        let log = ResizeArray<string>()
        let clock = Stopwatch.StartNew()

        let! ex =
            Assert.ThrowsAnyAsync<Exception>(fun () -> waitForDatabase db (TimeSpan.FromSeconds 3.0) log.Add :> System.Threading.Tasks.Task)

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds 3.0, $"gave up after {clock.Elapsed}")
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds 6.0, $"waited {clock.Elapsed}")

        // attempts at 0 s and 1 s wait; the one at 3 s is the last and throws
        Assert.Equal(2, log.Count)
        Assert.StartsWith("Waiting for the database (1, 0s): ", log[0])
        Assert.StartsWith("Waiting for the database (2, 1s): ", log[1])

        // the connection string, password included, never reaches the log or the final error
        Assert.DoesNotContain("secret-pw", String.Join("\n", log))
        Assert.DoesNotContain("secret-pw", string ex)
        Assert.Contains("did not answer within 3s", ex.Message)
    }

[<Fact>]
let ``a limit of zero fails at once`` () =
    task {
        let db = closedPortDb ()
        let log = ResizeArray<string>()
        let clock = Stopwatch.StartNew()

        let! _ =
            Assert.ThrowsAnyAsync<Exception>(fun () -> waitForDatabase db TimeSpan.Zero log.Add :> System.Threading.Tasks.Task)

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds 2.0)
        Assert.Empty log
    }

let private configOf (value: string option) : IConfiguration =
    let pairs =
        match value with
        | Some v -> [ KeyValuePair<string, string | null>(StartupWaitKey, v) ]
        | None -> []

    ConfigurationBuilder().AddInMemoryCollection(pairs).Build()

[<Fact>]
let ``the setting defaults to 60 seconds and accepts 0`` () =
    Assert.Equal(TimeSpan.FromSeconds 60.0, startupWait (configOf None))
    Assert.Equal(TimeSpan.FromSeconds 60.0, startupWait (configOf (Some "  ")))
    Assert.Equal(TimeSpan.Zero, startupWait (configOf (Some "0")))
    Assert.Equal(TimeSpan.FromSeconds 5.0, startupWait (configOf (Some " 5 ")))

[<Theory>]
[<InlineData("abc")>]
[<InlineData("-1")>]
[<InlineData("1.5")>]
[<InlineData("99999")>]
let ``an invalid setting is refused naming the key`` (value: string) =
    let ex = Assert.Throws<InvalidOperationException>(fun () -> startupWait (configOf (Some value)) |> ignore)
    Assert.Contains(StartupWaitKey, ex.Message)
