module FrunchyBackend.Handlers.Api.CreateImport

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open NpgsqlTypes
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract (specs/001-crunchyroll-history-import/contracts/import-api.md)
// ---------------------------------------------------------------------------

[<Literal>]
let MaxBodyBytes = 30_000_000

[<Literal>]
let MaxPages = 100

/// 202 response
type AcceptedResponse =
    { importId: Guid
      status: string
      statusUrl: string
      entriesReceived: int }

type private Page =
    { Url: string
      CapturedAt: DateTimeOffset option
      /// The page body's exact JSON text, stored verbatim
      RawBody: string
      EntryCount: int }

type private Submission =
    { ExportedAt: DateTimeOffset option
      Owner: HistoryOwner
      Pages: Page list }

type private Rejection =
    { Status: int
      Code: string
      Message: string }

let private invalid message =
    { Status = StatusCodes.Status400BadRequest
      Code = ApiErrors.InvalidSubmission
      Message = message }

let private ownerMissing message =
    { Status = StatusCodes.Status400BadRequest
      Code = ApiErrors.HistoryOwnerMissing
      Message = message }

let private tooLarge =
    { Status = StatusCodes.Status413PayloadTooLarge
      Code = ApiErrors.PayloadTooLarge
      Message = $"Request body is over {MaxBodyBytes} bytes; send fewer pages per request." }

let private mismatch =
    { Status = StatusCodes.Status409Conflict
      Code = ApiErrors.HistoryOwnerMismatch
      Message =
        "This sync key belongs to a user bound to a different Crunchyroll account or profile. Ask the operator to reset the binding if you changed account or profile." }

let private taken =
    { Status = StatusCodes.Status409Conflict
      Code = ApiErrors.HistoryOwnerTaken
      Message = "This Crunchyroll history is already bound to another Frunchy user." }

let private tryDate (element: JsonElement) (name: string) : Result<DateTimeOffset option, Rejection> =
    match element.TryGetProperty name with
    | false, _ -> Ok None
    | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
    | true, value when value.ValueKind = JsonValueKind.String ->
        match
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, d -> Ok(Some d)
        | _ -> Error(invalid $"'{name}' is not an ISO-8601 date")
    | _ -> Error(invalid $"'{name}' must be a string")

let private parsePage (index: int) (page: JsonElement) : Result<Page, Rejection> =
    if page.ValueKind <> JsonValueKind.Object then
        Error(invalid $"pages[{index}] must be an object")
    else
        match page.TryGetProperty "url", page.TryGetProperty "body" with
        | (true, url), (true, body) when
            url.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(url.GetString()))
            && body.ValueKind = JsonValueKind.Object
            ->
            match body.TryGetProperty "data" with
            | true, data when data.ValueKind = JsonValueKind.Array ->
                tryDate page "capturedAt"
                |> Result.map (fun capturedAt ->
                    { Url = url.GetString()
                      CapturedAt = capturedAt
                      RawBody = body.GetRawText()
                      EntryCount = data.GetArrayLength() })
            | _ -> Error(invalid $"pages[{index}].body has no 'data' array")
        | (true, url), _ when url.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(url.GetString())) ->
            Error(invalid $"pages[{index}] needs a 'body' object (the Crunchyroll response)")
        | _ -> Error(invalid $"pages[{index}] needs a string 'url'")

