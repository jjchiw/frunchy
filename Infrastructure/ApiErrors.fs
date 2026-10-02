module FrunchyBackend.Infrastructure.ApiErrors

open Microsoft.AspNetCore.Http
open Oxpecker

// Error codes of specs/001-crunchyroll-history-import/contracts/import-api.md ("Errors")
[<Literal>]
let Unauthorized = "unauthorized"

[<Literal>]
let InvalidSubmission = "invalid_submission"

[<Literal>]
let HistoryOwnerMissing = "history_owner_missing"

[<Literal>]
let HistoryOwnerMismatch = "history_owner_mismatch"

[<Literal>]
let HistoryOwnerTaken = "history_owner_taken"

[<Literal>]
let PayloadTooLarge = "payload_too_large"

[<Literal>]
let NotFound = "not_found"

/// Wire shape `{ "error": code, "message": English text }` (field names are the wire names)
type ApiError = { error: string; message: string }

let writeError (ctx: HttpContext) (status: int) (code: string) (message: string) =
    ctx.SetStatusCode status
    ctx.WriteJson { error = code; message = message }

let unauthorized (ctx: HttpContext) =
    writeError ctx StatusCodes.Status401Unauthorized Unauthorized "Missing, unknown or revoked sync key."

let notFound (ctx: HttpContext) =
    writeError ctx StatusCodes.Status404NotFound NotFound "Not found."
