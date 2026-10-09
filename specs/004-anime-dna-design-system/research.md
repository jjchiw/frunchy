# Research: Anime DNA Profile and Shared Design System

Decisions for feature 004. Figures were prototyped on the owner's real database (`frunchydb`,
user `jjchiw`, 2026-10-08) before deciding.

## R1. Styling base: Tailwind CDN + design tokens, no DaisyUI

- **Decision**: keep the Tailwind CDN script the layout already loads and drop DaisyUI.
  - **Tokens**: the design tokens are CSS custom properties in one `:root` block, named like the
    extension's `src/styles/tokens.css` (`--color-*`, `--text-<style>-size|line|weight|tracking`,
    `--radius-*`, `--space-*`).
  - **Tailwind config**: `tailwind.config` maps utilities onto those variables, so classes such as
    `bg-surface-container-low`, `text-primary` and `rounded-lg` resolve to tokens.
  - **Components**: a short `<style type="text/tailwindcss">` defines the shared components with
    `@apply`. The tokens, config and component CSS all live in one F# module, `Views/Theme.fs`.
- **Rationale**:
  - There's no frontend build step (constitution, "UI").
  - Only one naming system exists, the extension's.
  - The mockups in `design/` are already Tailwind with the same token names, so translating a
    mockup is mostly copying classes.
- **Alternatives considered**:
  - **A custom DaisyUI theme**: DaisyUI's own variable names (`--p`, `--b1`…) would make a second
    naming system next to the extension's, and its component look fights the mockups.
  - **Hand-written CSS only**: more CSS to write and review, and the mockup classes couldn't be
    reused.
  - **A Tailwind build (CLI or PostCSS)**: the constitution rules out a build step.

## R2. Fonts and icons

- **Decision**:
  - **Font**: Plus Jakarta Sans (weights 400/600/700/800) from Google Fonts with `display=swap`,
    falling back to `system-ui`.
  - **Icons**: Material Symbols Outlined, loaded with Google Fonts' `icon_names=` subset so only the
    icons used are downloaded.
- **Rationale**:
  - The site already loads everything from CDNs.
  - The extension bundles its fonts only because of its packaging, so the sharing happens through
    the names and the files don't need to be shared.
- **Alternatives considered**: self-hosting the fonts (needs static files, which the site doesn't
  serve today); SVG icons inline (more markup for each icon).

## R3. Shared components in `Views/Components.fs`

- **Decision**: one module of small view functions that every page uses:
  - `metricTile`, `card`, `pill`, `genreBar`
  - `button` (primary, secondary, ghost, destructive), `providerButton`
  - `field` (label, input, hint, error), `alert` (success, error, info)
  - `emptyState`, `processing`, `confirmLayout`

  Pages keep their `data-*` test hooks: components take them as parameters or pages add them.
- **Rationale**: constitution III puts UI shared by 2 or more slices in `Views/`, and FR-020 asks
  for one shared set.
- **Alternatives considered**: per-page markup (drifts, which is exactly the problem FR-018–020
  address).

## R4. Dub deduplication key: the original season, plus the episode number

