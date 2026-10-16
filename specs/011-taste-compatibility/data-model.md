# Data Model: Taste Compatibility

There are no new tables or columns. Everything is computed per request from:
- `watch_events` and `cr_episodes`, `cr_seasons`, `cr_series`;
- `cr_season_matches` and `mal_anime`, `mal_anime_tags`, `mal_tags`.

Computed shapes (in `Infrastructure/Compatibility.fs`):

```text
Person      = { Username; DisplayName; FullyWatched: int64; Series: Map<seriesId, SeriesFacts>;
                GenreMs: Map<genre, int64>; StudioMs: Map<studio, int64 * Set<seriesId>>;
                MatchedMs: int64; DedupMs: int64 }
SeriesFacts = { Title; Ms: int64; FullyWatched: int64 }
Comparison  = { Viewer: Person; Owner: Person; Enough: Enough (Both | ViewerShort | OwnerShort | BothShort);
                Score: float option; Level: int option; Shared: SharedSeries list; SharedGenres: string list;
                GenreRows: GenreRow list; Theirs: TheirSeries list; Studios: SharedStudio list }
```

**Rebuilds**: there's nothing to rebuild. The result follows the stored history and matches, so
`user-rebuild` and `mal-rebuild` keep it consistent automatically.
