module FrunchyBackend.Handlers.Imports.CreateImport

open System
open Npgsql
open Oxpecker
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract (the "view" of a JSON endpoint)
// ---------------------------------------------------------------------------

/// Request body for POST /imports
[<CLIMutable>]
type ImportRequest =
    { user_id: Guid
      data: string } // Raw JSON payload from the extension

/// Response to an import request (always 202 Accepted)
type ImportResponse =
    { import_id: Guid
      status: string
      message: string }

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// Store the raw import (never deleted) and queue it as Pending for ImportProcessorService
let private insertImport (connection: NpgsqlConnection) (importId: Guid) (req: ImportRequest) =
    task {
        let query = "INSERT INTO cr_imports (id, user_id, payload, created_at) VALUES (@id, @user_id, @payload::jsonb, CURRENT_TIMESTAMP)"
        use cmd = new NpgsqlCommand(query, connection)
        cmd.Parameters.AddWithValue("@id", importId) |> ignore
        cmd.Parameters.AddWithValue("@user_id", req.user_id) |> ignore
        cmd.Parameters.AddWithValue("@payload", req.data) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()

        let recQuery = "INSERT INTO import_records (cr_import_id, user_id, status, updated_at) VALUES (@import_id, @user_id, 'Pending', CURRENT_TIMESTAMP)"
        use cmd2 = new NpgsqlCommand(recQuery, connection)
        cmd2.Parameters.AddWithValue("@import_id", importId) |> ignore
        cmd2.Parameters.AddWithValue("@user_id", req.user_id) |> ignore
        let! _ = cmd2.ExecuteNonQueryAsync()
        ()
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler: EndpointHandler =
    fun ctx ->
        task {
            try
                let dbConfig = ctx.GetService<DbConfig>()
                let! req = ctx.BindJson<ImportRequest>()
                let importId = Guid.NewGuid()

                use connection = getConnection dbConfig
                do! connection.OpenAsync()
                do! insertImport connection importId req
                do! connection.CloseAsync()

                ctx.SetStatusCode 202
                let response: ImportResponse = { import_id = importId; status = "Accepted"; message = "Import queued for processing" }
                return! ctx.WriteJson response
            with ex ->
                ctx.SetStatusCode 500
                return! ctx.WriteText $"Error: {ex.Message}"
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/imports" handler ]