/// Validation rules 2–3 of data-model.md (shape, then history owner)
let private parseSubmission (root: JsonElement) : Result<Submission, Rejection> =
    if root.ValueKind <> JsonValueKind.Object then
        Error(invalid "Body must be a JSON object with a 'pages' array")
    else
        match root.TryGetProperty "pages" with
        | true, pages when pages.ValueKind = JsonValueKind.Array ->
            let pageCount = pages.GetArrayLength()

            if pageCount < 1 || pageCount > MaxPages then
                Error(invalid $"'pages' must contain between 1 and {MaxPages} pages (got {pageCount})")
            else
                let parsed = [ for i, page in Seq.indexed (pages.EnumerateArray()) -> parsePage i page ]

                match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                | Some rejection -> Error rejection
                | None ->
                    let pageList = parsed |> List.choose Result.toOption

                    // Crunchyroll's history URL only identifies the account: every profile of an account
                    // shares it (verified with a second-profile capture, research R5), so the client must
                    // say which profile the pages belong to. Without it, profiles would be mixed.
                    let profileId =
                        match root.TryGetProperty "profileId" with
                        | true, p when p.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(p.GetString())) ->
                            Ok(Some(p.GetString()))
                        | true, p when p.ValueKind <> JsonValueKind.String && p.ValueKind <> JsonValueKind.Null ->
                            Error(invalid "'profileId' must be a string")
                        | _ ->
                            Error(
                                ownerMissing
                                    "'profileId' is required: Crunchyroll's history URL identifies the account, not the profile, so send the active Crunchyroll profile id."
                            )

                    let owners = pageList |> List.map (fun p -> CrunchyrollPage.tryHistoryOwner p.Url)

                    match profileId, tryDate root "exportedAt" with
                    | Error e, _
                    | _, Error e -> Error e
                    | Ok profile, Ok exportedAt ->
                        match owners |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
                        | Some message -> Error(ownerMissing message)
                        | None ->
                            match owners |> List.choose Result.toOption |> List.distinct with
                            | [ accountId ] ->
                                Ok
                                    { ExportedAt = exportedAt
                                      Owner = { AccountId = accountId; ProfileId = profile }
                                      Pages = pageList }
                            | _ -> Error(ownerMissing "All pages of a submission must come from the same Crunchyroll history")
        | _ -> Error(invalid "Body must be a JSON object with a 'pages' array")

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

let private dbText (value: string option) =
    let p = NpgsqlParameter(NpgsqlDbType = NpgsqlDbType.Text)
    p.Value <- (match value with Some v -> box v | None -> box DBNull.Value)
    p

let private dbTime (value: DateTimeOffset option) =
    let p = NpgsqlParameter(NpgsqlDbType = NpgsqlDbType.TimestampTz)
    p.Value <- (match value with Some v -> box v.UtcDateTime | None -> box DBNull.Value)
    p

let private named (name: string) (p: NpgsqlParameter) =
    p.ParameterName <- name
    p

