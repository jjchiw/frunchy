module FrunchyBackend.Tests.Infrastructure.PostgresFixture

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Npgsql
open Testcontainers.PostgreSql
open FrunchyBackend
open FrunchyBackend.Infrastructure.Database

/// One PostgreSQL container for the whole test run (Testcontainers' reaper removes it afterwards)
let private container =
    lazy
        (task {
            // Every test gets its own database (and its own connection pool), so allow plenty of clients
            let c = PostgreSqlBuilder("postgres:16-alpine").WithCommand("-c", "max_connections=500").Build()
            do! c.StartAsync()
            return c
        })

/// A fresh, empty database with the schema bootstrapped; tests never share data
let newDatabase () : Task<DbConfig> =
    task {
        let! c = container.Force()
        let name = "t_" + Guid.NewGuid().ToString("N")
        use admin = new NpgsqlConnection(c.GetConnectionString())
        do! admin.OpenAsync()
        use create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)
        let! _ = create.ExecuteNonQueryAsync()
        let csb =
            NpgsqlConnectionStringBuilder(c.GetConnectionString(), Database = name, MaxPoolSize = 10, ConnectionIdleLifetime = 10, ConnectionPruningInterval = 5)

        let db = { connectionString = csb.ConnectionString }
        do! initializeSchema db
        return db
    }

/// A fresh database without the schema (for bootstrap tests)
let newEmptyDatabase () : Task<DbConfig> =
    task {
        let! c = container.Force()
        let name = "t_" + Guid.NewGuid().ToString("N")
        use admin = new NpgsqlConnection(c.GetConnectionString())
        do! admin.OpenAsync()
        use create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)
        let! _ = create.ExecuteNonQueryAsync()
        let csb =
            NpgsqlConnectionStringBuilder(c.GetConnectionString(), Database = name, MaxPoolSize = 10, ConnectionIdleLifetime = 10, ConnectionPruningInterval = 5)

        return { connectionString = csb.ConnectionString }
    }

/// The real app (Program.buildApp) on an in-process TestServer, background worker disabled,
/// with the fake `test` sign-in provider registered (feature 002)
type TestApp(app: WebApplication, client: HttpClient, db: DbConfig, jikan: FakeJikan.FakeJikanHandler) =
    member _.Client = client
    member _.Db = db
    member _.Services = app.Services

    /// The stand-in for Jikan this app talks to (feature 003); nothing is routed until a test says so
    member _.Jikan = jikan

    /// A fresh browser (own cookie jar) speaking the given language
    member _.NewBrowser(?language: string) =
        new Browser.Browser(app.GetTestServer().CreateHandler(), defaultArg language "en")

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            client.Dispose()
            app.DisposeAsync()

/// The app with extra configuration and an extra builder customization (e.g. a test clock)
let createAppWith (db: DbConfig) (extraConfig: (string * string) list) (customize: WebApplicationBuilder -> unit) : Task<TestApp> =
    task {
        let jikan = new FakeJikan.FakeJikanHandler()

        let app =
            Program.buildApp Array.empty (fun builder ->
                builder.WebHost.UseTestServer() |> ignore

                builder.Configuration.AddInMemoryCollection(
                    dict (
                        [ "ConnectionStrings:Database", db.connectionString
                          Program.WorkerEnabledKey, "false"
                          Program.EnrichmentWorkerEnabledKey, "false" ]
                        @ FakeJikan.fastSettings
                        @ extraConfig
                    )
                )
                |> ignore

                FakeJikan.useHandler jikan builder.Services
                FakeProvider.register builder
                customize builder)

        do! app.StartAsync()
        return new TestApp(app, app.GetTestClient(), db, jikan)
    }

let createApp (db: DbConfig) : Task<TestApp> = createAppWith db [] ignore

/// Fresh database + app in one call
let newApp () : Task<TestApp> =
    task {
        let! db = newDatabase ()
        return! createApp db
    }

module Db =
    let scalar<'T> (db: DbConfig) (sql: string) (parameters: (string * obj) list) : Task<'T> =
        task {
            use connection = getConnection db
            do! connection.OpenAsync()
            use cmd = new NpgsqlCommand(sql, connection)

            for (name, value) in parameters do
                cmd.Parameters.AddWithValue(name, value) |> ignore

            let! result = cmd.ExecuteScalarAsync()
            return unbox<'T> result
        }

    let execute (db: DbConfig) (sql: string) : Task<unit> =
        task {
            use connection = getConnection db
            do! connection.OpenAsync()
            use cmd = new NpgsqlCommand(sql, connection)
            let! _ = cmd.ExecuteNonQueryAsync()
            ()
        }

    /// Row count of a table
    let count (db: DbConfig) (table: string) : Task<int64> = scalar<int64> db $"SELECT count(*) FROM {table}" []

    /// Rows of a query rendered as text lines, for whole-table equality checks
    let dump (db: DbConfig) (sql: string) : Task<string list> =
        task {
            use connection = getConnection db
            do! connection.OpenAsync()
            use cmd = new NpgsqlCommand(sql, connection)
            use! reader = cmd.ExecuteReaderAsync()
            let rows = List<string>()

            while reader.Read() do
                rows.Add(String.Join("|", [ for i in 0 .. reader.FieldCount - 1 -> string (reader.GetValue i) ]))

            return List.ofSeq rows
        }
