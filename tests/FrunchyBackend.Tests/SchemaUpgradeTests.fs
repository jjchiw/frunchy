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