- **Decision**: a watched episode's dedup key is `(coalesce(match.shared_from, season_id),
  episode_number)`. Episodes without a number keep their own id. The row with the larger minutes
  watched wins.
- **Rationale**:
  - A dub row records the original season it follows (`shared_from`, feature 003).
  - Measured on `jjchiw`: this key merges **73** real sub/dub pairs.
  - Keying on the MyAnimeList entry instead merged **107**. The extra 34 were distinct episodes of
    shows whose seasons share one entry (for example, seasons that restart their numbering under a
    single MyAnimeList entry).
- **Alternatives considered**: the MyAnimeList id plus the episode number (wrong merges, see
  above); no dedup (double counts dubs, against FR-005).

## R5. Minutes and totals

- **Decision**:
  - **Minutes per episode**: the whole duration when fully watched, otherwise
    `least(playhead × 1000, duration)`. Only episodes with a duration count.
  - **MyAnimeList-based figures** use the deduplicated minutes (R4).
  - **Totals** (hours and days, FR-012) reuse the dashboard's exact query **without** dedup, so
    the two pages always agree. Measured: 2,367.6 h on the dashboard, 2,339.4 h deduplicated.
- **Rationale**: FR-003, FR-005 and FR-012 together. The dashboard's figures are the public
  reference and stay as they are.

## R6. Genre shares

- **Decision**:
  - **Shares**: each genre's minutes ÷ the sum of all genres' minutes (an episode counts in each of
    its genres). The top 5 are shown, plus "Others" = the remainder, so the 6 rows add up to 100%.
    Ties are ordered by name.
  - **Explicit genres** are excluded by MyAnimeList genre id: Hentai 12, Ecchi 9, Erotica 49.
  - **Top anime per genre**: the Crunchyroll series with the most minutes in that genre.
- **Rationale**:
  - FR-007.
  - The dashboard's "Top genres" (episodes, overlapping shares) is unchanged, and the DNA page says
    in its subtitle that it counts watch time.
  - Measured on `jjchiw`: Action 21.9%, Fantasy 21.7%, Adventure 13.8%, Comedy 11.6%, Drama 7.2%.

## R7. Studio, decade, format

- **Studio**:
  - Every studio credited on the anime counts (`mal_anime_tags` kind `studio`), and its share is
    over matched minutes.
  - Co-productions mean shares can add up to more than 100%. Only the top 2 are shown, so this never
    shows.
  - Measured: Toei Animation 6.8%, MAPPA 4.1%.
- **Decade**:
  - The decade comes from `mal_anime.year`, falling back to the year of `aired_from`.
  - Its share is over the minutes whose year is known, and that set is also the figure's coverage.
  - Measured: 2020s 80.7%.
- **Format**:
  - `mal_anime.type` as MyAnimeList names it (TV, Movie, OVA, ONA, Special, TV Special, Music…).
  - Shares are over matched minutes, largest first, and a row with no type is left out.
  - Measured: TV 97.0%, ONA 2.2%, Movie 0.8%.

## R8. Viewing pace

- **Decision**:
  - **Window**: `now` comes from the app's `TimeProvider` (testable), and the window is
    `[now − 90 days, now]`.
  - **Episodes**: fully watched, deduplicated episodes whose `watched_at` falls in the window.
  - **Pace**: episodes ÷ 90, with 1 decimal.
  - **Weeks**: the window has 13 weeks, counted back from `now`. "Steady" when 9 or more of them
    contain a watched episode, "in bursts" otherwise.
  - **Threshold**: under 10 episodes means "not enough recent data".
- **Rationale**: FR-006. Counting back from `now` avoids calendar-week edges, and day boundaries
  don't matter for a per-day average over 90 days.
- **Measured** on `jjchiw`: 268 episodes → 2.98 per day, 13 of 13 weeks → steady.

## R9. Insignia anime

- **Decision**:
  - **Which series**: the Crunchyroll series with the most deduplicated minutes. Ties go to the
    title.
  - **Title**: `cr_series.title`, falling back to the episodes' series title. Feature 003 learned
    that the `cr_series` row can be missing (T044), so every join on it is a LEFT JOIN.
  - **Poster**: the MyAnimeList image of that series' most-watched matched season, otherwise the
    newest episode thumbnail.
  - **Count**: its fully watched episodes, deduplicated.
- **Measured**: One Piece, 98.1 h, 245 episodes.

## R10. One request, one round trip

- **Decision**: the slice reads everything in one `NpgsqlBatch`:
  - the user, and the totals (the dashboard query);
  - a `dna` CTE with deduplicated minutes, followed by one statement per figure;
  - pace.

  There's no new table and no cache (FR-028).
- **Rationale**: the whole set took **334 ms** in `psql` on `jjchiw` (about 6,000 episodes),
  inside SC-002's 1 second. The plan re-measures it on the slice.
- **Alternatives considered**: a materialized table refreshed after imports (a rebuild path for no
  measured pain, against constitution II).

## R11. Copy link

- **Decision**: an Alpine component with a button that calls
  `navigator.clipboard.writeText(location.href)` and shows "Link copied" for 2 s (with
  `aria-live`).
  - **Fallback**: when the Clipboard API is unavailable (plain http on a non-localhost host), a
    read-only input with the address is shown and selected.
- **Rationale**: FR-013. Nothing is generated or sent anywhere.

## R12. Rolling out the redesign without waiting on mockups

- **Decision**: the order is:
  1. tokens and components;
  2. layout (navigation, footer, wordmark);
  3. the DNA page;
  4. the dashboard;
  5. the remaining pages.

  For each page, the mockup is used if it's in `design/`; otherwise the page is built from the
  components and added to the revisit list in `quickstart.md`. Each step goes through impeccable:
  shape → build → audit.
- **Rationale**: FR-021. The designer works in parallel (`design/BRIEF.md`).

## R13. Keeping behaviour identical

- **Decision**:
  - Tests address markup only through `data-*` hooks (`data-stat`, `data-value`,
    `data-recent-item`, `data-genre`, `data-provider`…), never through classes. Every existing hook
    is kept, and the list is a contract (`contracts/design-system.md`).
  - Routes, handlers, forms and SQL outside the DNA slice are untouched; only view functions change.
  - New tests check that the layout no longer loads DaisyUI, that the tokens match the extension's,
    and that the text/background token pairs meet AA contrast.
- **Rationale**: FR-022 and SC-003. The existing suite is the behaviour baseline.

## R14. Token parity and contrast as tests

- **Decision**:
  - **Parity**: a unit test parses `../browser-extension/src/styles/tokens.css` and compares every
    name both sides define. The test is skipped, with a reason, when the sibling repository isn't
    checked out.
  - **Contrast**: a unit test computes the WCAG contrast ratio for each text/background token pair
    the components use, and requires at least 4.5 for body text and at least 3 for large text and
    UI borders.
- **Rationale**: SC-004 and SC-005 become automatic rather than by-eye.

## R15. Constitution wording

- **Decision**: a PATCH amendment (1.1.0 → 1.1.1). In "UI", "Tailwind/DaisyUI loaded from CDNs"
  becomes "Tailwind, fonts and icons loaded from CDNs". The no-build-step rule is unchanged.
- **Rationale**: the constitution must stay true to the code (Governance). This is a clarification,
  not a new rule.

## R16. `DESIGN.md` contradictions

- **Decision**: the YAML token values win: primary `#4cd7f6`, secondary `#c0c1ff`, radius
  `DEFAULT` 0.5rem and `lg` 1rem. The prose is corrected to match, and the mockups' local Tailwind
  configs are treated as drafts.
- **Rationale**: the spec (FR-018), the extension and the brief already use the YAML values.

## R17. Mockup review (2026-10-08)

The designer delivered every screen of the brief. Decisions made by the owner:
- **Radius**: `lg` stays 1rem, as in the YAML and the extension. The mockups' 0.5rem configs are
  drafts.
- **Logo**: the clapperboard-and-bars mark, redrawn as `design/brand/frunchy-mark.svg` in the
  tokens.
- **Pace**: the 9-of-13-weeks rule stays. The mockup's "% active days" isn't used.
- **DNA minimum**: 15 fully watched episodes, taken from the mockup's cold-start state.
- **Tone**: the plain copy of `Views/Texts.fs`. The mockups' "telemetry" wording, and their false
  claims ("zero-knowledge", "end-to-end", "webhook"), don't ship.

`design/REVIEW.md` maps each page to its mockup, with what to keep and what to drop.
