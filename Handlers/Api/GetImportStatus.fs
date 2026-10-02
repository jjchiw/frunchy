module FrunchyBackend.Handlers.Api.GetImportStatus

open System
open System.Threading.Tasks
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Contract (specs/001-crunchyroll-history-import/contracts/import-api.md)
// ---------------------------------------------------------------------------

type CountsResponse =
    { received: int
      processed: int
      unresolved: int
      unsupported: int
      failed: int }

type IssueResponse =
    { page: int
      index: int
      episodeId: string // null when the entry had no id
      outcome: string
      reason: string }

type StatusResponse =
    { importId: Guid
      status: string
      receivedAt: DateTime
      completedAt: Nullable<DateTime>
      counts: CountsResponse
      issues: IssueResponse array
      error: string } // null unless status = failed

// ---------------------------------------------------------------------------
// Data access
// ---------------------------------------------------------------------------

/// The import, only when it belongs to the authenticated user (FR-001b)
let private tryLoad (db: DbConfig) (userId: Guid) (importId: Guid) : Task<StatusResponse option> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT status, received_at, completed_at, entries_received, processed, unresolved, unsupported, failed, error
                FROM cr_imports WHERE id = @id AND user_id = @user_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", importId) |> ignore
        cmd.Parameters.AddWithValue("user_id", userId) |> ignore

        let! header =
            task {
                use! r = cmd.ExecuteReaderAsync()

                if r.Read() then
                    return
                        Some(
                            r.GetString 0,
                            r.GetDateTime 1,
                            (if r.IsDBNull 2 then Nullable() else Nullable(r.GetDateTime 2)),
                            { received = r.GetInt32 3
                              processed = r.GetInt32 4
                              unresolved = r.GetInt32 5
                              unsupported = r.GetInt32 6
                              failed = r.GetInt32 7 },
                            (if r.IsDBNull 8 then null else r.GetString 8)
                        )
                else
                    return None
            }

        match header with
        | None -> return None
        | Some(status, receivedAt, completedAt, counts, error) ->
            use issuesCmd =
                new NpgsqlCommand(
                    """
                    SELECT page_index, entry_index, cr_episode_id, outcome, reason
                    FROM cr_import_entry_issues WHERE import_id = @id ORDER BY page_index, entry_index
                    """,
                    connection
                )

            issuesCmd.Parameters.AddWithValue("id", importId) |> ignore
            use! r = issuesCmd.ExecuteReaderAsync()
            let issues = ResizeArray<IssueResponse>()

            while r.Read() do
                issues.Add
                    { page = r.GetInt32 0
                      index = r.GetInt32 1
                      episodeId = (if r.IsDBNull 2 then null else r.GetString 2)
                      outcome = r.GetString 3
                      reason = r.GetString 4 }

            return
                Some
                    { importId = importId
                      status = status
                      receivedAt = receivedAt
                      completedAt = completedAt
                      counts = counts
                      issues = issues.ToArray()
                      error = error }
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (importId: Guid) : EndpointHandler =
    Auth.withUser (fun (user: User) ctx ->
        task {
            match! tryLoad (ctx.GetService<DbConfig>()) user.Id importId with
            | Some status -> return! ctx.WriteJson status
            | None -> return! ApiErrors.notFound ctx
        })

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/api/imports/{%O:guid}" handler ]
