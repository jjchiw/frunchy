---

description: "Task list for feature 004: Anime DNA profile and shared design system"
---

# Tasks: Anime DNA Profile and Shared Design System

**Input**: Design documents from `/specs/004-anime-dna-design-system/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: REQUIRED by constitution Principle V.
- **Integration tests**: against a real PostgreSQL (Testcontainers), using the 2026 Crunchyroll
  sample and feature 003's MyAnimeList fixtures (`FakeMal`, `MalFixtures.routeSampleCatalog`,
  `enrichAll`).
- **Unit tests**: for the design system (token parity, contrast, test hooks).
- **The existing suite is the behaviour baseline of the redesign.** It must stay green **without
  editing any assertion**: tests read only `data-*` hooks and decoded text.

**Organization**: tasks are grouped by user story, so each story can be implemented and tested on
its own.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 (DNA page) or US2 (site restyle) from spec.md

## Conventions for every UI task

- **impeccable**: load the `impeccable` skill (`.claude/skills/impeccable/`) and run its shape →
  build → audit steps for the page. Load the `oxpecker` skill before touching views.
- **Texts**: every visible string goes in `Views/Texts.fs`, in `en` **and** `es`. Views contain no
  literals.
- **Test hooks**: keep every `data-*` hook listed in `contracts/design-system.md` on the equivalent
  element. Never rename one.
- **Mockups**: follow the page's mockup in `design/` (mapped in `design/REVIEW.md`, with what to keep
  and what to drop) when it exists. Take layout and components, never copy: texts stay the site's
  plain texts. Otherwise build from
  `Views/Components.fs` and add the page to the quickstart's revisit list.
- **Checks**: at 1440px and at 360px, in English and in Spanish:
  - no clipped or overlapping text, and no horizontal scroll;
  - visible keyboard focus;
  - no motion when "reduce motion" is on.

---

## Phase 1: Setup

- [X] T001 Register the new files in `FrunchyBackend.fsproj`, each created as a minimal module so
  `dotnet build` passes with 0 warnings:
  - `Views/Theme.fs` and `Views/Components.fs` between `Views/Texts.fs` and `Views/Layout.fs`;
  - `Handlers/Users/Dna.fs` right after `Handlers/Users/Dashboard.fs`.
- [X] T002 [P] Register `DesignSystemTests.fs`, `DnaTests.fs` and `DnaPaceTests.fs` in
  `tests/FrunchyBackend.Tests/FrunchyBackend.Tests.fsproj`, after `SampleEnrichmentTests.fs`, as
  empty modules.

---

## Phase 2: Foundational (blocks both stories)

**Purpose**: tokens, components and the layout that every page, the DNA page included, is built on.

- [X] T003 [P] Write `tests/FrunchyBackend.Tests/DesignSystemTests.fs` first (research R13, R14):
  - **Token parity**: parse `../browser-extension/src/styles/tokens.css`, found from the repo root
    by walking up from `AppContext.BaseDirectory`. Every custom property defined on both sides must
    have the same value, `--font-family` excepted. Skip with a reason when the file is absent.
  - **Contrast**: WCAG relative luminance. Require ≥ 4.5 for each body-text pair and ≥ 3 for
    large-text and UI-border pairs:
    - on-surface and on-surface-variant on surface and on every surface container;
    - on-primary on primary;
    - error on surface;
    - on-error-container on error-container;
    - outline on surface (≥ 3).
  - **No DaisyUI**: render `/` and `/signin`; assert no `daisyui` stylesheet is loaded and the page
    loads Plus Jakarta Sans.
  - **Hooks survive**: one rendering test per page asserts that every hook listed in
    `contracts/design-system.md` for that page is still present. Use the existing helpers
    (`AuthHelpers.signedUp`, `connectExtension`, `Browser`) to reach the account pages.

  These tests fail until T004–T006 are done; the parity test may pass already.
- [X] T004 Create `Views/Theme.fs` (research R1, R2, R16):
  - `tokensCss`: a `:root` block with every token of `contracts/design-system.md`:
    - the names and values of the extension's `tokens.css`;
    - plus the website-only type styles from `DESIGN.md`'s YAML (`display-hero`,
      `display-hero-mobile`, `headline-xl`, `headline-xl-mobile`, `headline-lg`, `stat-counter`)
      and `--color-secondary-container`;
    - `color-scheme: dark`.
  - `tailwindConfig`: a `tailwind.config = {…}` script mapping:
    - `colors` to `var(--color-*)` (surface…, primary, on-primary, secondary, tertiary, error…);
    - `borderRadius` to `var(--radius-*)` (`DEFAULT` = `--radius-default`);
    - `fontFamily.sans` to Plus Jakarta Sans;
    - `spacing` extras (`gutter`, `margin`).
  - `componentCss`: a `<style type="text/tailwindcss">` with `@apply` classes for the components of
    T005 (`.fr-card`, `.fr-tile`, `.fr-pill`, `.fr-btn-*`, `.fr-input`, `.fr-alert-*`,
    `.fr-genre-track`, `.fr-genre-fill`, focus rings). Hover and transition effects go inside
    `@media (prefers-reduced-motion: no-preference)`.
  - `fontLinks`: the Google Fonts links for Plus Jakarta Sans (400/600/700/800, `display=swap`),
    JetBrains Mono 400/500 (keys, usernames and URLs only: `--font-mono`, `font-mono`), and the
    Material Symbols Outlined subset (`icon_names=` with only the icons used).
  - `markSvg`: the inline SVG of `design/brand/frunchy-mark.svg`, and `faviconHref`, a data-URI of it.
- [X] T005 Create `Views/Components.fs` (contract "Components"). Each function returns an
  `HtmlElement` and takes the strings it shows (no literals):
  - `card`, `metricTile label name invariantValue display` (keeps `data-stat` and `data-value`),
    `pill`;
  - `genreBar name share label` (keeps `data-genre`);
  - `button` (`Primary | Secondary | Ghost | Destructive`), `linkButton`,
    `providerButton provider label`;
  - `field label name hint error` (`aria-invalid` and `aria-describedby` when there's an error);
  - `alert` (`Success | Error | Info`, with `role`), `emptyState icon title body action`,
    `processing text`, `confirmLayout`;
  - `icon name` (Material Symbol, `aria-hidden`).
- [X] T006 Rewrite `Views/Layout.fs` with the design system:
  - **Head**: drop DaisyUI; keep the Tailwind CDN script, htmx 4.0.0, Alpine 3 and the time script;
    add `Theme.fontLinks`, `Theme.tokensCss`, `Theme.tailwindConfig` and `Theme.componentCss`.
  - **Body**: `bg-surface text-on-surface font-sans`.
  - **Navigation** (dashboard mockup, minus the extras dropped in `design/REVIEW.md`):
    - the mark `design/brand/frunchy-mark.svg` inline (`aria-hidden`) plus the "Frunchy" wordmark,
      instead of the 🎬 emoji, and the same SVG as a data-URI favicon (FR-025);
    - `navEnd` keeps `data-nav` and the sign-out POST form with antiforgery.
  - **Footer**: restyled.
  - **Main**: `max-w-[1360px] mx-auto` with gutters.
  - **`messagePage`**: rebuilt on `Components.emptyState`, keeping its signature so `Program.fs`
    and the handlers don't change.
  - **`timeElement`**: unchanged.
- [X] T007 Run `dotnet test`:
  - every earlier suite passes **with no assertion edited**;
  - `DesignSystemTests` passes for `/`, `/signin` and the layout.

  If a hook went missing, fix the view and not the test.

**Checkpoint**: the site renders on the new base, and behaviour is unchanged.

---

## Phase 3: User Story 1 - My Anime DNA (Priority: P1) 🎯 MVP

**Goal**: `/u/{username}/dna` shows the figures of FR-001–FR-017 from stored data.

**Independent Test**:
- Import the 2026 sample, enrich it with the fixtures, and open the page. Each figure equals a hand
  count, in en and es.
- Empty and unknown users get their states.

### Tests for User Story 1 (REQUIRED, Principle V)

- [X] T008 [P] [US1] Write `tests/FrunchyBackend.Tests/DnaTests.fs` (contract `dna-page.md`).
  Helper: a user `animenerd` with the 2026 sample imported (`submitAccepted`, `processAll`),
  `routeSampleCatalog`, `enrichAll`. Read the page with `readHtml` and parse the `data-*` hooks.
  - **Figures**: each one equals an expected value computed in the test by its own simple SQL over
    `watch_events` / `cr_episodes` / `cr_season_matches` / `mal_anime` / `mal_anime_tags`, written
    independently of the slice:
    - studio top 2;
    - decade and its share;
    - genre top 5 plus `others` adding up to 100.0 (±0.1);
    - each genre's `data-top-series`;
    - format shares;
    - insignia series and episodes.
  - **Totals**: `hours` and `days` equal the dashboard's `data-stat` values for the same user.
  - **Weighting**: with a synthetic page (`Fixtures.syntheticPages`), a 120-minute movie counts five
    times a 24-minute episode in the format split.
  - **Dub dedup**: the sample's dub pairs (e.g. Tamer `GRK5CNJ14`/`GYDQCGK5Q`) count once; a
    partly watched dub plus a fully watched original count once with the larger minutes.
  - **Explicit genres**: insert an explicit genre tag (id 12) on a matched anime; it never appears.
  - **Coverage**:
    - `data-coverage` equals matched ÷ all minutes;
    - one season forced to `no_match` lowers it;
    - with every season unmatched, each MyAnimeList section carries `data-not-enough`, while totals
      and insignia still show.
  - **Ties**: two studios with equal minutes are ordered by name.
  - **Empty**: a user with no history, and one with 14 fully watched episodes, get `data-dna-empty`
    (`data-value` = 0 and 14) and no figure sections; at 15 the figures appear.
  - **Not found**: an unknown username gets 404 and the "User not found" text.
  - **Links**: `/u/{username}` has `data-dna-link` → `/u/{username}/dna`, and the DNA page has
    `data-dashboard-link`.
  - **Localization**: en and es (`Accept-Language: es`) render; the localized labels differ; the
    MyAnimeList names don't.
  - **Stability (FR-016)**: the figures are identical after `user-rebuild` (`RebuildUser`) and
    after re-sending the same submissions.
  - **No external calls**: with the fake service cleared, viewing the page makes no request
    (`app.Mal.Requests` is empty).
- [X] T009 [P] [US1] Write `tests/FrunchyBackend.Tests/DnaPaceTests.fs`. Use an app with a fixed
  `TimeProvider` (`createAppWith`) and synthetic watches at chosen dates:
  - 30 fully watched episodes spread over 9 of the 13 weeks → `data-label="steady"`;
  - the same over 8 weeks → `bursts`;
  - 9 episodes in the window → `data-not-enough`;
  - episodes older than 90 days are ignored;
  - `data-value` equals episodes ÷ 90 with 1 decimal.

### Implementation for User Story 1

- [X] T010 [P] [US1] Add the DNA texts to `Views/Texts.fs`, in the record type and in `en` and
  `es`:
  - page title and intro, and the section titles (studio, decade, pace, insignia, genres, formats,
    totals);
  - runner-up label, "Others", the coverage sentence (a function of %), the weighting note ("by
    watch time");
  - pace unit and labels (steady / in bursts), the not-enough messages (page-level and
    per-section);
  - copy link, link copied, the fallback label;
  - the dashboard ↔ DNA link labels, and decade formatting (a function of year);
  - the empty state: "{n} of 15 fully watched episodes", how to get there, and the link label to the
    home page's how-to-sync;
  - the 90-day chart's accessible label, and the insignia's "{n} of {total} episodes".
- [X] T011 [US1] Implement `Handlers/Users/Dna.fs`, data section (research R4–R10, data-model):
  - **One round trip**: a private `load` reading everything with one `NpgsqlBatch`:
    - user lookup (case-insensitive, as `Dashboard.fs`);
    - totals: a copy of the dashboard's totals query, commented as pinned equal by `DnaTests`;
    - the `dna` CTE (deduplicated minutes keyed on `coalesce(shared_from, season_id)` +
      `episode_number`);
    - then one statement each for: coverage; studios top 2; decade (`year`, else
      `extract(year from aired_from)`); genres (excluding ids 9, 12, 49) with each genre's top
      series; formats; insignia (LEFT JOIN `cr_series`, title fallback to `cr_episodes.series_title`,
      poster from the most-watched matched season's `mal_anime.image_url`, else the newest
      thumbnail, plus its MyAnimeList type, first studio, season/year and `episodes`); pace
      (`@now` from `TimeProvider`), with per-day counts for the 90-day chart; and the decade
      breakdown (every decade with its share).
  - **SQL rules**: every value is passed as a parameter (`SqlGuardrailTests`), and the SQL text
    stays literal.
  - **Result**: a `DnaModel` record with an option per figure (`None` = not enough).
- [X] T012 [US1] Implement `Handlers/Users/Dna.fs`, view, handler and endpoint:
  - **Layout**: follow the DNA mockup (`design/anime_dna_perfil_frunchy/code.html`, real-data
    sections only, brief 5.2), on `Components`.
    - Copy: the site's plain texts only; drop every mockup element listed in `design/REVIEW.md`.
    - Header: display name, the copy-link control (Alpine: `navigator.clipboard.writeText`, an
      `aria-live` confirmation, a read-only input fallback, research R11), the dashboard link.
    - Sections in the contract's order, with its hooks; `genreBar` for genres, formats and the
      decade breakdown; the coverage line per MyAnimeList section; the empty state.
    - Pace: a 90-bar chart of episodes per day (plain divs with heights, `role="img"` and an
      `aria-label` summary, no chart library), per `design/REVIEW.md`.
  - **Handler**: `handler (username: string) : EndpointHandler`. A user that doesn't exist →
    `Layout.messagePage` with UserNotFound and a 404.
  - **Endpoint**: `let endpoint = GET [ routef "/u/{%s}/dna" handler ]`, added to `Program.fs`
    `endpoints` right after `Handlers.Users.Dashboard.endpoint`.
  - **impeccable**: shape → build → audit for this page.
- [X] T013 [US1] Add the DNA link to the dashboard in `Handlers/Users/Dashboard.fs`: a link to
  `/u/{username}/dna` with `data-dna-link`, visible near the header. Only the view changes.
- [X] T014 [US1] Run `DnaTests`, `DnaPaceTests` and the whole suite. Then check the page on the
  real `frunchydb` against the quickstart §2 table:
  - the totals equal `/u/jjchiw`;
  - it's ready in under 1 s (`curl -w '%{time_total}'`).

  Record both in the quickstart's validation log.

**Checkpoint**: the DNA page is complete and tested on its own (MVP).

---

## Phase 4: User Story 2 - The site looks like one product (Priority: P2)

**Goal**: every page uses the design system, with identical behaviour (FR-018–FR-025).

**Independent Test**:
- The whole suite passes with no assertion edited.
- `DesignSystemTests` hooks pass for every page.
- Each page passes an impeccable audit at 1440 and 360px, en and es.

### Tests for User Story 2 (REQUIRED, Principle V)

- [X] T015 [US2] Extend `DesignSystemTests.fs` so its "hooks survive" and "no DaisyUI" checks cover
  every page in the contract's table:
  - `/`, `/signin` (and its error state), `/signup`, `/claim`, `/signin/reconfirm`;
  - `/account`, `/account/key` (confirm and shown), `/account/delete`;
  - `/u/{username}`, a 404, and the rate-limit page;
  - no page may contain DaisyUI component classes: `btn`, `card-body`, `stat-value`, `alert-`,
    `badge-` and `navbar` as whole class tokens.

### Implementation for User Story 2

Each task below changes **view functions only** in the named file. Handlers, SQL, routes and form
fields stay as they are, and each task follows the "Conventions for every UI task".

- [X] T016 [US2] Restyle the dashboard view in `Handlers/Users/Dashboard.fs` (mockup
  `design/frunchy_public_dashboard_u_username`, keep/drop per `design/REVIEW.md`):
  - header: display name, the DNA link (T013) and a copy-profile-link control (the same Alpine
    control as the DNA page; move it to `Components.copyLink` when both use it);
  - the four stats on `metricTile`;
  - recent cards with thumbnail, progress, `Ep. N`, score `pill`, genres, and the matched anime's
    first studio, format and year (extend the recent-cards query in this slice), plus the unmatched
    variant ("Not on MyAnimeList yet"); a test in `DashboardMalTests.fs` covers studio, format and
    year;
  - "Top genres" on `genreBar`;
  - empty, processing and not-found states;
  - keep `data-stat`, `data-value`, `data-recent-item`, `data-mal-*`, `data-top-genres`,
    `data-genre*`, `data-genres-coverage`, `data-sync` and `data-fr-time`.
- [X] T017 [P] [US2] Restyle `Handlers/Home.fs` (brief 4.1): hero with wordmark and headline, the
  three steps, the sign-in call to action, and the `data-account-deleted` alert.
- [X] T018 [P] [US2] Restyle the sign-in pages (brief 4.2–4.4, 4.7):
  - `Handlers/Auth/SignIn.fs`: `providerButton` per provider; `data-signin-error` and
    `data-signin-unavailable` alerts.
  - `Handlers/Auth/SignUp.fs`: `field` with the live URL preview via Alpine; `data-signup-error`.
  - `Handlers/Auth/Claim.fs`: `data-claim-error`, `data-claim-link`.
  - `Handlers/Auth/Reconfirm.fs`.
- [X] T019 [P] [US2] Restyle the account pages (brief 4.5, 4.6, 4.8):
  - `Handlers/Account/AccountPage.fs`: the card stack; `data-account*`, `data-key-active`,
    `data-identity`, `data-link-provider`, `data-provider`, `data-security-event`;
  - `Handlers/Account/SyncKey.fs`: the key in a monospace box with a copy button;
    `data-new-sync-key`;
  - `Handlers/Account/DeleteAccount.fs`: `confirmLayout`; `data-delete-account`,
    `data-delete-error`.
- [X] T020 [US2] Check the message pages (404, 500, 429, expired form) built by
  `Layout.messagePage` (T006) against brief 4.9, and adjust `Views/Layout.fs` if needed.
- [ ] T021 [US2] Run the whole suite: no assertion edited, and every `DesignSystemTests` page check
  passes. Then run an impeccable audit pass over every page at 1440 and 360px, in en and es, fix
  the blocking findings, and record the result and the revisit list in `quickstart.md`.

**Checkpoint**: every page is on the design system, and behaviour is identical.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [X] T022 [P] Correct the prose of `design/neo_scrobble_profile/DESIGN.md` to the YAML values (R16):
  - primary `#4cd7f6`, secondary `#c0c1ff`;
  - the radius scale (`DEFAULT` 0.5rem, `lg` 1rem);
  - the button example colours;
  - and note that the tokens are shared with the extension.
