module FrunchyBackend.Domains.Tracking

open System
open FrunchyBackend.Domains.Catalog

/// Raw import from Crunchyroll extension
/// Never delete this - it's the audit trail
type CrunchyrollImport =
    { id: Guid
      user_id: Guid
      payload: string // JSON blob stored as string
      created_at: DateTime }

/// Normalized watch event
/// This is the core domain asset
type WatchEvent =
    { id: Guid
      user_id: Guid
      anime_id: Guid
      episode: int
      watched_at: DateTime
      source: string // "crunchyroll", "import", etc.
      created_at: DateTime }

/// Import status for background processing
type ImportStatus =
    | Pending
    | Processing
    | Completed
    | Failed of string

type ImportRecord =
    { cr_import_id: Guid
      user_id: Guid
      status: ImportStatus
      processed_watch_events: int
      error_message: string option
      updated_at: DateTime }

