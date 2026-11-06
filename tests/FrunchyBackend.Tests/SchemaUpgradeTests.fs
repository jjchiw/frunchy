/// Feature 021 (T001): the first change to a table that already exists in production. `profile_settings` was created
/// by feature 017 with CHECK (visibility IN ('public', 'private')); `initializeSchema` must accept `members` on such a
/// database without losing a row, change nothing on its second run, and be safe when copies start together.
module FrunchyBackend.Tests.SchemaUpgradeTests

open System
open System.Threading.Tasks
open Npgsql
open Xunit
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// `profile_settings` exactly as feature 017 created it (the old CHECK), copied from git history
let private shape017 =
    """
    DROP TABLE profile_settings;
    CREATE TABLE profile_settings (
        user_id               uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
        visibility            text NULL CHECK (visibility IN ('public', 'private')),
        share_token_hash      text NULL UNIQUE,
        share_link_id         uuid NULL,
        share_link_created_at timestamptz NULL,
        updated_at            timestamptz NOT NULL DEFAULT now(),
        CHECK ((share_token_hash IS NULL) = (share_link_id IS NULL)
               AND (share_link_id IS NULL) = (share_link_created_at IS NULL))
    );
    """

let private publicUser = Guid.Parse "00000000-0000-0000-0000-0000000000a1"
let private privateUser = Guid.Parse "00000000-0000-0000-0000-0000000000a2"
let private defaultUser = Guid.Parse "00000000-0000-0000-0000-0000000000a3"

/// A database from feature 017: the whole schema, then `profile_settings` rebuilt in its old shape with three rows
let private database017 () : Task<DbConfig> =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db shape017

        for (id, name) in [ publicUser, "pub"; privateUser, "priv"; defaultUser, "dflt" ] do
            do!
                Db.execute
                    db
                    $"INSERT INTO users (id, username, display_name) VALUES ('{id}', '{name}', '{name}')"

        do!
            Db.execute
                db
                $"""INSERT INTO profile_settings (user_id, visibility) VALUES
                    ('{publicUser}', 'public'), ('{privateUser}', 'private'), ('{defaultUser}', NULL)"""

        return db
    }

let private checkOid (db: DbConfig) =
    Db.scalar<string>
        db
        "SELECT c.oid::text FROM pg_constraint c WHERE c.conrelid = 'profile_settings'::regclass AND c.contype = 'c' AND pg_get_constraintdef(c.oid) LIKE '%visibility%'"
        []

let private checkDefinitions (db: DbConfig) =
    Db.dump
        db
        "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'profile_settings'::regclass AND contype = 'c' ORDER BY 1"

let private rows (db: DbConfig) =
    Db.dump db "SELECT user_id || '|' || coalesce(visibility, '<null>') FROM profile_settings ORDER BY 1"

let private setVisibility (db: DbConfig) (userId: Guid) (value: string) =
    Db.execute db $"UPDATE profile_settings SET visibility = '{value}' WHERE user_id = '{userId}'"

[<Fact>]
let ``the 017 shape refuses members before the upgrade`` () =
    task {
        let! db = database017 ()
        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> setVisibility db defaultUser "members" :> Task)
        Assert.Equal("23514", error.SqlState)
    }

[<Fact>]
let ``the upgrade accepts members, keeps every row, and the second run changes nothing`` () =
    task {
        let! db = database017 ()
        let! rowsBefore = rows db

        do! initializeSchema db

        // members is accepted, anything else still is not
        do! setVisibility db defaultUser "members"
        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> setVisibility db defaultUser "everyone" :> Task)
        Assert.Equal("23514", error.SqlState)
        do! setVisibility db defaultUser "public"
        do! Db.execute db $"UPDATE profile_settings SET visibility = NULL WHERE user_id = '{defaultUser}'"

        // the old rows are exactly as they were
        let! rowsAfter = rows db
        Assert.Equal<string list>(rowsBefore, rowsAfter)
        Assert.Equal(3, rowsAfter.Length)

        // a second run (the next start) is a no-op: the same constraint object, not a drop and re-add
        let! oidBefore = checkOid db
        let! definitionsBefore = checkDefinitions db
        do! initializeSchema db
        do! initializeSchema db
        let! oidAfter = checkOid db
        let! definitionsAfter = checkDefinitions db

        Assert.Equal(oidBefore, oidAfter)
        Assert.Equal<string list>(definitionsBefore, definitionsAfter)
        Assert.Contains(definitionsAfter, fun d -> d.StartsWith "profile_settings_visibility_check|" && d.Contains "members")
        let! rowsEnd = rows db
        Assert.Equal<string list>(rowsBefore, rowsEnd)
    }

