# Tasks: Night Broadcast Design System

**Input**: [night-broadcast.spec.md](night-broadcast.spec.md), [plan.md](plan.md), [research.md](research.md),
[contracts/design-system.md](contracts/design-system.md), [quickstart.md](quickstart.md)

**Tests**: required by constitution V. `DesignSystemTests` changes only its expected design values
and gains the leftover checks. Every other test stays as it is and green.

**Delegation**: Sonnet agents implement; the main session reviews each diff (styling only, every
hook kept) and runs the suite. UI work goes through the `impeccable` skill, views through `oxpecker`.

## Phase 1: Foundational, tokens and components (US2, blocks everything)

- [x] T001 [US2] `DesignSystemTests` first; it fails until T002 lands:
  - **Expected values**: change them to the contract: `--color-primary: #ffb547;` and `family=Figtree` (in `pages load the design system` and `pageProblems`).
  - **New test: no Neo-Scrobble values or old fonts.** No page in the every-page check contains `#4cd7f6`, `#0f131c`, `#06b6d4`, `#c0c1ff`, `Plus+Jakarta`, `Plus Jakarta`, `JetBrains` or `--color-primary: #4cd7f6`.
  - **New test: no gradients.** `Theme.componentCss` contains no `gradient`.
  - **New test: `--font-display`** is in `Theme.tokens`.

  · tests/FrunchyBackend.Tests/DesignSystemTests.fs
- [x] T002 [US2] `Views/Theme.fs`:
  - **Colours**: every value from research R1, with the same names.
  - **Type styles**: R3 weights and tracking only.
  - **Radii**: R3.
  - **Effects**: from the contract, plus `--font-family`, `--font-display` and `--font-mono` from R2.
  - **Tailwind config**: `fontFamily.sans` Figtree, `display` Bricolage Grotesque, `mono` IBM Plex Mono.
  - **`fontStylesheets`**: one Google Fonts URL for the three families; Material Symbols unchanged.
  - **`componentCss`**, per R4:
    - `::selection` amber;
    - headings (`h1, h2, h3` and the `text-headline-*`/`text-display-*` classes) in `font-display`;
    - buttons `rounded-full`;
    - secondary outlined;
    - pills retinted;
    - `.fr-fill` solid primary;
    - glow/edge in amber.
  - **Mark and favicon**: `markSvg` recoloured per R5; favicon tile `#13111a`.

  · Views/Theme.fs
- [x] T003 [US2] Add `theme-color` `#13111a` and the wordmark in `font-display` to `Views/Layout.fs`. In `Views/Components.fs`, metric tile values and stat counters use `font-mono`. Keep every hook · Views/Layout.fs, Views/Components.fs
- [x] T004 [P] [US2] Recolour `design/brand/frunchy-mark.svg` per R5 and update its comment. Write `design/night_broadcast/DESIGN.md` (R9: values, roles, rules, the pitch link), and add a "superseded by night_broadcast" line at the top of `design/neo_scrobble_profile/DESIGN.md` · design/brand/frunchy-mark.svg, design/night_broadcast/DESIGN.md, design/neo_scrobble_profile/DESIGN.md

**Checkpoint**: the full suite is green, and every page is on the new tokens.

## Phase 2: Pages (US1)

One task per page group. Each task:
- reads its handlers' views;
- removes only what the tokens can't fix: eyebrow overlines above headings (`fr-overline` on a heading's lead-in), hard-coded colours, radii or sizes that clash with the new shapes, and any gradient;
- uses `impeccable`'s audit on its pages, and fixes blocking and high findings;
- keeps every hook, text and order;
- runs its pages' tests.

- [x] T005 [US1] Public pages: home, the dashboard, the 404 and message pages · Handlers/Home.fs, Handlers/Users/Dashboard.fs, Views/Layout.fs (messagePage)
- [x] T006 [US1] Profile pages: DNA, year, anime · Handlers/Users/Dna.fs, Handlers/Users/Year.fs, Handlers/Users/Anime.fs
- [x] T007 [US1] Episode page and comment edit page · Handlers/Episodes/Episode.fs, Handlers/Episodes/EditComment.fs
- [ ] T008 [US1] Auth and account: sign-in, sign-up, claim, reconfirm, account, key, delete · Handlers/Auth/*.fs, Handlers/Account/*.fs
- [ ] T009 [US1] Operator pages: MyAnimeList review, operators · Handlers/Review/MalReview.fs, Handlers/Review/Operators.fs

## Phase 3: The extension can follow (US3)

- [ ] T010 [US3] The contract and the handoff are written (contracts/). Send the handoff to the extension session if one is running; otherwise record that the owner has to start it · specs/010-night-broadcast/contracts/extension-handoff.md

## Phase 4: Polish

- [ ] T011 [P] Update the docs:
  - `CLAUDE.md`: the design-system paragraph: Night Broadcast, the colour roles, `--font-display`, and that the extension follows through the 010 contract.
  - `FRONTEND.md`: the design lines.
  - `README.md`: roadmap row 010.

  · CLAUDE.md, FRONTEND.md, README.md
- [ ] T012 Run the full suite and an `impeccable` audit of every page in quickstart §2 (1280 and 360, en and es), then append the validation log · specs/010-night-broadcast/quickstart.md

## Dependencies

- **Order**: Phase 1 (T001 → T002 → T003, with T004 in parallel), then T005–T009, then T011–T012. T010 is independent.
- **Running in parallel**: T005–T009 touch different handler files and can run in parallel, but they share the audit tooling; one agent running them in sequence is simpler.
