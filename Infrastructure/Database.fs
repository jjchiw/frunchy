module FrunchyBackend.Infrastructure.Database

open System.Threading.Tasks
open Npgsql

type DbConfig =
    { connectionString: string }

/// Raised when the database still has the pre-001 placeholder layout, which never held real data
exception LegacySchemaException of string

/// Get a database connection from the connection string
let getConnection (config: DbConfig) : NpgsqlConnection =
    new NpgsqlConnection(config.connectionString)

let private legacyLayoutQuery =
    """
    SELECT EXISTS (SELECT 1 FROM information_schema.tables
                   WHERE table_schema = current_schema() AND table_name = 'import_records')
        OR EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = current_schema() AND table_name = 'users' AND column_name = 'email')
        OR EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = current_schema() AND table_name = 'cr_imports' AND column_name = 'payload')
    """

// See specs/001-crunchyroll-history-import/data-model.md and SCHEMA.md
let private schema =
    """
    -- Identity
    CREATE TABLE IF NOT EXISTS users (
        id                  uuid PRIMARY KEY,
        username            text NOT NULL UNIQUE,
        display_name        text NOT NULL,
        sync_key_hash       text NOT NULL UNIQUE,
        sync_key_created_at timestamptz NOT NULL,
        cr_account_id       text NULL,
        cr_profile_id       text NULL,
        created_at          timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT ck_users_profile_needs_account CHECK (cr_profile_id IS NULL OR cr_account_id IS NOT NULL)
    );

    -- One Crunchyroll history owner belongs to at most one user
    CREATE UNIQUE INDEX IF NOT EXISTS ux_users_history_owner
        ON users (cr_account_id, coalesce(cr_profile_id, ''))
        WHERE cr_account_id IS NOT NULL;

    -- Tracking: submissions (raw pages are never modified; deleted only with their user)
    CREATE TABLE IF NOT EXISTS cr_imports (
        id                    uuid PRIMARY KEY,
        user_id               uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        received_at           timestamptz NOT NULL DEFAULT now(),
        exported_at           timestamptz NULL,
        status                text NOT NULL CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
        processing_started_at timestamptz NULL,
        completed_at          timestamptz NULL,
        page_count            int NOT NULL,
        entries_received      int NOT NULL,
        processed             int NOT NULL DEFAULT 0,
        unresolved            int NOT NULL DEFAULT 0,
        unsupported           int NOT NULL DEFAULT 0,
        failed                int NOT NULL DEFAULT 0,
        error                 text NULL
    );

    CREATE INDEX IF NOT EXISTS ix_cr_imports_status_received ON cr_imports (status, received_at);
    CREATE INDEX IF NOT EXISTS ix_cr_imports_user_received ON cr_imports (user_id, received_at DESC);

    CREATE TABLE IF NOT EXISTS cr_import_pages (
        import_id   uuid NOT NULL REFERENCES cr_imports(id) ON DELETE CASCADE,
        page_index  int NOT NULL,
        url         text NOT NULL,
        captured_at timestamptz NULL,
        body        json NOT NULL, -- json (not jsonb): the exact text received
        PRIMARY KEY (import_id, page_index)
    );

    CREATE TABLE IF NOT EXISTS cr_import_entry_issues (
        import_id     uuid NOT NULL REFERENCES cr_imports(id) ON DELETE CASCADE,
        page_index    int NOT NULL,
        entry_index   int NOT NULL,
        cr_episode_id text NULL,
        outcome       text NOT NULL CHECK (outcome IN ('unresolved', 'unsupported', 'failed')),
        reason        text NOT NULL,
        PRIMARY KEY (import_id, page_index, entry_index)
    );

    -- Catalog: shared Crunchyroll metadata, no personal data
    CREATE TABLE IF NOT EXISTS cr_series (
        id        text PRIMARY KEY,
        title     text NULL,
        slug      text NULL,
        source_at timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS cr_seasons (
        id             text PRIMARY KEY,
        series_id      text NOT NULL,
        season_number  int NULL,
        display_number text NULL,
        title          text NULL,
        audio_locale   text NULL,
        source_at      timestamptz NOT NULL
    );

    CREATE TABLE IF NOT EXISTS cr_episodes (
        id             text PRIMARY KEY,
        series_id      text NOT NULL,
        season_id      text NULL,
        series_title   text NULL,
        title          text NULL,
        episode_number int NULL,
        episode_label  text NULL,
        duration_ms    int NULL, -- NULL = unresolved (no details received yet)
        thumbnail_url  text NULL,
        source_at      timestamptz NULL
    );

    -- Derived ledger: one row per user and episode, newest watch state wins
    CREATE TABLE IF NOT EXISTS watch_events (
        user_id       uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        cr_episode_id text NOT NULL REFERENCES cr_episodes(id),
        watched_at    timestamptz NOT NULL,
        playhead_s    int NOT NULL,
        fully_watched boolean NOT NULL,
        import_id     uuid NOT NULL,
        updated_at    timestamptz NOT NULL DEFAULT now(),
        PRIMARY KEY (user_id, cr_episode_id)
    );

    CREATE INDEX IF NOT EXISTS ix_watch_events_user_watched ON watch_events (user_id, watched_at DESC);

    -- Accounts (feature 002): a signed-up user has no sync key until "Connect extension"
    ALTER TABLE users ALTER COLUMN sync_key_hash DROP NOT NULL;
    ALTER TABLE users ALTER COLUMN sync_key_created_at DROP NOT NULL;

    -- One provider identity belongs to at most one user (shape of ASP.NET Core Identity's user logins)
    CREATE TABLE IF NOT EXISTS user_identities (
        provider         text NOT NULL,
        provider_user_id text NOT NULL,
        user_id          uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        linked_at        timestamptz NOT NULL DEFAULT now(),
        last_used_at     timestamptz NOT NULL DEFAULT now(),
        PRIMARY KEY (provider, provider_user_id)
    );

    CREATE INDEX IF NOT EXISTS ix_user_identities_user ON user_identities (user_id);

    -- One-time sign-up invites (only the code's hash is stored); not personal data
    CREATE TABLE IF NOT EXISTS invites (
        id              uuid PRIMARY KEY,
        code_hash       text NOT NULL UNIQUE,
        created_at      timestamptz NOT NULL DEFAULT now(),
        expires_at      timestamptz NOT NULL,
        revoked_at      timestamptz NULL,
        used_at         timestamptz NULL,
        used_by_user_id uuid NULL REFERENCES users(id) ON DELETE SET NULL
    );

    -- Account security events shown to the user (no IP or device data)
    CREATE TABLE IF NOT EXISTS security_events (
        id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        occurred_at timestamptz NOT NULL DEFAULT now(),
        event       text NOT NULL,
        provider    text NULL,
        actor       text NOT NULL CHECK (actor IN ('user', 'operator'))
    );

    CREATE INDEX IF NOT EXISTS ix_security_events_user ON security_events (user_id, occurred_at DESC);

    -- MyAnimeList enrichment (feature 003): shared catalog data, nothing references users.
    -- raw columns hold the provider's responses verbatim (source says which provider); everything
    -- else is re-derivable (mal-rebuild).
    CREATE INDEX IF NOT EXISTS ix_cr_episodes_season ON cr_episodes (season_id);

    CREATE TABLE IF NOT EXISTS mal_anime (
        mal_id               int PRIMARY KEY,
        url                  text NOT NULL,
        title                text NOT NULL,
        title_english        text NULL,
        title_japanese       text NULL,
        synonyms             text[] NOT NULL DEFAULT '{}',
        type                 text NULL,
        episodes             int NULL,
        status               text NULL,
        airing               boolean NOT NULL DEFAULT false,
        aired_from           date NULL,
        year                 int NULL,
        season               text NULL,
        score                numeric(4,2) NULL, -- NULL = no score (a missing or 0 score)
        image_url            text NULL,
        raw                  json NOT NULL,
        source               text NOT NULL DEFAULT 'jikan', -- provider of raw: mal-api | jikan
        data_hash            text NOT NULL,
        etag                 text NULL,
        fetched_at           timestamptz NOT NULL,
        checked_at           timestamptz NOT NULL,
        missing_since        timestamptz NULL,
        refresh_attempts     int NOT NULL DEFAULT 0,
        refresh_next_at      timestamptz NULL,
        refresh_claimed_at   timestamptz NULL,
        refresh_requested_at timestamptz NULL,
        refresh_error        text NULL
    );

    CREATE INDEX IF NOT EXISTS ix_mal_anime_checked ON mal_anime (checked_at);

    CREATE TABLE IF NOT EXISTS mal_tags (
        kind   text NOT NULL CHECK (kind IN ('genre', 'explicit_genre', 'theme', 'demographic', 'studio')),
        tag_id int NOT NULL,
        name   text NOT NULL,
        PRIMARY KEY (kind, tag_id)
    );

    CREATE TABLE IF NOT EXISTS mal_anime_tags (
        mal_id   int NOT NULL REFERENCES mal_anime(mal_id) ON DELETE CASCADE,
        kind     text NOT NULL,
        tag_id   int NOT NULL,
        position int NOT NULL,
        PRIMARY KEY (mal_id, kind, tag_id),
        FOREIGN KEY (kind, tag_id) REFERENCES mal_tags(kind, tag_id)
    );

    -- related_id has no FK: the related anime may not be held yet
    CREATE TABLE IF NOT EXISTS mal_relations (
        mal_id       int NOT NULL REFERENCES mal_anime(mal_id) ON DELETE CASCADE,
        relation     text NOT NULL,
        related_id   int NOT NULL,
        related_name text NOT NULL,
        PRIMARY KEY (mal_id, relation, related_id)
    );

    CREATE TABLE IF NOT EXISTS mal_searches (
        query       text PRIMARY KEY,
        raw         json NOT NULL,
        source      text NOT NULL DEFAULT 'jikan', -- provider of raw: mal-api | jikan
        searched_at timestamptz NOT NULL
    );

    -- Databases created before the provider switch (feature 003 amendment)
    ALTER TABLE mal_anime ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'jikan';
    ALTER TABLE mal_searches ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'jikan';

    -- One row per Crunchyroll season: its MyAnimeList match and its place in the enrichment queue
    CREATE TABLE IF NOT EXISTS cr_season_matches (
        season_id       text PRIMARY KEY REFERENCES cr_seasons(id) ON DELETE CASCADE,
        status          text NOT NULL CHECK (status IN ('pending', 'matched', 'ambiguous', 'no_match', 'failed')),
        mal_id          int NULL REFERENCES mal_anime(mal_id),
        method          text NULL CHECK (method IN ('search', 'link', 'dub', 'manual')),
        shared_from     text NULL REFERENCES cr_seasons(id),
        candidates      jsonb NULL,
        reason          text NULL,
        title_seen      text NULL,
        decided_at      timestamptz NULL,
        attempts        int NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NULL,
        claimed_at      timestamptz NULL,
        requested_at    timestamptz NULL,
        last_error      text NULL,
        created_at      timestamptz NOT NULL DEFAULT now(),
        CHECK ((status = 'matched') = (mal_id IS NOT NULL)),
        CHECK (method IS DISTINCT FROM 'dub' OR shared_from IS NOT NULL)
    );

    CREATE INDEX IF NOT EXISTS ix_cr_season_matches_queue ON cr_season_matches (status, next_attempt_at);
    """

/// Create the schema if missing (idempotent). Fails fast on the legacy placeholder layout.
let initializeSchema (config: DbConfig) : Task<unit> =
    task {
        use connection = getConnection config
        do! connection.OpenAsync()

        use legacyCmd = new NpgsqlCommand(legacyLayoutQuery, connection)
        let! legacy = legacyCmd.ExecuteScalarAsync()

        if unbox<bool> legacy then
            raise (
                LegacySchemaException
                    "The database has the legacy pre-001 schema (placeholder tables, never real data). Reset the dev database and start again: dropdb frunchydb && createdb frunchydb (locally: docker exec postgres-postgres-1 dropdb -U postgres frunchydb, then createdb), see README Getting Started"
            )

        use cmd = new NpgsqlCommand(schema, connection)
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }
