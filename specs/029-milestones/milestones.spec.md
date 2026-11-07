# Feature Specification: Milestones on the dashboard

**Feature Branch**: `029-milestones`

**Created**: 2026-11-06

**Status**: Draft

**Input**: the follow-up task "Badges and milestones" (`.alta/tasks/2026-10-20-badges-and-milestones-pkmc.md`): a light,
fun layer on the existing history that gives users something to share. A small fixed set of milestones computed from
`watch_events` and the catalog, shown on the dashboard; decide whether they're computed per request or stored.

## Clarifications

### Session 2026-11-06 (decided by the main session)

- Q: Computed per request or stored? → A: **Per request**, like every other figure on the dashboard. A milestone is a
  pure function of the user's `watch_events` rows and the catalog, so storing it would add a table, a backfill and a
  second truth that `user-rebuild`, re-sent imports and reordered imports would have to keep equal (the
  order-independence and idempotency invariants). Computing it keeps those invariants for free, needs no schema
  change, and lets the thresholds change later without a migration. Cost: one statement per dashboard view returning
  one compact row per watch event of known length (a few thousand rows for the largest histories), folded by a pure
  F# function.
- Q: Which milestones? → A: Six **tracks**, each with fixed ascending thresholds (tiers). A track's reached tier is the
  highest threshold its value meets; the next tier is the first one above it.

  | Track key | Value | Thresholds |
  |---|---|---|
  | `episodes` | fully watched episodes of known length (the dashboard's "Episodes" figure) | 10, 50, 100, 250, 500, 1000, 2500, 5000 |
  | `hours` | hours watched (the dashboard's "Hours" figure: fully watched length, else the playhead capped at the length) | 24, 100, 250, 500, 1000, 2500 |
  | `series` | series with a fully watched episode (the dashboard's "Series" figure) | 5, 10, 25, 50, 100, 250 |
  | `streak` | longest run of consecutive UTC days with a fully watched episode | 3, 7, 14, 30, 100, 365 |
  | `marathon` | most fully watched episodes in one UTC day | 6, 12, 24, 48 |
  | `long-run` | most fully watched episodes of one series, a dub and its original counted once (DNA key) | 24, 50, 100, 250, 500, 1000 |

  The first three repeat the dashboard's own figures on purpose, so a badge never disagrees with the tile above it
  (a test pins the values equal). No MyAnimeList data: milestones work without enrichment.
- Q: The date a tier was reached? → A: Shown, because it is what makes a milestone worth sharing ("1,000 episodes,
  reached 12 Mar 2025"). It is derived from `watched_at`, which holds an episode's **latest** watch (the only one
  stored, as on the year page), so a rewatch can move a date later; the page says nothing about it, the year page
  has the same rule. Ordering is by `(watched_at, episode id)`, so the date is deterministic.
  - `episodes`: the `watched_at` of the N-th fully watched episode.
  - `hours`: the `watched_at` of the row whose running sum of watched milliseconds first reaches N hours.
  - `series`: the `watched_at` at which the N-th distinct series got its first fully watched episode.
  - `streak`: the UTC date of the N-th day of the first run that reaches N days.
  - `marathon`: the first UTC date with N fully watched episodes, at the `watched_at` of that day's N-th one.
  - `long-run`: per series, the dedup keys ordered by their earliest fully watched `watched_at`; the date is the
    earliest, over all series, at which some series reached N keys.
- Q: Which series does `long-run` name? → A: The series with the most keys, then the title, then the series id. The card
  links it with `Components.seriesLink`. Its date is the date of the reached tier (any series), not necessarily of the
  named series; with one series dominating they coincide, and the card shows "N episodes of <title>" for the value
  and the tier on its own line, so nothing claims the named series reached it first.
- Q: Who sees them? → A: Everyone who can see the dashboard: the section sits behind the existing visibility gate
  (features 017 and 021) and adds no rule of its own. The milestones query runs only after the gate let the viewer
  in, and only when the dashboard has figures (no section, no query, for an empty history).
- Q: Where on the page? → A: A "Milestones" section right after the four figure tiles, before the recent list and top
  genres: a heading with "N of M reached" and a grid of six cards (one per track, always in the table's order). Each
  card: the track name, the reached tier in words ("1,000 episodes") or "Not yet", the date reached (a
  `<time data-fr-time>` like the rest of the page), and progress to the next tier (a bar and "234 to go to 2,500
  episodes"), or "Every milestone reached" at the top tier.
- Q: Federation, sharing, notifications? → A: Out of scope: no post when a tier is reached, no badge image, no stored
  "seen" state. The dashboard's URL is what people share.
- Q: The DNA and year pages? → A: Unchanged.

## Requirements *(mandatory)*

- **FR-001**: `Infrastructure/Milestones.fs` (after `Seasons.fs`, before `Charts.fs` or anywhere after `Database.fs` in
  the Infrastructure section): the track definitions (key, thresholds), a row type
  `WatchRow = { WatchedAt: DateTime; EpisodeId: string; FullyWatched: bool; WatchedMs: int64; SeriesId: string;
  SeriesTitle: string; DedupKey: string }`, a result type per track (key, value, reached threshold option, reached
  date option, next threshold option, and for `long-run` the series id and title), and a **pure**
  `compute (rows: WatchRow list) : Track list` that does every rule of the clarifications. `load connection userId`
  runs **one** literal SQL statement with the parameter `@user_id` (the guardrail test) over
  `watch_events JOIN cr_episodes` (`duration_ms IS NOT NULL`) `LEFT JOIN cr_series`, `LEFT JOIN cr_season_matches`
  for `shared_from` (any status: the DNA key uses `coalesce(m.shared_from, e.season_id)`; check how `Dna.fs` /
  `Fediverse.fs` build the key and match it), and returns `compute` of its rows.
- **FR-002**: `WatchedMs` is the dashboard's rule: `duration_ms` when fully watched, else
  `least(playhead_s * 1000, duration_ms)`. Every count except `hours` uses fully watched rows only. Hours compare in
  milliseconds (`threshold * 3_600_000`); the hours value shown is the dashboard's rounding (one decimal), the tier
  test uses the exact milliseconds.
- **FR-003**: `Handlers/Users/Dashboard.fs`: `DashboardModel` gets `Milestones: Milestones.Track list` (empty when
  `Figures` is `None`; the query runs only when `Figures` is `Some`, inside `tryLoad` after the gate). The view renders
  the section described above between `stats` and the recent/top-genres grid.
- **FR-004**: Hooks (tests read only these; `contracts/hooks.md`):
  - `section[data-milestones]` with `data-milestones-reached` (the tiers reached, summed over the six tracks) and
    `aria-labelledby` its heading (the total was removed, see "Endless tiers");
  - each card `[data-milestone="<track key>"]` with `data-value` (invariant: an integer, `hours` with one decimal),
    `data-tier` (the reached threshold, `""` when none), `data-next` (the next threshold, always present);
  - the reached date `<time … data-fr-time data-milestone-reached>` (absent when no tier);
  - the progress bar inside `[data-milestone-progress]` (always present; the top state was removed);
  - on the `long-run` card, `data-long-run-series="<series id>"` on the card when the value is above 0, and the series
    link through `seriesLink` (its `data-series-link`).
- **FR-005**: Copy in `Views/Texts.fs`, `en` and `es`, no literal in the view: section title ("Milestones" /
  "Hitos"), the "N of M reached" line, the six track names, the six tier phrasings with culture-formatted numbers
  (e.g. "1,000 episodes" / "1.000 episodios", "24 hours" / "24 horas", "25 series" / "25 series", "7 days in a row" /
  "7 días seguidos", "12 episodes in one day" / "12 episodios en un día", "100 episodes of one series" / "100
  episodios de una serie"), "Not yet" / "Aún no", "Reached" / "Conseguido", the "N to go to <tier>" line, "Every
  milestone reached" / "Todos los hitos conseguidos", and the long-run value line "N episodes of <title>" (title as
  an element, so the view composes it). Singular forms where a number can be 1 (`streak` value 1 day, `long-run`
  1 episode, `marathon` 1 episode).
- **FR-006**: Design: Night Broadcast (`design/night_broadcast/DESIGN.md`), existing components only (`fr-card`,
  `shareBar`, `pill`, `icon`, `Layout.timeElement`); a track icon each from `Theme.Icon` (add cases if needed, e.g.
  `EmojiEvents`, `Movie`, `Bolt`; adding a case adds it to the font subset automatically). Amber (`primary`) for the
  reached tier and the bar, muted text for "Not yet". UI work goes through the `impeccable` skill's craft floor (no
  eyebrow labels, no emoji characters, plain copy). Responsive: one column on phones, 2 from `sm`, 3 from `lg`.
- **FR-007**: No schema change, no new configuration, no change to `/api/*`, the import contract, the extension or any
  design token.
- **FR-008**: Docs: CLAUDE.md (one line under the dashboard route), `changelog.d/029-milestones.md` (Added).

## Success Criteria *(mandatory)*

- **SC-001**: A user's dashboard shows six milestone cards whose `episodes`, `hours` and `series` values equal the
  dashboard's figures, with tiers and dates following the rules above.
- **SC-002**: Re-sending, reordering or rebuilding a history gives byte-identical milestone sections.
- **SC-003**: A hidden profile still answers the unknown-user 404 and runs no milestone query; an empty history shows
  no section.

## Endless tiers (2026-11-07)

The fixed tiers stay, and after the last one each track continues for ever, so a track always has a next tier and
the "every milestone reached" state (`data-milestone-top`, `Texts.MilestoneTop`) no longer exists:

- `episodes`, `hours`, `series`, `long-run`: the 1 / 2.5 / 5 x 10^n ladder from the last fixed tier (`nextLadder`:
  after 1 x 10^n comes 2.5 x 10^n, then 5 x 10^n, then 10^(n+1)). Episodes: 5,000, 10,000, 25,000, 50,000, 100,000,
  250,000 ...; hours: 2,500, 5,000, 10,000 ...; series: 250, 500, 1,000 ...; long-run: 1,000, 2,500, 5,000 ...
- `streak`: after 365, every further 365 (730, 1,095 ...).
- `marathon`: after 48, every further 24 (72, 96 ...).

Tiers are generated lazily, only up to the first one above the value (`Milestones.thresholds`, `tierAfter`). The
reached tier, its date (the same `reachedAt` logic with the generated threshold), the bar and "N to go to X" work as for
a fixed tier. The header is "N reached" with no total (`Texts.MilestonesReached` takes one number), and
`data-milestones-reached` counts the generated tiers too; `data-milestones-total` and `Milestones.totalTiers` are removed.
`Track.Next` is a plain `int`.
