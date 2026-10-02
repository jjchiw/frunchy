# Database Schema Documentation

## Overview

The source of truth is the `initializeSchema` script in `Infrastructure/Database.fs`, which runs on every startup (`CREATE TABLE IF NOT EXISTS`, no migrations). Keep this document in sync with it.

The schema is organized around four core domains:

1. **Identity** - User management
2. **Tracking** - Watch history and imports
3. **Catalog** - Anime metadata
4. **Analytics** - Projections and statistics

## Identity Domain

### `users`
Core user identity.

```sql
CREATE TABLE users (
    id UUID PRIMARY KEY,
    username VARCHAR(255) NOT NULL UNIQUE,
    email VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

### `user_profiles`
Extended user profile information.

```sql
CREATE TABLE user_profiles (
    user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    display_name VARCHAR(255),
    bio TEXT,
    avatar_url TEXT,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

### `user_config`
User preferences and settings.

```sql
CREATE TABLE user_config (
    user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    auto_sync_enabled BOOLEAN DEFAULT FALSE,
    sync_interval_minutes INT DEFAULT 60,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

## Tracking Domain - CORE

### `cr_imports` ⭐
**CRITICAL TABLE - Never delete records**

Raw imports from the Crunchyroll extension. This is the immutable audit trail and source of truth for what data has been ingested.

```sql
CREATE TABLE cr_imports (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    payload JSONB NOT NULL,  -- Raw JSON from extension
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_cr_imports_user_id ON cr_imports(user_id);
CREATE INDEX idx_cr_imports_created_at ON cr_imports(created_at);
```

**Why JSONB?**
- Flexibility: Different extension versions may have different payloads
- Auditability: Store exactly what was sent
- Reprocessing: Can reprocess with new business logic without data loss

### `watch_events` ⭐⭐
**THE CORE DOMAIN ASSET**

Normalized watch events derived from imports. This table is the foundation of everything else.

```sql
CREATE TABLE watch_events (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
    episode INT NOT NULL,
    watched_at TIMESTAMP NOT NULL,
    source VARCHAR(50) NOT NULL,  -- 'crunchyroll', 'manual', 'import', etc.
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_watch_events_user_id ON watch_events(user_id);
CREATE INDEX idx_watch_events_anime_id ON watch_events(anime_id);
CREATE INDEX idx_watch_events_watched_at ON watch_events(watched_at);
CREATE INDEX idx_watch_events_created_at ON watch_events(created_at);
```

**Why this structure?**
- Simple, immutable events
- Fast queries by user + anime + date
- Foundation for all analytics
- Like a ledger - only append, never modify

### `import_records`
Tracks processing status of imports.

```sql
CREATE TABLE import_records (
    cr_import_id UUID PRIMARY KEY REFERENCES cr_imports(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status VARCHAR(50) NOT NULL DEFAULT 'Pending',  -- Pending, Processing, Completed, Failed
    processed_watch_events INT DEFAULT 0,
    error_message TEXT,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_import_records_user_id ON import_records(user_id);
CREATE INDEX idx_import_records_status ON import_records(status);
```

**Status Flow:**
```
Pending → Processing → Completed
              ↘ Failed (with error_message)
```

## Catalog Domain

### `anime`
Anime catalog entries with flexible metadata storage.

```sql
CREATE TABLE anime (
    id UUID PRIMARY KEY,
    mal_id BIGINT NOT NULL,  -- MyAnimeList ID for external lookups
    title VARCHAR(500) NOT NULL,
    metadata JSONB NOT NULL,  -- Full anime data from Jikan API
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_anime_mal_id ON anime(mal_id);
```

**metadata JSONB example:**
```json
{
  "mal_id": 5114,
  "title": "Fullmetal Alchemist: Brotherhood",
  "title_english": "Fullmetal Alchemist 2009",
  "year": 2009,
  "genres": ["Action", "Adventure", "Drama"],
  "studios": ["Bones"],
  "episodes": 64,
  "status": "Finished Airing",
  "aired": "2009-04-05 to 2010-07-04",
  "score": 9.16,
  "scored_by": 2000000
}
```

### `genres`
Genre reference table.

```sql
CREATE TABLE genres (
    id UUID PRIMARY KEY,
    name VARCHAR(255) NOT NULL UNIQUE,
    mal_id INT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

### `studios`
Studio reference table.

```sql
CREATE TABLE studios (
    id UUID PRIMARY KEY,
    name VARCHAR(255) NOT NULL UNIQUE,
    mal_id INT,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

### `seasons`
Season/season information per anime.

```sql
CREATE TABLE seasons (
    id UUID PRIMARY KEY,
    anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
    season_number INT NOT NULL,
    episode_count INT NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

## Analytics Domain - Projections

These tables store pre-computed statistics for fast dashboard queries.

### `user_genre_stats`
Aggregated genre consumption per user.

```sql
CREATE TABLE user_genre_stats (
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    genre_id UUID NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
    watch_count INT NOT NULL DEFAULT 0,
    total_hours FLOAT NOT NULL DEFAULT 0,
    last_watched TIMESTAMP,
    PRIMARY KEY (user_id, genre_id)
);
```

### `anime_stats`
Anime consumption progress per user.

```sql
CREATE TABLE anime_stats (
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    anime_id UUID NOT NULL REFERENCES anime(id) ON DELETE CASCADE,
    episodes_watched INT NOT NULL DEFAULT 0,
    hours_watched FLOAT NOT NULL DEFAULT 0,
    last_episode_watched INT,
    last_watched_at TIMESTAMP,
    completion_percentage FLOAT DEFAULT 0,
    PRIMARY KEY (user_id, anime_id)
);
```

### `user_dna`
User's anime profile snapshot.

```sql
CREATE TABLE user_dna (
    user_id UUID PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    total_anime_watched INT DEFAULT 0,
    total_episodes INT DEFAULT 0,
    total_hours FLOAT DEFAULT 0,
    top_genre VARCHAR(255),
    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
);
```

## Key Design Principles

### 1. Immutability
- `cr_imports` - never modified or deleted
- `watch_events` - append-only ledger

### 2. Indexing Strategy
- User ID indexes for "get all data for user X"
- Date indexes for time-based queries
- Status indexes for background processing

### 3. Foreign Keys with CASCADE
- Cascading deletes mean deleting a user removes all their data
- Ensures referential integrity

### 4. JSONB for Flexibility
- `cr_imports.payload` - exact extension output
- `anime.metadata` - evolving anime data structure

### 5. Projection Tables
- `user_genre_stats`, `anime_stats`, `user_dna` are computed from `watch_events`
- Can be regenerated if needed
- Optimize for read performance (dashboards)

## Query Examples

### Get user's anime DNA
```sql
SELECT * FROM user_dna WHERE user_id = $1;
```

### Get user's watch history (recent)
```sql
SELECT we.*, a.title, a.metadata->>'title_english' as title_en
FROM watch_events we
JOIN anime a ON we.anime_id = a.id
WHERE we.user_id = $1
ORDER BY we.watched_at DESC
LIMIT 20;
```

### Get top genres for user
```sql
SELECT g.name, ugs.watch_count, ugs.total_hours
FROM user_genre_stats ugs
JOIN genres g ON ugs.genre_id = g.id
WHERE ugs.user_id = $1
ORDER BY ugs.watch_count DESC;
```

### Find pending imports
```sql
SELECT ci.id, ci.user_id, ci.created_at
FROM cr_imports ci
JOIN import_records ir ON ci.id = ir.cr_import_id
WHERE ir.status = 'Pending'
ORDER BY ci.created_at ASC;
```

## Maintenance

### Vacuum & Analyze
Regular maintenance is recommended for large tables:
```sql
VACUUM ANALYZE watch_events;
VACUUM ANALYZE user_genre_stats;
```

### Backup
Always backup `cr_imports` - it's your audit trail.

### Monitoring
Key tables to monitor:
- `watch_events` - grows with user activity
- `cr_imports` - grows with imports
- `user_genre_stats` - fixed size relative to users × genres

