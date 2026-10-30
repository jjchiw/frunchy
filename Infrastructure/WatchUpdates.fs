/// Feature 028: watch updates on the fediverse. A user who turned them on (`watch_update_settings`), whose profile is
/// Public and whose username can federate gets, at most once per UTC day, one Note on their account listing what they
/// finished recently. This module is the data access the import processor, the account page and the account handlers
/// share: it decides when a day's update is queued (`queueAfterImport`), records the opt-in (`setEnabled`) and queues
/// the removal of earlier posts (`queueRemoval`). The worker (`Workers/FediverseOutbox.fs`) builds and sends the Note.
/// Nothing here talks to Megáfono.
module FrunchyBackend.Infrastructure.WatchUpdates

open System
open System.Threading.Tasks
open Npgsql

/// Only recent watching is posted: an old history imported today posts nothing
let recentWindow = TimeSpan.FromHours 48.0

/// What `queueAfterImport` needs beyond the database: the instance default visibility (the rule of `Profiles.effective`)
type Policy = { DefaultVisibility: Profiles.Visibility }

/// Some only when federation is on: with it off nothing is queued and no query is made
let policyOf (federation: Federation.FederationSettings) (profiles: Profiles.ProfileSettings) : Policy option =
    match federation with
    | Federation.Enabled _ -> Some { DefaultVisibility = profiles.DefaultVisibility }
    | Federation.Disabled -> None

/// Whether the user turned watch updates on (the account page)
let isOn (connection: NpgsqlConnection) (userId: Guid) : Task<bool> =
    task {
        use cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM watch_update_settings WHERE user_id = @user_id)", connection)
        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! found = cmd.ExecuteScalarAsync()
        return unbox<bool> found
    }

/// After an import completed, in its transaction: when the user opted in, their effective visibility is Public and their
/// username can federate, and the window has a qualifying episode, insert the day's `watch_updates` row and queue
/// `post_watch_update`. The window is `(covered_from, now()]` on `watched_at`, where `covered_from` is the latest of
/// 48 hours ago, the moment the user turned updates on and the `covered_until` of their previous update (any state).
/// A qualifying episode is fully watched with a known length. One row per user and day (`ON CONFLICT DO NOTHING`), and
/// the outbox item is queued only when the row was inserted.
let queueAfterImport (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (policy: Policy) (userId: Guid) : Task<unit> =
    task {
        use usernameCmd = new NpgsqlCommand("SELECT username FROM users WHERE id = @user_id", connection, tx)
        usernameCmd.Parameters.AddWithValue("user_id", userId) |> ignore

        let! usernameValue = usernameCmd.ExecuteScalarAsync()

        let canFederate =
            match usernameValue with
            | :? string as username -> Fediverse.isValidHandle username
            | _ -> false

        if canFederate then
            use cmd =
                new NpgsqlCommand(
                    """
                    WITH s AS (
                        SELECT u.id AS user_id,
                               (now() AT TIME ZONE 'UTC')::date AS day,
                               now() AS until_at,
                               greatest(now() - @window,
                                        st.enabled_at,
                                        coalesce((SELECT max(p.covered_until) FROM watch_updates p WHERE p.user_id = u.id),
                                                 '-infinity'::timestamptz)) AS from_at
                        FROM users u
                        JOIN watch_update_settings st ON st.user_id = u.id
                        LEFT JOIN profile_settings ps ON ps.user_id = u.id
                        WHERE u.id = @user_id
                          AND coalesce(ps.visibility, @default_visibility) = 'public'
                    )
                    INSERT INTO watch_updates (user_id, day, covered_from, covered_until)
                    SELECT s.user_id, s.day, s.from_at, s.until_at
                    FROM s
                    WHERE EXISTS (SELECT 1
                                  FROM watch_events w
                                  JOIN cr_episodes e ON e.id = w.cr_episode_id
                                  WHERE w.user_id = s.user_id
                                    AND w.fully_watched
                                    AND e.duration_ms IS NOT NULL
                                    AND w.watched_at > s.from_at
                                    AND w.watched_at <= s.until_at)
                    ON CONFLICT (user_id, day) DO NOTHING
                    RETURNING day
                    """,
                    connection,
                    tx
                )

            cmd.Parameters.AddWithValue("user_id", userId) |> ignore
            cmd.Parameters.AddWithValue("window", recentWindow) |> ignore
            cmd.Parameters.AddWithValue("default_visibility", Profiles.Visibility.toDb policy.DefaultVisibility) |> ignore

            match! cmd.ExecuteScalarAsync() with
            | null -> ()
            | _ -> do! Fediverse.enqueue connection tx "post_watch_update" (string userId)
    }

/// Turn watch updates on (insert the row if missing, which keeps the first `enabled_at`) or off (delete it). True when
/// the state really changed. The caller records the event and, for off, calls `queueRemoval`.
let setEnabled (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: Guid) (on: bool) : Task<bool> =
    task {
        use cmd =
            if on then
                new NpgsqlCommand(
                    "INSERT INTO watch_update_settings (user_id) VALUES (@user_id) ON CONFLICT (user_id) DO NOTHING",
                    connection,
                    tx
                )
            else
                new NpgsqlCommand("DELETE FROM watch_update_settings WHERE user_id = @user_id", connection, tx)

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! changed = cmd.ExecuteNonQueryAsync()
        return changed > 0
    }

/// The user stopped being a poster (turned updates off, or the profile left Public): their `pending` updates become
/// `skipped`, and when any update is `pending` or `published` (read before the change) a `delete_watch_updates` item
/// is queued, which deletes the earlier posts on Megáfono. Run in the change's own transaction.
let queueRemoval (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: Guid) : Task<unit> =
    task {
        use anyCmd =
            new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM watch_updates WHERE user_id = @user_id AND state IN ('pending', 'published'))",
                connection,
                tx
            )

        anyCmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! any = anyCmd.ExecuteScalarAsync()

        if unbox<bool> any then
            use skip =
                new NpgsqlCommand("UPDATE watch_updates SET state = 'skipped' WHERE user_id = @user_id AND state = 'pending'", connection, tx)

            skip.Parameters.AddWithValue("user_id", userId) |> ignore
            let! _ = skip.ExecuteNonQueryAsync()
            do! Fediverse.enqueue connection tx "delete_watch_updates" (string userId)
    }