[<Fact>]
let ``a fresh database accepts members and the same upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! oidBefore = checkOid db

        do! Db.execute db $"INSERT INTO users (id, username, display_name) VALUES ('{publicUser}', 'pub', 'pub')"
        do! Db.execute db $"INSERT INTO profile_settings (user_id, visibility) VALUES ('{publicUser}', 'members')"

        do! initializeSchema db
        let! oidAfter = checkOid db
        Assert.Equal(oidBefore, oidAfter)
    }

[<Fact>]
let ``copies starting together on the old database all succeed and upgrade it once`` () =
    task {
        let! db = database017 ()
        let! rowsBefore = rows db

        let! results = Task.WhenAll [| for _ in 1..6 -> initializeSchema db |]
        Assert.Equal(6, results.Length)

        let! definitions = checkDefinitions db
        // exactly one check on visibility, and it is the three-value one
        let onVisibility = definitions |> List.filter (fun d -> d.Contains "visibility")
        Assert.Single onVisibility |> ignore
        Assert.Contains("members", onVisibility.Head)

        let! rowsAfter = rows db
        Assert.Equal<string list>(rowsBefore, rowsAfter)

        do! setVisibility db defaultUser "members"
    }

// ---------------------------------------------------------------------------
// Feature 023: invites.is_setup on a database whose invites table predates it
// ---------------------------------------------------------------------------

/// `invites` as it was before feature 023 (no is_setup column), with two rows
let private databaseBefore023 () : Task<DbConfig> =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db "ALTER TABLE invites DROP COLUMN is_setup"

        do!
            Db.execute
                db
                """INSERT INTO invites (id, code_hash, expires_at) VALUES
                    ('00000000-0000-0000-0000-0000000000b1', 'hash-one', now() + interval '7 days'),
                    ('00000000-0000-0000-0000-0000000000b2', 'hash-two', now() + interval '7 days')"""

        return db
    }

let private setupColumn (db: DbConfig) =
    Db.dump
        db
        "SELECT data_type || '|' || is_nullable || '|' || column_default FROM information_schema.columns WHERE table_name = 'invites' AND column_name = 'is_setup'"

[<Fact>]
let ``an invites table without is_setup gets the column once, old rows read false, and the second run changes nothing`` () =
    task {
        let! db = databaseBefore023 ()
        let! before = setupColumn db
        Assert.Empty before

        do! initializeSchema db
        let! after = setupColumn db
        Assert.Equal<string list>([ "boolean|NO|false" ], after)

        let! flags = Db.dump db "SELECT code_hash || '|' || is_setup FROM invites ORDER BY code_hash"
        Assert.Equal<string list>([ "hash-one|false"; "hash-two|false" ], flags)

        // a row marked as setup survives the next runs (the upgrade never resets it)
        do! Db.execute db "UPDATE invites SET is_setup = true WHERE code_hash = 'hash-two'"
        do! initializeSchema db
        do! initializeSchema db
        let! again = setupColumn db
        Assert.Equal<string list>([ "boolean|NO|false" ], again)
        let! flagsAgain = Db.dump db "SELECT code_hash || '|' || is_setup FROM invites ORDER BY code_hash"
        Assert.Equal<string list>([ "hash-one|false"; "hash-two|true" ], flagsAgain)
    }

