# Data Model: Charts and Trends

**New index**: `ix_watch_events_watched ON watch_events (watched_at)`. It goes in `initializeSchema`
and `SCHEMA.md`. No new table or column.

**Computed shapes** (`Infrastructure/Charts.fs`):

```text
Window   = Week | Season | AllTime
ChartRow = { SeriesId; Title; Slug: string option; Poster: string option; People: int; Episodes: int;
             Mine: bool; Move: Move option (Week only); Trend: int list (Week only, 8 values, oldest first) }
Move     = Up of int | Down of int | Same | New
Chart    = { Window; Season: (string * int) option; MinPeople: int; Rows: ChartRow list }
```

**Rebuilds**: there's nothing to rebuild. Charts follow `watch_events`, which `user-rebuild`
reproduces.
