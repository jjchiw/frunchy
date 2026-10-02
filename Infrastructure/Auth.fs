module FrunchyBackend.Infrastructure.Auth

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

let private optionalString (reader: System.Data.Common.DbDataReader) (i: int) =
    if reader.IsDBNull i then None else Some(reader.GetString i)

/// Resolve `Authorization: Bearer <syncKey>` to its user (lookup by SHA-256 hash)
let tryAuthenticate (ctx: HttpContext) : Task<User option> =
    task {
        match ctx.TryGetHeaderValue "Authorization" with
        | Some header when header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ->
            let key = header.Substring(7).Trim()

            if key = "" then
                return None
            else
                use connection = getConnection (ctx.GetService<DbConfig>())
                do! connection.OpenAsync()

                use cmd =
                    new NpgsqlCommand(
                        "SELECT id, username, display_name, cr_account_id, cr_profile_id, created_at FROM users WHERE sync_key_hash = @hash",
                        connection
                    )

                cmd.Parameters.AddWithValue("hash", SyncKey.hash key) |> ignore
                use! reader = cmd.ExecuteReaderAsync()

                if reader.Read() then
                    return
                        Some
                            { Id = reader.GetGuid 0
                              Username = reader.GetString 1
                              DisplayName = reader.GetString 2
                              CrAccountId = optionalString reader 3
                              CrProfileId = optionalString reader 4
                              CreatedAt = reader.GetFieldValue<DateTimeOffset> 5 }
                else
                    return None
        | _ -> return None
    }

/// Run the handler for the authenticated user, or answer 401 `unauthorized`
let withUser (handler: User -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        task {
            match! tryAuthenticate ctx with
            | Some user -> return! handler user ctx
            | None -> return! ApiErrors.unauthorized ctx
        }