[<Fact>]
let ``copies starting together on an invites table without is_setup all succeed`` () =
    task {
        let! db = databaseBefore023 ()
        let! results = Task.WhenAll [| for _ in 1..6 -> initializeSchema db |]
        Assert.Equal(6, results.Length)
        let! after = setupColumn db
        Assert.Equal<string list>([ "boolean|NO|false" ], after)
    }

// ---------------------------------------------------------------------------
// fediverse_outbox.state gains 'failed' (feature 015 fix)
// ---------------------------------------------------------------------------

/// `fediverse_outbox` as feature 015 created it: state is only pending or done
let private outboxShapeBefore =
    """
    DROP TABLE fediverse_outbox;
    CREATE TABLE fediverse_outbox (
        id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        kind            text NOT NULL CHECK (kind IN ('ensure_thread', 'publish_comment', 'update_comment', 'delete_comment',
                                                       'delete_actor', 'refresh_profile')),
        subject         text NOT NULL,
        state           text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'done')),
        attempts        int NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL DEFAULT now(),
        claimed_at      timestamptz NULL,
        last_error      text NULL,
        created_at      timestamptz NOT NULL DEFAULT now(),
        done_at         timestamptz NULL
    );
    CREATE INDEX IF NOT EXISTS ix_fediverse_outbox_pending ON fediverse_outbox (next_attempt_at, id) WHERE state = 'pending';
    """

let private setOutboxFailed (db: DbConfig) =
    Db.execute db "UPDATE fediverse_outbox SET state = 'failed', last_error = '400 invalid_request' WHERE subject = 's1'"

let private outboxChecks (db: DbConfig) =
    Db.dump
        db
        "SELECT oid::text || '|' || conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'fediverse_outbox'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%state%'"

[<Fact>]
let ``the upgrade lets an older outbox hold failed items, keeps its rows, and the second run changes nothing`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db outboxShapeBefore
        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject, state) VALUES ('refresh_profile', 's1', 'pending'), ('refresh_profile', 's2', 'done')"

        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> setOutboxFailed db :> Task)
        Assert.Equal("23514", error.SqlState)

        do! initializeSchema db
        do! setOutboxFailed db
        let! error2 = Assert.ThrowsAnyAsync<PostgresException>(fun () -> Db.execute db "UPDATE fediverse_outbox SET state = 'lost' WHERE subject = 's2'" :> Task)
        Assert.Equal("23514", error2.SqlState)

        let! rows = Db.dump db "SELECT subject || '|' || state FROM fediverse_outbox ORDER BY id"
        Assert.Equal<string list>([ "s1|failed"; "s2|done" ], rows)

        // the next starts leave the constraint alone
        let! before = outboxChecks db
        do! initializeSchema db
        do! initializeSchema db
        let! after = outboxChecks db
        Assert.Equal<string list>(before, after)
        Assert.Equal(1, after.Length)
        Assert.Contains("fediverse_outbox_state_check", after[0])
    }

[<Fact>]
let ``a fresh database accepts failed outbox items and the upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! before = outboxChecks db
        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject, state) VALUES ('refresh_profile', 's1', 'failed')"
        do! initializeSchema db
        let! after = outboxChecks db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// fediverse_outbox.kind gains post_watch_update and delete_watch_updates (feature 028)
// ---------------------------------------------------------------------------

let private kindChecks (db: DbConfig) =
    Db.dump
        db
        "SELECT oid::text || '|' || conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'fediverse_outbox'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%kind%'"

let private insertWatchKinds (db: DbConfig) =
    Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('post_watch_update', 'u1'), ('delete_watch_updates', 'u1')"

[<Fact>]
let ``the upgrade lets an older outbox hold the watch update kinds, keeps its rows, and the second run changes nothing`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        // the six-kind shape (state already has failed), as a database from before feature 028 has it
        do!
            Db.execute
                db
                (outboxShapeBefore.Replace("CHECK (state IN ('pending', 'done'))", "CHECK (state IN ('pending', 'done', 'failed'))"))

        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject, state) VALUES ('refresh_profile', 's1', 'pending'), ('delete_actor', 's2', 'done')"

        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> insertWatchKinds db :> Task)
        Assert.Equal("23514", error.SqlState)

        do! initializeSchema db
        do! insertWatchKinds db

        let! error2 = Assert.ThrowsAnyAsync<PostgresException>(fun () -> Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('nonsense', 'u1')" :> Task)
        Assert.Equal("23514", error2.SqlState)

        let! rows = Db.dump db "SELECT kind || '|' || subject || '|' || state FROM fediverse_outbox ORDER BY id"

        Assert.Equal<string list>(
            [ "refresh_profile|s1|pending"
              "delete_actor|s2|done"
              "post_watch_update|u1|pending"
              "delete_watch_updates|u1|pending" ],
            rows
        )

        // the next starts leave the constraint alone
        let! before = kindChecks db
        do! initializeSchema db
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
        Assert.Equal(1, after.Length)
        Assert.Contains("fediverse_outbox_kind_check", after[0])
    }

