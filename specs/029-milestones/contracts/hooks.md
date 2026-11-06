# Hooks: Milestones on the dashboard (feature 029)

Tests read only these `data-*` hooks, never classes. The section is on `GET /u/{username}` only, after the four figure
tiles and before the recent list; it is absent (no section, no query) when the dashboard has no figures.

| Element | Hook | Value |
|---|---|---|
| the section | `section[data-milestones]` | `aria-labelledby="milestones-title"` |
| | `data-milestones-reached` | tiers reached, summed over the six tracks |
| | `data-milestones-total` | every tier of every track (36) |
| a card (one per track, always in this order: `episodes`, `hours`, `series`, `streak`, `marathon`, `long-run`) | `[data-milestone="<key>"]` | the track key |
| | `data-value` | the value: an integer, `hours` with one decimal (invariant culture) |
| | `data-tier` | the reached threshold, `""` when none |
| | `data-next` | the next threshold, `""` at the top |
| | `data-long-run-series` | `long-run` card only, when the value is above 0: the series id |
| the reached date | `time[data-fr-time][data-milestone-reached]` | absent when no tier is reached |
| progress to the next tier | `[data-milestone-progress]` | holds the bar and the "N to go" line; absent at the top tier |
| the top state | `[data-milestone-top]` | present only at the top tier |
| the named series on `long-run` | `[data-series-link="<series id>"]` | the usual `Components.seriesLink` |

`episodes`, `hours` and `series` values equal the dashboard's tiles (`data-stat` `episodes`, `hours`, `series`).

## Rules in one place

- Tracks and thresholds: `Infrastructure/Milestones.fs` (`tracks`); the copy is `Texts.Milestone*`.
- Dates come from `watched_at`, ordered by `(watched_at, episode id)`. The `streak` date is the first fully watched
  watch of the run's N-th UTC day (an instant, so the viewer's browser localizes it).
- `long-run` dedups a dub and its original with the DNA key and names the series with the most keys, then title, then id.
