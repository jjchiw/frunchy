module FrunchyBackend.Domains.Analytics

open System
open FrunchyBackend.Domains.Catalog

/// Projected genre statistics per user
type UserGenreStats =
    { user_id: System.Guid
      genre_id: System.Guid
      watch_count: int
      total_hours: float
      last_watched: DateTime }

/// Anime consumption snapshot
type AnimeStats =
    { user_id: System.Guid
      anime_id: System.Guid
      episodes_watched: int
      hours_watched: float
      last_episode_watched: int
      last_watched_at: DateTime
      completion_percentage: float }

/// Overall user statistics
type UserDNA =
    { user_id: System.Guid
      total_anime_watched: int
      total_episodes: int
      total_hours: float
      top_genre: string option
      updated_at: DateTime }