- [X] T023 [P] Amend `.specify/memory/constitution.md` to 1.1.1 (R15):
  - in "UI", "Tailwind/DaisyUI loaded from CDNs" becomes "Tailwind, fonts and icons loaded from
    CDNs";
  - update the Sync Impact Report;
  - check `.specify/templates/plan-template.md`'s constraints line.
- [X] T024 [P] Update `README.md`:
  - the DNA page;
  - the design system and its shared tokens;
  - the roadmap (004 Implemented).
- [X] T025 [P] Update `CLAUDE.md`:
  - routes (`GET /u/{username}/dna`);
  - Localization/Views: `Views/Theme.fs` and `Views/Components.fs`, no DaisyUI;
  - test hooks are `data-*` only;
  - token names shared with the extension (`tokens.css`) and kept equal by `DesignSystemTests`;
  - use impeccable for UI work.
- [X] T026 [P] Add a note to `SCHEMA.md`: the DNA page reads `watch_events`, `cr_episodes`,
  `cr_season_matches`, `mal_anime` and `mal_anime_tags` at request time; there's no new table.
- [ ] T027 Run the full `dotnet test` with 0 build warnings, and record the counts in the
  quickstart's validation log.
- [ ] T028 Tell the browser-extension session (`ListAgents`, by its cwd `../browser-extension`) that
  the website's tokens are live in `Views/Theme.fs`, that parity is tested, and which website-only
  tokens exist.

