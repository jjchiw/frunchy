/// Account security events (feature 002, FR-022): recorded inside the caller's transaction
module FrunchyBackend.Infrastructure.SecurityEvents

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type SecurityEvent =
    { OccurredAt: DateTime
      Event: string
      Provider: string option
      Actor: string }

let record
    (connection: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    (userId: Guid)
    (event: SecurityEventType)
    (provider: string option)
    (actor: Actor)
    : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand(
                "INSERT INTO security_events (user_id, event, provider, actor) VALUES (@user_id, @event, @provider, @actor)",
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.AddWithValue("event", SecurityEventType.toDb event) |> ignore
        cmd.Parameters.Add(NpgsqlParameter("provider", NpgsqlDbType.Text, Value = (provider |> Option.map box |> Option.defaultValue (box DBNull.Value)))) |> ignore
        cmd.Parameters.AddWithValue("actor", Actor.toDb actor) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// Newest first
let recent (db: DbConfig) (userId: Guid) (limit: int) : Task<SecurityEvent list> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                "SELECT occurred_at, event, provider, actor FROM security_events WHERE user_id = @user_id ORDER BY occurred_at DESC, id DESC LIMIT @limit",
                connection
            )

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        cmd.Parameters.AddWithValue("limit", limit) |> ignore
        use! r = cmd.ExecuteReaderAsync()
        let rows = ResizeArray<SecurityEvent>()

        while r.Read() do
            rows.Add
                { OccurredAt = r.GetDateTime 0
                  Event = r.GetString 1
                  Provider = if r.IsDBNull 2 then None else Some(r.GetString 2)
                  Actor = r.GetString 3 }

        return List.ofSeq rows
    }