/// Bind the user if needed (rule 4) and store the submission and its raw pages, all in one transaction
let private store (db: DbConfig) (user: User) (submission: Submission) : Task<Result<Guid, Rejection>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()

        use lockUser =
            new NpgsqlCommand("SELECT cr_account_id, cr_profile_id FROM users WHERE id = @id FOR UPDATE", connection, tx)

        lockUser.Parameters.AddWithValue("id", user.Id) |> ignore
        let! boundAccount, boundProfile =
            task {
                use! reader = lockUser.ExecuteReaderAsync()
                reader.Read() |> ignore
                let account = if reader.IsDBNull 0 then None else Some(reader.GetString 0)
                let profile = if reader.IsDBNull 1 then None else Some(reader.GetString 1)
                return account, profile
            }

        let owner = submission.Owner

        let! binding =
            task {
                match boundAccount with
                | Some account when account = owner.AccountId && boundProfile = owner.ProfileId -> return Ok()
                | Some _ -> return Error mismatch
                | None ->
                    use takenCmd =
                        new NpgsqlCommand(
                            "SELECT EXISTS (SELECT 1 FROM users WHERE id <> @id AND cr_account_id = @account AND coalesce(cr_profile_id, '') = coalesce(@profile, ''))",
                            connection,
                            tx
                        )

                    takenCmd.Parameters.AddWithValue("id", user.Id) |> ignore
                    takenCmd.Parameters.Add(named "account" (dbText (Some owner.AccountId))) |> ignore
                    takenCmd.Parameters.Add(named "profile" (dbText owner.ProfileId)) |> ignore
                    let! isTaken = takenCmd.ExecuteScalarAsync()

                    if unbox<bool> isTaken then
                        return Error taken
                    else
                        use bind =
                            new NpgsqlCommand(
                                "UPDATE users SET cr_account_id = @account, cr_profile_id = @profile WHERE id = @id",
                                connection,
                                tx
                            )

                        bind.Parameters.AddWithValue("id", user.Id) |> ignore
                        bind.Parameters.Add(named "account" (dbText (Some owner.AccountId))) |> ignore
                        bind.Parameters.Add(named "profile" (dbText owner.ProfileId)) |> ignore

                        try
                            let! _ = bind.ExecuteNonQueryAsync()
                            return Ok()
                        with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation ->
                            return Error taken
            }

        match binding with
        | Error rejection -> return Error rejection
        | Ok() ->
            let importId = Guid.NewGuid()

            use insertImport =
                new NpgsqlCommand(
                    """
                    INSERT INTO cr_imports (id, user_id, received_at, exported_at, status, page_count, entries_received)
                    VALUES (@id, @user_id, now(), @exported_at, 'pending', @page_count, @entries_received)
                    """,
                    connection,
                    tx
                )

            insertImport.Parameters.AddWithValue("id", importId) |> ignore
            insertImport.Parameters.AddWithValue("user_id", user.Id) |> ignore
            insertImport.Parameters.Add(named "exported_at" (dbTime submission.ExportedAt)) |> ignore
            insertImport.Parameters.AddWithValue("page_count", submission.Pages.Length) |> ignore
            insertImport.Parameters.AddWithValue("entries_received", submission.Pages |> List.sumBy _.EntryCount) |> ignore
            let! _ = insertImport.ExecuteNonQueryAsync()

            use batch = new NpgsqlBatch(connection, tx)

            for index, page in List.indexed submission.Pages do
                let cmd =
                    NpgsqlBatchCommand(
                        "INSERT INTO cr_import_pages (import_id, page_index, url, captured_at, body) VALUES ($1, $2, $3, $4, $5)"
                    )

                cmd.Parameters.Add(NpgsqlParameter(Value = importId)) |> ignore
                cmd.Parameters.Add(NpgsqlParameter(Value = index)) |> ignore
                cmd.Parameters.Add(NpgsqlParameter(Value = page.Url)) |> ignore
                cmd.Parameters.Add(dbTime page.CapturedAt) |> ignore
                cmd.Parameters.Add(NpgsqlParameter(NpgsqlDbType = NpgsqlDbType.Json, Value = page.RawBody)) |> ignore
                batch.BatchCommands.Add cmd

            let! _ = batch.ExecuteNonQueryAsync()
            do! tx.CommitAsync()
            return Ok importId
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

/// Read the body, refusing anything over MaxBodyBytes (rule 5)
let private readBody (ctx: HttpContext) : Task<Result<byte array, Rejection>> =
    task {
        if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > int64 MaxBodyBytes then
            return Error tooLarge
        else
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 81920
            let mutable reading = true
            let mutable over = false

            while reading do
                let! n = ctx.Request.Body.ReadAsync(chunk, 0, chunk.Length, ctx.RequestAborted)

                if n = 0 then
                    reading <- false
                elif buffer.Length + int64 n > int64 MaxBodyBytes then
                    over <- true
                    reading <- false
                else
                    buffer.Write(chunk, 0, n)

            return if over then Error tooLarge else Ok(buffer.ToArray())
    }

let private parseJson (bytes: byte array) : Result<JsonDocument, Rejection> =
    try
        Ok(JsonDocument.Parse(ReadOnlyMemory bytes))
    with :? JsonException ->
        Error(invalid "Body is not valid JSON")

let private reject (ctx: HttpContext) (r: Rejection) = ApiErrors.writeError ctx r.Status r.Code r.Message

let handler: EndpointHandler =
    Auth.withUser (fun user ctx ->
        task {
            match! readBody ctx with
            | Error r -> return! reject ctx r
            | Ok bytes ->
                match parseJson bytes with
                | Error r -> return! reject ctx r
                | Ok doc ->
                    use doc = doc

                    match parseSubmission doc.RootElement with
                    | Error r -> return! reject ctx r
                    | Ok submission ->
                        match! store (ctx.GetService<DbConfig>()) user submission with
                        | Error r -> return! reject ctx r
                        | Ok importId ->
                            ctx.SetStatusCode StatusCodes.Status202Accepted

                            return!
                                ctx.WriteJson
                                    { importId = importId
                                      status = "pending"
                                      statusUrl = $"/api/imports/{importId}"
                                      entriesReceived = submission.Pages |> List.sumBy _.EntryCount }
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = POST [ route "/api/imports" handler ]
