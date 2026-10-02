module FrunchyBackend.Domains.Identity

open System

/// Core user identity
type User =
    { id: Guid
      username: string
      email: string
      created_at: DateTime }

/// User profile and settings
type UserProfile =
    { user_id: Guid
      display_name: string
      bio: string option
      avatar_url: string option
      updated_at: DateTime }

/// Configuration per user
type UserConfig =
    { user_id: Guid
      auto_sync_enabled: bool
      sync_interval_minutes: int
      updated_at: DateTime }