[<Fact>]
let ``a fresh database accepts the watch update kinds and the upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! before = kindChecks db
        do! insertWatchKinds db
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// fediverse_outbox.kind gains post_recap and delete_recaps (feature 030)
// ---------------------------------------------------------------------------

let private insertRecapKinds (db: DbConfig) =
    Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('post_recap', 'u1'), ('delete_recaps', 'u1')"

/// The outbox as feature 028 left it: the eight-kind check under its own name
let private eightKindOutbox =
    outboxShapeBefore
        .Replace("CHECK (state IN ('pending', 'done'))", "CHECK (state IN ('pending', 'done', 'failed'))")
        .Replace("'delete_actor', 'refresh_profile'))", "'delete_actor', 'refresh_profile', 'post_watch_update', 'delete_watch_updates'))")

let private sixKindOutbox =
    outboxShapeBefore.Replace("CHECK (state IN ('pending', 'done'))", "CHECK (state IN ('pending', 'done', 'failed'))")

let private checkRecapUpgrade (shape: string) (expectedRows: string list) =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db shape
        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject, state) VALUES ('refresh_profile', 's1', 'pending'), ('delete_actor', 's2', 'done')"

        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> insertRecapKinds db :> Task)
        Assert.Equal("23514", error.SqlState)

        do! initializeSchema db
        do! insertRecapKinds db
        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('post_watch_update', 'u2'), ('delete_watch_updates', 'u2')"

        let! error2 = Assert.ThrowsAnyAsync<PostgresException>(fun () -> Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('nonsense', 'u1')" :> Task)
        Assert.Equal("23514", error2.SqlState)

        let! rows = Db.dump db "SELECT kind || '|' || subject || '|' || state FROM fediverse_outbox ORDER BY id"

        Assert.Equal<string list>(
            expectedRows
            @ [ "post_recap|u1|pending"
                "delete_recaps|u1|pending"
                "post_watch_update|u2|pending"
                "delete_watch_updates|u2|pending" ],
            rows
        )

        // the next starts leave the constraint alone
        let! before = kindChecks db
        do! initializeSchema db
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
        Assert.Equal(1, after.Length)
        Assert.Contains("fediverse_outbox_kind_check", after[0])
        Assert.Contains("post_recap", after[0])
    }

[<Fact>]
let ``the upgrade takes an eight-kind outbox to ten kinds, keeps its rows, and the second run changes nothing`` () =
    checkRecapUpgrade eightKindOutbox [ "refresh_profile|s1|pending"; "delete_actor|s2|done" ]

[<Fact>]
let ``the upgrade takes a six-kind outbox through both blocks to ten kinds`` () =
    checkRecapUpgrade sixKindOutbox [ "refresh_profile|s1|pending"; "delete_actor|s2|done" ]

[<Fact>]
let ``a fresh database accepts the recap kinds and the upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! before = kindChecks db
        do! insertRecapKinds db
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
    }

// ---------------------------------------------------------------------------
// Feature 027: cr_episodes.aired_at, filled from the stored pages
// ---------------------------------------------------------------------------

