# Design direction: Progress (frunchy_designer, 2026-11-06)

Every hook in contracts/hooks.md is kept. No new tokens, no new icons.

## Answers

1. **100+ unfinished**: compact rows, not tiles, grouped by the year of the last watch (the 027 timeline pattern):
   each year a `<details>`, the newest group open, an open group shows at most 10 rows then "Show all N". With 10 or
   fewer unfinished shows: no grouping, just the rows.
2. **Counts**: one card, three figures in a row, one segmented bar under them (not three dashboard tiles: the numbers
   are parts of one whole, and the proportion is the information).
3. **Next episode**: a `btnSm Secondary` per row with a trailing `arrow_forward` icon (`open_in_new` when it goes to
   Crunchyroll, plus `target=_blank` and `rel=noopener`, as "Watch on Crunchyroll" does). Not a pill (pills are tags).
4. **Order**: Watching, then Unfinished, then Completed. The counts card follows the lifecycle left to right:
   Completed, Watching, Unfinished.

## Page (owner)

a) **Profile top + tabs** (022), Progress the fourth tab and current. At 360 four tabs don't fit in either language, so
   the bar scrolls; the fourth label must be visibly cut at the right edge (the cut is the scroll cue). On load, one
   line of progressive enhancement:
   `document.querySelector('[data-profile-tabs] [aria-current]')?.scrollIntoView({inline:'nearest',block:'nearest'})`.
   Without JS the tab is still reachable by scrolling.
b) **h1** "Progress" at headline-lg plus the intro, like the DNA page.
c) **Counts card** (`fr-card`):
   - `grid-cols-3 gap-4`; each cell the number FIRST (`text-stat-counter`, mono), the label under it
     (`text-label-md font-normal on-surface-variant`, not an uppercase overline), so the numbers share a baseline even
     when "Sin terminar" wraps at 360.
   - Number colours: Completed `text-primary`, Watching `text-tertiary`, Unfinished `text-on-surface-variant`
     (neutral, never rose or coral: it is not a failure).
   - Under the cells the bar: `div h-2 rounded-full overflow-hidden flex gap-0.5`, `aria-hidden`; three solid segments
     with widths in %: `bg-primary`, `bg-tertiary`, `bg-outline`. No legend needed.
   - The unmatched note under the card in the coverage-note style (info icon, `text-label-md`), `data-progress-unmatched`.
d) **Watching** (`data-progress-list="watching"`): header h2 headline-md with the count beside it
   (`text-label-md on-surface-variant`, `items-baseline gap-2`), a hint line, then the rows. Empty: one line.
e) **Unfinished**: same header and hint; `data-progress-list="unfinished"` on ONE wrapper around all the year groups.
   Group summary stacked like the final 027 rows: the year as h3 headline-md, then the count line "37 shows" with the
   `expand_more` chevron.
f) **Recently completed** (`data-progress-list="completed"`): the 027 tiles (auto-fill `minmax(6.5rem,1fr)`,
   `grid-cols-3` on phones), the same quiet empty slot; tile lines: title, "12 ep.", and the relative time
   (`data-entry-last`) on its own line. A reward shelf, visually different from the actionable rows.

## The row (Watching and Unfinished)

