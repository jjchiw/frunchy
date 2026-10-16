# Contract: charts and people counts

## Routes

| Verb | Route | Who | Result |
|---|---|---|---|
| GET | `/charts` | anyone | the week chart |
| GET | `/charts/season` | anyone | the current season's chart |
| GET | `/charts/all` | anyone | the all-time chart |

**Navigation**: every page's header has `a href="/charts"` with `data-nav="charts"`.

## Test hooks

| Element | Hook |
|---|---|
| Page | `data-charts` = `week`, `season` or `all`; `data-min-people` = the minimum in effect |
| Tabs | `data-chart-tab` = `week`, `season` or `all` (`aria-current="page"` on the active one) |
| Season name | `data-season` = e.g. `fall-2026` |
| A row | `data-chart-row` = series id; `data-rank`; `data-people`; `data-episodes` |
| Viewer's link | `data-chart-mine`, href `/u/{viewer}/anime/{seriesId}` |
| Crunchyroll link | `data-cr-series-link` |
| Movement (week) | `data-move` = `up`, `down`, `same` or `new`; `data-move-by` = integer (absent for `same` and `new`) |
| Trend (week) | `data-trend` = 8 integers, comma separated, oldest first |
| Empty chart | `data-chart-empty` |
| Anime page count | `data-people-count` = integer (absent below the minimum) |
| Episode page count | `data-people-count` = integer (absent below the minimum) |

## Rules

- **Ranking**: people (distinct users with at least one watched episode of known length in the
  window), then episodes, then title, then series id. Up to 20 rows.
- **Minimum**: a row exists only when people ≥ minimum. The same minimum gates the counts on the
  anime and episode pages.
- **No person's identity**: no username, display name or user link appears on the charts, except
  the viewer's own anime-page link.
