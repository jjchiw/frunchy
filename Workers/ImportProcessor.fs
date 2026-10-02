module FrunchyBackend.Workers.ImportProcessor

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open FrunchyBackend.Infrastructure.Database
open Npgsql
open Newtonsoft.Json.Linq

/// Background service to process pending imports
type ImportProcessorService(dbConfig: DbConfig) =
    inherit BackgroundService()

    // Update import status in database
    let updateImportStatus (connection: NpgsqlConnection) (importId: Guid) (status: string) (errorMsg: string option) : Async<unit> = async {
        let query = """
            UPDATE import_records
            SET status = @status, error_message = @error_msg, updated_at = CURRENT_TIMESTAMP
            WHERE cr_import_id = @import_id
        """

        use cmd = new NpgsqlCommand(query, connection)
        cmd.Parameters.AddWithValue("@import_id", importId) |> ignore
        cmd.Parameters.AddWithValue("@status", status) |> ignore
        let errorValue = errorMsg |> Option.map box |> Option.defaultValue (box DBNull.Value)
        cmd.Parameters.AddWithValue("@error_msg", errorValue) |> ignore

        do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
    }

    // Get pending imports
    let getPendingImports (connection: NpgsqlConnection) : Async<(Guid * Guid * string) list> = async {
        let query = """
            SELECT ci.id, ci.user_id, ci.payload
            FROM cr_imports ci
            JOIN import_records ir ON ci.id = ir.cr_import_id
            WHERE ir.status = 'Pending'
            ORDER BY ci.created_at ASC
            LIMIT 10
        """

        use cmd = new NpgsqlCommand(query, connection)
        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask

        let mutable results = []
        while reader.Read() do
            let importId = reader.GetGuid(0)
            let userId = reader.GetGuid(1)
            let payload = reader.GetString(2)
            results <- results @ [(importId, userId, payload)]

        return results
    }

    // Process a single import
    let processImport (connection: NpgsqlConnection) (importId: Guid) (userId: Guid) (payload: string) : Async<Result<int, string>> = async {
        try
            // Parse the JSON payload
            let json = JObject.Parse(payload)

            // Extract array of watch history items
            // This depends on the Crunchyroll extension format
            let data = json.["data"] :?> JArray

            if data = null then
                return Result.Error "Invalid payload format: missing 'data' array"
            else
                let mutable watchEventCount = 0

                // Process each item
                for item in data do
                    try
                        // For now, just count them
                        // In a real implementation, we'd:
                        // 1. Extract anime info
                        // 2. Look up or create anime in catalog
                        // 3. Create watch_event record
                        watchEventCount <- watchEventCount + 1
                    with
                    | ex -> () // Log and continue

                return Result.Ok watchEventCount
        with ex ->
            return Result.Error ex.Message
    }

    override this.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    use connection = getConnection dbConfig
                    do! connection.OpenAsync()

                    // Get pending imports
                    let! pendingImports = getPendingImports connection

                    if not (List.isEmpty pendingImports) then
                        for (importId, userId, payload) in pendingImports do
                            // Mark as processing
                            do! updateImportStatus connection importId "Processing" None

                            // Process the import
                            let! result = processImport connection importId userId payload

                            match result with
                            | Ok count ->
                                // Update record with success
                                let updateQuery = """
                                    UPDATE import_records
                                    SET status = 'Completed', processed_watch_events = @count, updated_at = CURRENT_TIMESTAMP
                                    WHERE cr_import_id = @import_id
                                """

                                use cmd = new NpgsqlCommand(updateQuery, connection)
                                cmd.Parameters.AddWithValue("@import_id", importId) |> ignore
                                cmd.Parameters.AddWithValue("@count", count) |> ignore

                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                printfn "Completed import %A: processed %d watch events" importId count

                            | Error errorMsg ->
                                do! updateImportStatus connection importId "Failed" (Some errorMsg)
                                printfn "Failed import %A: %s" importId errorMsg

                    do! connection.CloseAsync()

                    // Wait before next check (30 seconds)
                    do! Task.Delay(30000, stoppingToken) |> Async.AwaitTask
                with ex ->
                    printfn "Error in ImportProcessorService: %s" ex.Message
                    do! Task.Delay(5000, stoppingToken) |> Async.AwaitTask
        }