`li` rows with `divide-y divide-outline-variant/40`, `py-4`; layout `flex flex-col gap-3 sm:flex-row sm:items-center
sm:gap-4`.
- Left (`flex-1 min-w-0 flex items-start gap-3`): poster `w-10 aspect-[2/3] rounded-sm` (027's quiet empty slot);
  a column with the title (the series page link, `text-body-md font-semibold`, `line-clamp-2`), then a meta line
  (`text-label-md font-normal on-surface-variant`): "4 of 12 episodes · last watched 3 months ago". When the total is
  known and it isn't a long runner, a track under the meta: `fr-track h-1 max-w-48 mt-1.5`, amber fill at
  watched/total.
- Right: the next-episode button, `shrink-0`. At 360 it sits under the meta, indented to the text column with
  `ml-[3.25rem]` (poster 2.5rem + gap 0.75rem), not full width. Visible text "Next: episode 5", then
  `span.sr-only ", {title}"` so 100 links don't share one name.
- Long runner: meta "266 episodes · last watched …", then pill `TertiaryTone` "Long run". No track, never "of 1,155"
  (`data-entry-total` may stay in the DOM; only the visible text drops it).
- Airing with no total: "4 episodes · last watched …" and pill `Muted` "Airing" (says why there's no "of").

## States

- **All caught up** (unfinished 0): keep the Unfinished h2; under it a line with `check_circle` in `text-primary`.
- **Nothing started**: `data-progress-empty` replaces the counts and lists (owner and visitor lines below).
- **Visitor** (`data-progress="visitor"`): the counts card with a line inside it under the bar (lock icon + "Only
  {name} can see which shows these are."), then the unmatched note if any. Nothing else: no section headings.

## Copy, en / es

| Key | English | Spanish |
|---|---|---|
| Tab and h1 | Progress | Progreso |
| Intro | The shows this profile completed, is watching and left unfinished. Each MyAnimeList entry is one show. | Las series que este perfil completó, está viendo y dejó sin terminar. Cada entrada de MyAnimeList es una serie. |
| Count labels | Completed / Watching / Unfinished | Completadas / Viendo / Sin terminar |
| Visitor line | Only {name} can see which shows these are. | Solo {name} puede ver qué series son. |
| Unmatched | {n} started Crunchyroll seasons have no MyAnimeList details yet, so they aren't counted. (1: 1 started Crunchyroll season has no MyAnimeList details yet, so it isn't counted.) | {n} temporadas de Crunchyroll empezadas aún no tienen datos de MyAnimeList, así que no se cuentan. (1: 1 temporada de Crunchyroll empezada aún no tiene datos de MyAnimeList, así que no se cuenta.) |
| Watching hint | Watched in the last 30 days, or keeping up with a show that's airing. | Vistas en los últimos 30 días, o al día con una serie en emisión. |
| Watching empty | Nothing in progress right now. | Nada en curso ahora mismo. |
| Unfinished hint | Started, not completed, and not watched in the last 30 days. Most recent first. | Empezadas, sin completar y sin verse en los últimos 30 días. Las más recientes primero. |
| Group count | {n} shows (1 show) | {n} series (1 serie) |
| Show all | Show all {n} | Ver las {n} |
| Completed heading | Recently completed | Completadas hace poco |
| Completed line (only when n > 12) | The 12 most recent of {n}. | Las 12 más recientes de {n}. |
| Meta | {w} of {t} episodes / {w} episodes / last watched {time} | {w} de {t} episodios / {w} episodios / visto por última vez {time} |
| Pills | Long run / Airing | Larga duración / En emisión |
| Next | Next: episode {n} | Siguiente: episodio {n} |
| All caught up | You're all caught up: every show you started is completed or in progress. | Vas al día: cada serie que empezaste está completada o en curso. |
| Owner empty | Nothing started yet. When a sync brings a fully watched episode of a show with MyAnimeList details, it appears here. | Aún no hay nada empezado. Cuando una sincronización traiga un episodio visto entero de una serie con datos de MyAnimeList, aparecerá aquí. |
| Visitor empty | {name} hasn't started a show yet. | {name} aún no ha empezado ninguna serie. |

## Screenshots for the review (1280 and 360, en and es)

The owner with 100+ unfinished; the owner all caught up; the owner with nothing started; the visitor; one crop with a
long-runner row, an airing row without a total and a row without a poster; the tab bar at 360 on the Progress page.

## Second look (2026-11-06)

Approved once two fixes land; the tab-scroll selector (`nav.fr-tabs [aria-current]`) and leaving out the Completed
section at 0 are accepted.

1. **Meta line**: rows and tiles show the relative time only ("last watched 3 months ago"); the absolute date goes
   in the `<time>` element's `title` (a "relative-only" mode of the time element if Layout's Intl script always
   writes both). The meta becomes two fixed parts: wrapper `flex flex-col sm:flex-row sm:flex-wrap sm:items-center
   sm:gap-x-2`; part one "3 of 12 episodes" plus the Airing/Long run pill; a middot `span` with `hidden sm:inline`,
   `aria-hidden`; part two "last watched …". Two clean lines at 360, one line at 1280.
2. **Completed tiles**: solved by 1 (title, "12 ep.", "2 months ago").
3. **Counts bar**: Unfinished is the track, not a segment: the bar's background is `bg-surface-container-highest`
   and only Completed (`bg-primary`) and Watching (`bg-tertiary`) are drawn, as % of all started shows. The
   Unfinished number keeps `text-on-surface-variant`. Caught up fills the bar.
4. Nested "Show all N": keep. 5. Scroll cue: fine. 6. Long runner in a closed year: correct, no change.

## Sign-off (2026-11-06)

Signed off at fd15a35: the counts bar on a quiet track, rows one line at 1280 and two clean lines at 360 (es), the
next-episode button under them on phones. Nothing left from design.
