# Design direction: Seasons (frunchy_designer, 2026-10-26)

"Your seasons" becomes a collapsed timeline of season rows with poster tiles (the top season open at the top), the
long run becomes a horizontal strip so it cannot be mistaken for the flagship, and the per-show "Watched while
airing" pill goes, replaced by a per-season count and a small amber mark. Every hook in contracts/hooks.md is kept;
nothing leaves the DOM, it only collapses.

## 1. Long run: a strip, not a second flagship

- Keep the h2 "Long run" (sectionHeading). Under it ONE row: poster w-16 (sm:w-20, aspect 2/3, rounded-md), then a
  min-w-0 column with the title at text-headline-md (not lg: the flagship owns lg) and ONE meta line in
  text-body-md on-surface-variant joined by middots: "37 episodes · 14.6 hours · aired Winter 2023 to Summer 2026".
  Hooks stay (data-long-run-episodes on the episodes span, data-long-run-span on the span span).
- Placement: full width (lg:col-span-12) directly under the flagship + pace row. About 150 px tall. Flagship = tall
  card with pills (your show); long run = one wide line (your marathon). No pills on the strip.
- Flagship absent, long run present: the strip takes the flagship's slot (lg:col-span-7) with the same inner layout,
  pace keeps col-span-5. Only the column class changes.
- Copy: DnaLongRunSpan en "aired {first} to {last}" / one season "aired {first}"; es "emitido de {first} a {last}" /
  "emitido en {first}". Lower-case start (after a middot). Episodes "37 episodes" / "37 episodios"
  (DnaInsigniaEpisodes with None already does this), hours as today.

## 2. Your seasons: top season open, then a timeline of collapsed season rows

Inside the existing card (data-dna-seasons):

a) heading + intro as now.

b) TOP SEASON block, data-top-season: h3 "Winter 2024" in text-headline-lg text-primary, then p text-body-md
   on-surface-variant: "Your busiest season: 26 shows, 24 of them watched while airing." Then the TILE GRID (3) with
   the first 6 shows (most episodes first), and when there are more, a native <details> under it: <summary> "Show all
   26 shows" (btnSm Ghost styling on the summary, -ml-3.5 so the text aligns) containing the remaining tiles in the
   same grid classes. Default closed.

c) TIMELINE: the 8 newest seasons, newest first, each a <details data-season="…" data-season-shows="N"> with
   border-t border-outline-variant/50:
   - <summary class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-3 cursor-pointer list-none">
     (list-none + [&::-webkit-details-marker]:hidden hides the triangle): left h3 text-headline-md "Summer 2026";
     right: span text-label-md on-surface-variant "18 shows · 14 while airing" and a Material expand_more icon (add
     ExpandMore to Theme.Icon) that rotates 180° when open (details[open] > summary .icon { rotate: 180deg },
     transition 160ms under the reduced-motion gate, like .fr-tab).
   - content: the tile grid with ALL the season's shows (no cap), pb-4.
   - The NEWEST listed season carries the open attribute; the other seven start closed. If the top season is also
     among the eight, it still gets its timeline row (closed); both hooks then exist once each.

d) earlier line, data-earlier-seasons, text-label-md: "Plus 18 earlier seasons." / "Y 18 temporadas anteriores."

Expected height: desktop about 250 (top block) + 7 × 52 (closed rows) + one open season; the phone under about
1,200 px. No JavaScript.

## 3. The tile (replaces the poster+text row): data-season-show, data-show-episodes, data-while-airing

<li class="flex flex-col gap-1.5"> poster box w-full aspect-[2/3] rounded-md; then the title through seriesLink
(keeps data-series-link, fr-link) at text-label-md, line-clamp-2, [overflow-wrap:anywhere]; then one line
text-label-md font-normal on-surface-variant: [airing mark] "5 ep.". Grid: ul class
"grid grid-cols-3 gap-3 sm:grid-cols-4 lg:grid-cols-6".

- Missing poster: the box is bg-surface-container with the --border-micro border and nothing inside. No initials,
  mark or icon. Keep alt="" on real posters.
- Episodes copy: DnaShowEpisodes en "5 ep." / "1 ep."; es "5 ep.".

## 4. Watched while airing: drop the pill

- Per show: a small amber mark before the episodes text: Material `sensors` icon (add Sensors to Theme.Icon) at the
  label size, text-primary, aria-hidden, followed by span.sr-only "Watched while airing" / "Visto en emisión". The
  li keeps data-while-airing.
- Per season: the count in the summary line, "14 while airing" / "14 en emisión" (omit the fragment when 0). Top
  season line as in 2b.
- Nothing on the long run or the flagship.

## 5. Coverage wording (taste cards)

- en: "Based on the 8 of 88 watched seasons with MyAnimeList details." es: "Basado en las 8 de 88 temporadas vistas
  con datos de MyAnimeList." Decade card: "…with MyAnimeList details and a known airing year." (es: "…con datos de
  MyAnimeList y año de emisión conocido.")
- Studio card "12.5% · 1 season" fine; genres intro fine.

## 6. Copy, en / es

- DnaSeasonsIntro: keep.
- DnaTopSeasonLine n a: "Your busiest season: {n} shows, {a} of them watched while airing." / "Tu temporada con más
  series: {n} series, {a} de ellas vistas en emisión." (a = 0: drop the clause; n = 1: "1 show" / "1 serie")
- DnaSeasonSummary n a: "{n} shows · {a} while airing" / "{n} series · {a} en emisión"
- DnaShowAll n: "Show all {n} shows" / "Ver las {n} series"
- DnaEarlierSeasons: "Plus {n} earlier seasons." / "Y {n} temporadas anteriores." (1: "Plus 1 earlier season." /
  "Y 1 temporada anterior.")
- DnaWhileAiring (sr-only): "Watched while airing" / "Visto en emisión"
- DnaLongRunTitle stays "Long run" / "Larga duración".

## 7. Drop

The per-show pill, the "N shows" span on the right of an always-open heading (it moves into the summary), the
2-column list layout, the long run's three stacked lines.

## Noted, not 027

The flagship reads "48 of 12 episodes watched" (Eyeshield 21): watched exceeds the MyAnimeList count. Pre-existing;
its own small task.

## Second look (2026-10-26)

1. Tile size: every season tile grid is `grid gap-3 grid-cols-[repeat(auto-fill,minmax(6.5rem,1fr))]` (the fixed 3/4/6
   column classes are dropped), in the top block, its "Show all" content and the timeline rows.
2. Missing poster: the quiet slot uses `surface-container-lowest` with the `--border-micro` border, nothing inside. No
   text-only tiles, no Crunchyroll thumbnails.
3. Long-run text: `DnaEpisodesPlain` ("37 episodes" / "1 episode", es "37 episodios" / "1 episodio") replaces the
   "episodes watched" wording on the strip, so it reads "37 episodes · 14.6 hours · aired Winter 2023 to Summer 2026".
   `data-long-run-episodes` stays on that span with the same value.
4. Open by default means capped: whatever is open by default shows at most 6 tiles. The newest timeline row starts open
   but shows its first 6 tiles, then a nested `<details>` "Show all N shows" (the `DnaShowAll` text and summary styling
   of the top block) with the rest. The other seven rows show everything when opened. `data-season`,
   `data-season-shows` and every show's hooks are kept; a show in the nested details is still in the DOM.