let private user027 = Guid.Parse "00000000-0000-0000-0000-0000000000c1"

/// A page whose entries are (episode id, episode_air_date text or None, with panel)
let private pageOf027 (entries: (string * string option) list) =
    let entry (id: string, airDate: string option) =
        let field =
            airDate |> Option.map (fun d -> $",\"episode_air_date\":\"{d}\"") |> Option.defaultValue ""

        $"""{{"id":"{id}","date_played":"2026-01-01T00:00:00Z","parent_id":"SER1","playhead":10,"fully_watched":true,"panel":{{"id":"{id}","type":"episode","episode_metadata":{{"series_id":"SER1","duration_ms":1400000{field}}}}}}}"""

    $"""{{"data":[{String.Join(",", entries |> List.map entry)}]}}"""

let private storePage (db: DbConfig) (importId: Guid) (receivedAt: string) (capturedAt: string) (body: string) =
    task {
        do!
            Db.execute
                db
                $"""INSERT INTO cr_imports (id, user_id, received_at, status, page_count, entries_received)
                    VALUES ('{importId}', '{user027}', '{receivedAt}', 'completed', 1, 1);
                    INSERT INTO cr_import_pages (import_id, page_index, url, captured_at, body)
                    VALUES ('{importId}', 0, 'https://x/content/v2/a/watch-history', '{capturedAt}', '{body}'::json)"""
    }

/// A database from before feature 027: no `aired_at`, three catalog episodes, three stored pages. E1 is on two pages
/// (the newer capture says 2026-04-01, the older 2026-01-01, and the newer one was received first), E2 has a date
/// that is not one, E3 has none, E4 is a page-less stub, E5 is in the page with a time zone offset.
let private databaseBefore027 () : Task<DbConfig> =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db "ALTER TABLE cr_episodes DROP COLUMN aired_at"

        do! Db.execute db $"INSERT INTO users (id, username, display_name) VALUES ('{user027}', 'dates', 'dates')"

        for id in [ "E1"; "E2"; "E3"; "E4"; "E5" ] do
            do!
                Db.execute
                    db
                    $"INSERT INTO cr_episodes (id, series_id, duration_ms, source_at) VALUES ('{id}', 'SER1', 1400000, now())"

        do!
            storePage
                db
                (Guid.Parse "00000000-0000-0000-0000-0000000000d1")
                "2026-06-01T00:00:00Z"
                "2026-06-01T00:00:00Z"
                (pageOf027 [ "E1", Some "2026-01-01T00:00:00Z"; "E2", Some "not a date"; "E3", None ])

        do!
            storePage
                db
                (Guid.Parse "00000000-0000-0000-0000-0000000000d2")
                "2026-05-01T00:00:00Z"
                "2026-09-01T00:00:00Z"
                (pageOf027 [ "E1", Some "2026-04-01T00:00:00Z"; "E5", Some "2026-10-04T15:30:00+02:00" ])

        return db
    }

let private airedAt (db: DbConfig) =
    Db.dump
        db
        "SELECT id || '|' || coalesce(to_char(aired_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI'), '<null>') FROM cr_episodes ORDER BY id"

let private airedAtColumn (db: DbConfig) =
    Db.dump
        db
        "SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_name = 'cr_episodes' AND column_name = 'aired_at'"

[<Fact>]
let ``a catalog without aired_at gets the column and the dates from the stored pages, newest capture wins`` () =
    task {
        let! db = databaseBefore027 ()
        let! column = airedAtColumn db
        Assert.Empty column

        do! initializeSchema db

        let! column = airedAtColumn db
        Assert.Equal<string list>([ "timestamp with time zone|YES" ], column)

        let! dates = airedAt db

        Assert.Equal<string list>(
            [ "E1|2026-04-01T00:00" // the newer capture wins although the older page was received later
              "E2|<null>" // not a date
              "E3|<null>" // no date in the page
              "E4|<null>" // in no page at all
              "E5|2026-10-04T13:30" ], // read as an instant
            dates
        )
    }

