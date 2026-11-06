/// Feature 030: weekly recap posts on the fediverse. A user who turned them on (`recap_post_settings`) before a week
/// began, whose profile is Public and whose username can federate gets, at most once per completed UTC week (Monday to
/// Monday), one Note on their account with the week's recap figures (feature 029's rules). This module is the data access
/// the import processor, the account page and the account handlers share: it decides when a week's post is queued
/// (`queueAfterImport`), records the opt-in (`setEnabled`) and queues the removal of earlier posts (`queueRemoval`). The
/// worker (`Workers/FediverseOutbox.fs`) builds and sends the Note. Independent of the daily watch updates (028). Nothing
/// here talks to Megáfono.
module FrunchyBackend.Infrastructure.RecapPosts

open System
open System.Threading.Tasks
open Npgsql

/// Whether the user turned recap posts on (the account page)
let isOn (connection: NpgsqlConnection) (userId: Guid) : Task<bool> =
    task {
        use cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM recap_post_settings WHERE user_id = @user_id)", connection)
        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! found = cmd.ExecuteScalarAsync()
        return unbox<bool> found
    }

/// After an import completed, in its transaction: the week to post is the last completed UTC week as of now (Monday 00:00
/// to the next Monday 00:00, `week_start` its Monday). When the user opted in before that week began, their effective
/// visibility is Public, their username can federate and the week has a fully watched episode of known length, insert
/// the week's `recap_posts` row and queue `post_recap`. One row per user and week (`ON CONFLICT DO NOTHING`), and the
/// outbox item is queued only when the row was inserted, so later imports and `user-rebuild` never post a week twice.
/// There is no backfill: a week nobody synced in the following week is never posted.
let queueAfterImport (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (policy: WatchUpdates.Policy) (userId: Guid) : Task<unit> =
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
                               st.enabled_at,
                               date_trunc('week', now() AT TIME ZONE 'UTC')::date - 7 AS week_start
                        FROM users u
                        JOIN recap_post_settings st ON st.user_id = u.id
                        LEFT JOIN profile_settings ps ON ps.user_id = u.id
                        WHERE u.id = @user_id
                          AND coalesce(ps.visibility, @default_visibility) = 'public'
                    ),
                    w AS (
                        SELECT s.user_id, s.week_start, s.enabled_at,
                               s.week_start::timestamp AT TIME ZONE 'UTC' AS from_at,
                               (s.week_start + 7)::timestamp AT TIME ZONE 'UTC' AS until_at
                        FROM s
                    )
                    INSERT INTO recap_posts (user_id, week_start, covered_from, covered_until)
                    SELECT w.user_id, w.week_start, w.from_at, w.until_at
                    FROM w
                    WHERE w.enabled_at <= w.from_at
                      AND EXISTS (SELECT 1
                                  FROM watch_events we
                                  JOIN cr_episodes e ON e.id = we.cr_episode_id
                                  WHERE we.user_id = w.user_id
                                    AND we.fully_watched
                                    AND e.duration_ms IS NOT NULL
                                    AND we.watched_at >= w.from_at
                                    AND we.watched_at < w.until_at)
                    ON CONFLICT (user_id, week_start) DO NOTHING
                    RETURNING week_start
                    """,
                    connection,
                    tx
                )

            cmd.Parameters.AddWithValue("user_id", userId) |> ignore
            cmd.Parameters.AddWithValue("default_visibility", Profiles.Visibility.toDb policy.DefaultVisibility) |> ignore

            match! cmd.ExecuteScalarAsync() with
            | null -> ()
            | _ -> do! Fediverse.enqueue connection tx "post_recap" (string userId)
    }

/// Turn recap posts on (insert the row if missing, which keeps the first `enabled_at`) or off (delete it). True when the
/// state really changed. The caller records the event and, for off, calls `queueRemoval`.
let setEnabled (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: Guid) (on: bool) : Task<bool> =
    task {
        use cmd =
            if on then
                new NpgsqlCommand(
                    "INSERT INTO recap_post_settings (user_id) VALUES (@user_id) ON CONFLICT (user_id) DO NOTHING",
                    connection,
                    tx
                )
            else
                new NpgsqlCommand("DELETE FROM recap_post_settings WHERE user_id = @user_id", connection, tx)

        cmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! changed = cmd.ExecuteNonQueryAsync()
        return changed > 0
    }

/// The user stopped being a recap poster (turned recaps off, or the profile left Public): their `pending` posts become
/// `skipped`, and when any post is `pending` or `published` (read before the change) a `delete_recaps` item is queued,
/// which deletes the earlier posts on Megáfono. Run in the change's own transaction.
let queueRemoval (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: Guid) : Task<unit> =
    task {
        use anyCmd =
            new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM recap_posts WHERE user_id = @user_id AND state IN ('pending', 'published'))",
                connection,
                tx
            )

        anyCmd.Parameters.AddWithValue("user_id", userId) |> ignore
        let! any = anyCmd.ExecuteScalarAsync()

        if unbox<bool> any then
            use skip =
                new NpgsqlCommand("UPDATE recap_posts SET state = 'skipped' WHERE user_id = @user_id AND state = 'pending'", connection, tx)

            skip.Parameters.AddWithValue("user_id", userId) |> ignore
            let! _ = skip.ExecuteNonQueryAsync()
            do! Fediverse.enqueue connection tx "delete_recaps" (string userId)
    }
