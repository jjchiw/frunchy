module FrunchyBackend.Tests.TestHelpers

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Operations
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// Create a user and return its sync key
let createUser (db: DbConfig) (username: string) : Task<string> =
    task {
        match! CreateUser.createUser db username (username.ToUpperInvariant()) with
        | Ok created -> return created.SyncKey
        | Error e -> return failwith $"createUser failed: {e}"
    }

let readJson (response: HttpResponseMessage) : Task<JsonElement> =
    task {
        let! body = response.Content.ReadAsStringAsync()
        use doc = JsonDocument.Parse body
        return doc.RootElement.Clone()
    }

/// POST a submission and return (status code, JSON body)
let submit (app: TestApp) (key: string) (json: string) : Task<int * JsonElement> =
    task {
        let! response = postImport app.Client (Some key) json
        let! body = readJson response
        return int response.StatusCode, body
    }

/// POST and assert 202; returns the import id
let submitAccepted (app: TestApp) (key: string) (json: string) : Task<Guid> =
    task {
        let! status, body = submit app key json

        if status <> 202 then
            failwith $"expected 202, got {status}: {body}"

        return body.GetProperty("importId").GetGuid()
    }

/// Run the worker until nothing is pending
let processAll (db: DbConfig) : Task<int> =
    task {
        let mutable processed = 0
        let mutable more = true

        while more do
            let! worked = FrunchyBackend.Workers.ImportProcessor.processNext db
            more <- worked
            if worked then processed <- processed + 1

        return processed
    }

/// Run the fediverse outbox worker (feature 015) as of `now` until nothing is due; returns how many items it processed.
/// The test app's worker stays off, so tests drive it here against `app.Megafono`.
let drainOutboxAt (app: TestApp) (now: DateTimeOffset) : Task<int> =
    task {
        let services = app.Services
        let client = services.GetRequiredService<IHttpClientFactory>().CreateClient "megafono"

        let values =
            match services.GetRequiredService<FrunchyBackend.Infrastructure.Federation.FederationSettings>() with
            | FrunchyBackend.Infrastructure.Federation.Enabled values -> values
            | FrunchyBackend.Infrastructure.Federation.Disabled -> failwith "federation is off in this app"

        let mutable processed = 0
        let mutable more = true

        while more && processed < 1000 do
            let! worked = FrunchyBackend.Workers.FediverseOutbox.processNext app.Db client values now
            more <- worked
            if worked then processed <- processed + 1

        return processed
    }

/// `drainOutboxAt` at the present, with a few seconds of margin for the database's clock
let drainOutbox (app: TestApp) : Task<int> =
    drainOutboxAt app (DateTimeOffset.UtcNow.AddSeconds 5.0)

/// Rows that must stay untouched by a rejected submission
let snapshot (db: DbConfig) : Task<int64 * int64 * string list> =
    task {
        let! imports = Db.count db "cr_imports"
        let! pages = Db.count db "cr_import_pages"
        let! bindings = Db.dump db "SELECT username, coalesce(cr_account_id,'-'), coalesce(cr_profile_id,'-') FROM users ORDER BY username"
        return imports, pages, bindings
    }

/// watch_events as comparable text, without the bookkeeping columns (import_id, updated_at)
let watchEvents (db: DbConfig) =
    Db.dump db "SELECT u.username, w.cr_episode_id, w.watched_at, w.playhead_s, w.fully_watched FROM watch_events w JOIN users u ON u.id = w.user_id ORDER BY 1, 2"

let episodesTable (db: DbConfig) =
    Db.dump db "SELECT id, series_id, coalesce(season_id,'-'), coalesce(series_title,'-'), coalesce(title,'-'), coalesce(episode_number::text,'-'), coalesce(duration_ms::text,'-'), coalesce(thumbnail_url,'-'), coalesce(aired_at::text,'-') FROM cr_episodes ORDER BY id"
