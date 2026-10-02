module FrunchyBackend.Domains.Catalog

/// Crunchyroll series, keyed by the entries' parent_id (franchise movies belong to it)
type CrSeries =
    { Id: string
      Title: string option
      Slug: string option }

/// Crunchyroll season; dub variants are separate seasons of the same series
type CrSeason =
    { Id: string
      SeriesId: string
      SeasonNumber: int option
      DisplayNumber: string option
      Title: string option
      AudioLocale: string option }

/// Crunchyroll episode. DurationMs = None means unresolved (no details received yet).
type CrEpisode =
    { Id: string
      SeriesId: string
      SeasonId: string option
      SeriesTitle: string option
      Title: string option
      EpisodeNumber: int option
      EpisodeLabel: string option
      DurationMs: int option
      ThumbnailUrl: string option }
