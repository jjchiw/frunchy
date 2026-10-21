/// The sign-up mode (feature 020): `invite` (a new account needs an invite code, the default) or `open` (anyone who
/// signs in with a provider can create an account). Kept in `instance_settings`, read per request so a change by the
/// root applies to every copy of Frunchy at once. Fails closed: a missing row, an unknown value, or a mode nobody
/// set all mean `invite`. Only the root changes it (the handler's `Layout.requireRoot`), and the change and its
/// security event share one transaction.
module FrunchyBackend.Infrastructure.SignUpMode

open System
open System.Threading.Tasks
open Npgsql
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure.Database

type SignUpMode =
    | Invite
    | Open

/// The `instance_settings` key
[<Literal>]
let Key = "signup_mode"

let toDb =
    function
    | Invite -> "invite"
    | Open -> "open"

/// Exactly `open` opens sign-up; anything else (unknown, empty, different case, null) is `Invite`
let parse (value: string) =
    match value with
    | "open" -> Open
    | _ -> Invite

/// Only the two real values count as a valid choice from a form (parse is lenient on purpose; a form is not)
let tryParseChoice (value: string) =
    match value with
    | "invite" -> Some Invite
    | "open" -> Some Open
    | _ -> None

/// The mode now: one primary-key query; no row means `Invite`
let current (db: DbConfig) : Task<SignUpMode> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd = new NpgsqlCommand("SELECT value FROM instance_settings WHERE key = @key", connection)
        cmd.Parameters.AddWithValue("key", Key) |> ignore
        let! value = cmd.ExecuteScalarAsync()

        return
            match value with
            | :? string as s -> parse s
            | _ -> Invite
    }

/// Store the mode and record `signup_mode_changed` on the root's account, in the caller's transaction
let setIn (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (rootId: Guid) (mode: SignUpMode) : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                INSERT INTO instance_settings (key, value, updated_at, updated_by) VALUES (@key, @value, now(), @by)
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("key", Key) |> ignore
        cmd.Parameters.AddWithValue("value", toDb mode) |> ignore
        cmd.Parameters.AddWithValue("by", rootId) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        do! SecurityEvents.record connection tx rootId SecurityEventType.SignUpModeChanged None Actor.User
    }

/// Change the mode in one transaction (the setting and the event commit together or not at all)
let set (db: DbConfig) (rootId: Guid) (mode: SignUpMode) : Task<unit> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()
        use tx = connection.BeginTransaction()
        do! setIn connection tx rootId mode
        do! tx.CommitAsync()
    }