[<Fact>]
let ``the second start changes nothing, even when a later page would give another date`` () =
    task {
        let! db = databaseBefore027 ()
        do! initializeSchema db
        let! first = airedAt db

        // a page stored after the upgrade is not read by it: only the importer sets dates from then on
        do!
            storePage
                db
                (Guid.Parse "00000000-0000-0000-0000-0000000000d3")
                "2026-12-01T00:00:00Z"
                "2026-12-01T00:00:00Z"
                (pageOf027 [ "E1", Some "2026-11-11T00:00:00Z"; "E3", Some "2026-03-03T00:00:00Z" ])

        do! initializeSchema db
        do! initializeSchema db
        let! second = airedAt db
        Assert.Equal<string list>(first, second)
    }

[<Fact>]
let ``a fresh database has the column and the upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! column = airedAtColumn db
        Assert.Equal<string list>([ "timestamp with time zone|YES" ], column)
        do! initializeSchema db
        let! again = airedAtColumn db
        Assert.Equal<string list>(column, again)
    }

[<Fact>]
let ``copies starting together on a catalog without aired_at all succeed and date it once`` () =
    task {
        let! db = databaseBefore027 ()
        let! results = Task.WhenAll [| for _ in 1..6 -> initializeSchema db |]
        Assert.Equal(6, results.Length)
        let! dates = airedAt db
        Assert.Contains("E1|2026-04-01T00:00", dates)
    }

// ---------------------------------------------------------------------------
// Feature 028: comments.remote_thread and the two new outbox kinds
// ---------------------------------------------------------------------------

let private user028 = Guid.Parse "00000000-0000-0000-0000-0000000000e1"

/// A database from before feature 028: `comments` without `remote_thread`, the outbox with the old kind check
/// (but already allowing `failed`), one local comment and one outbox row
let private databaseBefore028 () : Task<DbConfig> =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        do! Db.execute db "ALTER TABLE comments DROP COLUMN remote_thread"

        do!
            Db.execute
                db
                """
                ALTER TABLE fediverse_outbox DROP CONSTRAINT fediverse_outbox_kind_check;
                ALTER TABLE fediverse_outbox
                    ADD CONSTRAINT fediverse_outbox_kind_check
                    CHECK (kind IN ('ensure_thread', 'publish_comment', 'update_comment', 'delete_comment', 'delete_actor', 'refresh_profile'))
                """

        do! Db.execute db $"INSERT INTO users (id, username, display_name) VALUES ('{user028}', 'before28', 'before28')"
        do! Db.execute db "INSERT INTO cr_episodes (id, series_id, duration_ms, source_at) VALUES ('EP28', 'SER28', 1400000, now())"

        do!
            Db.execute
                db
                $"INSERT INTO comments (id, cr_episode_id, author_user_id, content) VALUES ('c28', 'EP28', '{user028}', 'hello')"

        do! Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('refresh_profile', 's28')"
        return db
    }

let private remoteThreadColumn (db: DbConfig) =
    Db.dump
        db
        "SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_name = 'comments' AND column_name = 'remote_thread'"

let private enqueueFollow (db: DbConfig) =
    Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('follow_series', 'SER28@other.test'), ('unfollow_series', 'SER28@other.test')"

[<Fact>]
let ``the 027 shape refuses the follow kinds and has no remote_thread column`` () =
    task {
        let! db = databaseBefore028 ()
        let! column = remoteThreadColumn db
        Assert.Empty column
        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> enqueueFollow db :> Task)
        Assert.Equal("23514", error.SqlState)
    }

