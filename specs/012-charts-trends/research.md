# Research: Charts and Trends

## R1. Where the code lives

- **`Infrastructure/Charts.fs`** (new). It is shared by three slices: the charts page, the anime page
  and the episode page.
  - `minPeople (config: IConfiguration) : int`: reads `Frunchy:Charts:MinPeople`; default 3, anything
    below 2 becomes 2.
  - `seasonOf (now: DateTimeOffset)`: returns `(name, year, start)`. The quarter's first day is UTC
    00:00, and the name is Winter, Spring, Summer or Fall.
  - `chart db window minPeople now viewer : Task<Chart>`.
  - `seriesPeople db seriesId minPeople : Task<int option>` (all time, all users).
  - `episodePeople db episodeIds minPeople : Task<int option>`.
- **`Handlers/Charts/Charts.fs`** (new): one slice with three routes, `GET /charts`,
  `/charts/season` and `/charts/all`, sharing one view.
- **Anime and episode pages**: each gets one line. The anime page calls `Charts.seriesPeople`. The
  episode page calls `Charts.episodePeople` with the group ids it already computes (feature 009).
- **Time**: `TimeProvider`, as the DNA page uses, so tests control "now".

## R2. The chart query (one statement per window)

```sql
WITH win AS (
  SELECT w.user_id, e.series_id, e.id AS episode_id
  FROM watch_events w JOIN cr_episodes e ON e.id = w.cr_episode_id
  WHERE e.duration_ms IS NOT NULL
    AND (@from IS NULL OR w.watched_at >= @from) AND w.watched_at < @to
),
ranked AS (
  SELECT series_id, count(DISTINCT user_id) AS people, count(*) AS episodes
  FROM win GROUP BY series_id
  HAVING count(DISTINCT user_id) >= @min
)
SELECT r.series_id, r.people, r.episodes, <title>, <slug>, <poster>,
       EXISTS (SELECT 1 FROM win v WHERE v.series_id = r.series_id AND v.user_id = @viewer) AS mine
FROM ranked r LEFT JOIN cr_series s ON s.id = r.series_id
ORDER BY r.people DESC, r.episodes DESC, <title>, r.series_id
LIMIT 20
```

- **Title**: the DNA rule, `coalesce(cr_series.title, min(cr_episodes.series_title), series_id)`.
- **Poster**: the anime page's rule (feature 006), computed for the 20 rows only, in a lateral
  subquery.
- **`mine`**: whether the viewer watched the series at all, at any time, not only in the window.
  The link goes to their anime page, which shows all their history. `@viewer` is NULL when the
  visitor is signed out.
- **Week movement**: the same statement with `@from = now - 14 days` and `@to = now - 7 days`, and
  no `LIMIT`, so a series that ranked 25th last week still has a rank. The result is a map from
  series to previous rank. A row missing from the map is new.
- **Trend**: one statement for the up to 20 charted series. For each, it counts distinct people per
  7-day block `k = 0..7` (block `k` covers `[now - 7(k+1) days, now - 7k days)`), with
  `generate_series` so empty weeks are 0.
  - The minimum filters which *series* appear, not the weekly values: a charted series already
    passed it this week.
  - A weekly value below the minimum is still shown, as a point on the line. Since it's a count of
    people for a series with at least the minimum overall, it identifies nobody.

## R3. Index

`CREATE INDEX IF NOT EXISTS ix_watch_events_watched ON watch_events (watched_at)`, in
`initializeSchema` and `SCHEMA.md`. The week and season charts read a window of `watch_events`.
Without the index they scan the whole table.

## R4. Performance (SC-003)

- **Load**: 10k users × 200 episodes is 2M `watch_events`. A week window touches only the recent
  rows, through the index. The all-time chart aggregates everything, which takes about a second on
  2M rows on a laptop.
- **Plan**: no cache in this feature. Measure all-time against 1 s in the test, and record the
  measurement in the validation log.
- **If all-time misses**: precomputing it is a later decision, and it would need a Complexity
  Tracking justification (constitution II).
- `// simplified:` the test seeds 2M rows with one `INSERT … SELECT generate_series`, not imports.

## R5. Season names

| Quarter | Season |
|---|---|
| Jan–Mar | Winter |
| Apr–Jun | Spring |
| Jul–Sep | Summer |
| Oct–Dec | Fall |

This is MyAnimeList's convention, which the site already shows for anime. The names are localized
through the same texts the DNA page uses for seasons (`DnaSeason`), so check whether a shared
season-name text exists before adding one.

## R6. The trend line (no library)

- **Shape**: an inline `<svg viewBox="0 0 80 24">` per row, with one `<polyline>` of 8 points in
  `stroke="currentColor"` inside a `text-primary` span. A fixed scale per row (that row's own
  maximum) shows the shape, not a comparison between rows.
- **Accessibility**: `role="img"` and `aria-label` = the text equivalent (`t.ChartTrendLabel`
  values, oldest first).
- **Order**: oldest week on the left.

## R7. Look

- **Design**: Night Broadcast through `impeccable`.
- **Rows**:
  - the rank in IBM Plex Mono;
  - the poster as a small rounded thumbnail;
  - the title;
  - "N people · M episodes";
  - a solid amber `toneBar` with people as a share of the first row;
  - movement as an arrow icon plus a number, using the existing Material Symbols subset (add
    `TrendingUp`/`TrendingDown`-style cases to `Theme.Icon` only if needed, and keep the icon
    subset test green). Up is amber, down is `on-surface-variant`, new is a lilac "New" pill.
- **Tabs**: three links with `aria-current="page"` on the active one, like the year picker.
- **Navigation**: a "Charts" link in the header for everyone, next to the sign-in or account links,
  as a `btnSm Ghost`.