---

## Dependencies & Execution Order

- **Setup (T001–T002)**: T002 runs in parallel with T001.
- **Foundational (T003–T007)**: T003 in parallel with T004 → T005 (needs T004) → T006 (needs T004
  and T005) → T007. This phase **blocks both stories**.
- **US1 (T008–T014)**:
  1. T008, T009 and T010 in parallel;
  2. → T011 → T012 → T013 → T014.
- **US2 (T015–T021)**: needs only the foundation; it's independent of US1, except that T016 should
  come after T013, since both edit `Dashboard.fs`.
  1. T015;
  2. → T016, T017, T018 and T019 in parallel (different files);
  3. → T020 → T021.
- **Polish (T022–T028)**: T022–T026 in parallel after both stories → T027 → T028.

## Parallel Examples

```text
# Foundational
T003 DesignSystemTests.fs   T004 Views/Theme.fs

# US1, tests and texts first
T008 DnaTests.fs   T009 DnaPaceTests.fs   T010 Views/Texts.fs (DNA entries)

# US2, after T015: one teammate per page group (impeccable each)
T016 Dashboard.fs   T017 Home.fs   T018 Handlers/Auth/*.fs   T019 Handlers/Account/*.fs

# Polish
T022 DESIGN.md   T023 constitution   T024 README   T025 CLAUDE.md   T026 SCHEMA.md
```

**Text entries**: T017–T019 restyle existing views and should reuse the existing texts. If a page
needs a new label, add it in `Views/Texts.fs` in one batch after the parallel tasks, to avoid edit
conflicts on that file.

## Implementation Strategy

1. **MVP**: Setup + Foundational + US1. The DNA page works on the new base, and the rest of the
   site already looks right through the shared layout and components.
2. **Increment**: US2 restyles the page bodies page by page. Each page is shippable on its own, and
   pages are revisited as mockups arrive.
3. **Close**: polish, the full suite, docs, and the extension notified.

## Notes

- **Behaviour baseline**: the existing tests are the redesign's definition of "unchanged".
  Changing an assertion to make a restyle pass is a defect, not a fix.
- **DNA SQL**: hand-written, all values as parameters, and none of it shared with the dashboard
  slice. The totals query is duplicated on purpose, and a test pins the two equal.
- **Scope**: nothing in this feature changes the extension's API (`import-api.md` stays at v1.3).
