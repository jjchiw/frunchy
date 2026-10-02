module FrunchyBackend.Handlers.Imports.GetImportStatus

open System
open Npgsql
open Oxpecker
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract (the "view" of a JSON endpoint)
// ---------------------------------------------------------------------------

type StatusResponse =
    { import_id: Guid
      status: string
      processed_watch_events: int
      error_message: string option }

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private tryGetStatus (connection: NpgsqlConnection) (importId: Guid) =
    task {
        let query = "SELECT status, processed_watch_events, error_message FROM import_records WHERE cr_import_id = @id"
        use cmd = new NpgsqlCommand(query, connection)
        cmd.Parameters.AddWithValue("@id", importId) |> ignore

        use! reader = cmd.ExecuteReaderAsync()
        if reader.Read() then
            let error = if reader.IsDBNull(2) then None else Some(reader.GetString(2))
            return
                Some
                    { import_id = importId
                      status = reader.GetString(0)
                      processed_watch_events = reader.GetInt32(1)
                      error_message = error }
        else
            return None
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (importId: Guid) : EndpointHandler =
    fun ctx ->
        task {
            try
                let dbConfig = ctx.GetService<DbConfig>()
                use connection = getConnection dbConfig
                do! connection.OpenAsync()
                let! result = tryGetStatus connection importId
                do! connection.CloseAsync()

                match result with
                | Some response -> return! ctx.WriteJson response
                | None ->
                    ctx.SetStatusCode 404
                    return! ctx.WriteText "Not found"
            with ex ->
                ctx.SetStatusCode 500
                return! ctx.WriteText $"Error: {ex.Message}"
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/imports/{%O:guid}" handler ]
