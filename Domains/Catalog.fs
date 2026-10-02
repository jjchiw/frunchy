module FrunchyBackend.Domains.Catalog

open System

/// Anime catalog entry
/// Stores metadata as JSONB for flexibility
type Anime =
    { id: Guid
      mal_id: int64 // MyAnimeList ID for external linking
      title: string
      metadata: string // JSON blob with full anime data from Jikan
      created_at: DateTime
      updated_at: DateTime }

/// Genre information
type Genre =
    { id: Guid
      name: string
      mal_id: int option
      created_at: DateTime }

/// Studio information
type Studio =
    { id: Guid
      name: string
      mal_id: int option
      created_at: DateTime }

/// Season information
type Season =
    { id: Guid
      anime_id: Guid
      season_number: int
      episode_count: int
      created_at: DateTime }