[<Fact>]
let ``the 028 upgrade adds the column and the kinds, keeps the rows, and the second run changes nothing`` () =
    task {
        let! db = databaseBefore028 ()
        do! initializeSchema db

        let! column = remoteThreadColumn db
        Assert.Equal<string list>([ "text|YES" ], column)

        do! enqueueFollow db
        let! error = Assert.ThrowsAnyAsync<PostgresException>(fun () -> Db.execute db "INSERT INTO fediverse_outbox (kind, subject) VALUES ('nonsense', 'x')" :> Task)
        Assert.Equal("23514", error.SqlState)

        let! rows = Db.dump db "SELECT kind || '|' || subject FROM fediverse_outbox ORDER BY id"
        Assert.Equal<string list>([ "refresh_profile|s28"; "follow_series|SER28@other.test"; "unfollow_series|SER28@other.test" ], rows)

        let! comment = Db.dump db "SELECT id || '|' || content || '|' || coalesce(remote_thread, '<null>') FROM comments"
        Assert.Equal<string list>([ "c28|hello|<null>" ], comment)

        let! before = kindChecks db
        do! initializeSchema db
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
        Assert.Equal(1, after.Length)
        Assert.Contains("fediverse_outbox_kind_check", after[0])
    }

[<Fact>]
let ``a fresh database has the 028 tables and kinds and the upgrade leaves it alone`` () =
    task {
        let! db = newEmptyDatabase ()
        do! initializeSchema db
        let! before = kindChecks db
        do! enqueueFollow db
        do! Db.execute db "INSERT INTO instance_follows (domain) VALUES ('other.test')"

        do!
            Db.execute
                db
                "INSERT INTO series_follows (series_id, domain, remote_actor, source) VALUES ('SER28', 'other.test', 'https://other.test/ap/actors/series-SER28', 'series')"

        let! error =
            Assert.ThrowsAnyAsync<PostgresException>(fun () ->
                Db.execute
                    db
                    "INSERT INTO series_follows (series_id, domain, remote_actor, source, state) VALUES ('SER29', 'other.test', 'https://other.test/ap/actors/series-SER29', 'series', 'bogus')"
                :> Task)

        Assert.Equal("23514", error.SqlState)
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
    }

[<Fact>]
let ``copies starting together on a database without the 028 changes all succeed`` () =
    task {
        let! db = databaseBefore028 ()
        let! results = Task.WhenAll [| for _ in 1..6 -> initializeSchema db |]
        Assert.Equal(6, results.Length)
        let! column = remoteThreadColumn db
        Assert.Equal<string list>([ "text|YES" ], column)
        do! enqueueFollow db
    }

// A database as 0.6.1 left it (the ten-kind check of features 028 watch updates and 030), with watch-update and
// recap items in the outbox, meets the instance-follows block: every block writes the full list, so the rows stay
// and the follow kinds are accepted (a block writing only its own kinds would refuse the existing rows)
[<Fact>]
let ``a ten-kind outbox with watch update and recap items takes the follow kinds and keeps every row`` () =
    task {
        let! db = databaseBefore028 ()

        do!
            Db.execute
                db
                """
                ALTER TABLE fediverse_outbox DROP CONSTRAINT fediverse_outbox_kind_check;
                ALTER TABLE fediverse_outbox
                    ADD CONSTRAINT fediverse_outbox_kind_check
                    CHECK (kind IN ('ensure_thread', 'publish_comment', 'update_comment', 'delete_comment', 'delete_actor',
                                    'refresh_profile', 'post_watch_update', 'delete_watch_updates', 'post_recap', 'delete_recaps'));
                INSERT INTO fediverse_outbox (kind, subject) VALUES ('post_watch_update', 'u1'), ('post_recap', 'u1');
                """

        do! initializeSchema db
        do! enqueueFollow db

        let! rows = Db.dump db "SELECT kind || '|' || subject FROM fediverse_outbox ORDER BY id"

        Assert.Equal<string list>(
            [ "refresh_profile|s28"
              "post_watch_update|u1"
              "post_recap|u1"
              "follow_series|SER28@other.test"
              "unfollow_series|SER28@other.test" ],
            rows
        )

        let! before = kindChecks db
        Assert.Equal(1, before.Length)
        Assert.Contains("post_recap", before[0])
        Assert.Contains("follow_series", before[0])
        do! initializeSchema db
        let! after = kindChecks db
        Assert.Equal<string list>(before, after)
    }
