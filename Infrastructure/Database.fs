module FrunchyBackend.Infrastructure.Database

open System
open Npgsql
open NpgsqlTypes

type DbConfig =
    { connectionString: string }

/// Initialize database schema
let initializeSchema (config: DbConfig) : Async<unit> = async {
    use connection = new NpgsqlConnection(config.connectionString)
    do! connection.OpenAsync() |> Async.AwaitTask

    let createTablesCommand = """
        -- Identity Domain
        CREATE TABLE IF NOT EXISTS users (
            id UUID PRIMARY KEY,
            username VARCHAR(255) NOT NULL UNIQUE,
            email VARCHAR(255) NOT NULL UNIQUE,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS user_profiles (
            user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            display_name VARCHAR(255),
            bio TEXT,
            avatar_url TEXT,
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS user_config (
            user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            auto_sync_enabled BOOLEAN DEFAULT FALSE,
            sync_interval_minutes INT DEFAULT 60,
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        -- Catalog Domain
        CREATE TABLE IF NOT EXISTS anime (
            id UUID PRIMARY KEY,
            mal_id BIGINT NOT NULL,
            title VARCHAR(500) NOT NULL,
            metadata JSONB NOT NULL,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE INDEX IF NOT EXISTS idx_anime_mal_id ON anime(mal_id);

        CREATE TABLE IF NOT EXISTS genres (
            id UUID PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE,
            mal_id INT,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS studios (
            id UUID PRIMARY KEY,
            name VARCHAR(255) NOT NULL UNIQUE,
            mal_id INT,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS seasons (
            id UUID PRIMARY KEY,
            anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
            season_number INT NOT NULL,
            episode_count INT NOT NULL,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        -- Tracking Domain (Core)
        CREATE TABLE IF NOT EXISTS cr_imports (
            id UUID PRIMARY KEY,
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            payload JSONB NOT NULL,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE INDEX IF NOT EXISTS idx_cr_imports_user_id ON cr_imports(user_id);
        CREATE INDEX IF NOT EXISTS idx_cr_imports_created_at ON cr_imports(created_at);

        CREATE TABLE IF NOT EXISTS watch_events (
            id UUID PRIMARY KEY,
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
            episode INT NOT NULL,
            watched_at TIMESTAMP NOT NULL,
            source VARCHAR(50) NOT NULL,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE INDEX IF NOT EXISTS idx_watch_events_user_id ON watch_events(user_id);
        CREATE INDEX IF NOT EXISTS idx_watch_events_anime_id ON watch_events(anime_id);
        CREATE INDEX IF NOT EXISTS idx_watch_events_watched_at ON watch_events(watched_at);
        CREATE INDEX IF NOT EXISTS idx_watch_events_created_at ON watch_events(created_at);

        CREATE TABLE IF NOT EXISTS import_records (
            cr_import_id UUID PRIMARY KEY REFERENCES cr_imports(id) ON DELETE CASCADE,
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            status VARCHAR(50) NOT NULL DEFAULT 'Pending',
            processed_watch_events INT DEFAULT 0,
            error_message TEXT,
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE INDEX IF NOT EXISTS idx_import_records_user_id ON import_records(user_id);
        CREATE INDEX IF NOT EXISTS idx_import_records_status ON import_records(status);

        -- Analytics Domain (Projections)
        CREATE TABLE IF NOT EXISTS user_genre_stats (
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            genre_id UUID NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
            watch_count INT NOT NULL DEFAULT 0,
            total_hours FLOAT NOT NULL DEFAULT 0,
            last_watched TIMESTAMP,
            PRIMARY KEY (user_id, genre_id)
        );

        CREATE TABLE IF NOT EXISTS anime_stats (
            user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
            episodes_watched INT NOT NULL DEFAULT 0,
            hours_watched FLOAT NOT NULL DEFAULT 0,
            last_episode_watched INT,
            last_watched_at TIMESTAMP,
            completion_percentage FLOAT DEFAULT 0,
            PRIMARY KEY (user_id, anime_id)
        );

        CREATE TABLE IF NOT EXISTS user_dna (
            user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            total_anime_watched INT DEFAULT 0,
            total_episodes INT DEFAULT 0,
            total_hours FLOAT DEFAULT 0,
            top_genre VARCHAR(255),
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """

    use cmd = new NpgsqlCommand(createTablesCommand, connection)
    do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
    do! connection.CloseAsync() |> Async.AwaitTask
}

/// Get a database connection from the connection string
let getConnection (config: DbConfig) : NpgsqlConnection =
    new NpgsqlConnection(config.connectionString)

