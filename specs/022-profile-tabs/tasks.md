# Tasks: Profile tabs

**Input**: [profile-tabs.spec.md](profile-tabs.spec.md); the binding design is in `design/profile_tabs/` (open
`code.html`; its `<style>` holds `.fr-tabs`/`.fr-tab` with comments).

**Tests**: `data-*` hooks only. The suite must stay green.

- [ ] T001 `Theme.componentCss`: `.fr-tabs` and `.fr-tab` from the mock; add `.fr-tab` to the reduced-motion-gated
  transition rule. Remove the `Fingerprint`/`CalendarMonth` icons from `Theme.Icon` only if nothing else uses them
  · Views/Theme.fs
- [ ] T002 `Views/Components.fs`:
  - `profileMasthead` (name as `h1` on the dashboard, else a `p` with a link to `/u/{username}`; `@username`; the
    notes, including the members note moved here; the right column takes `profileAddress` or nothing);
  - `profileTabs` (selected: `Dashboard | Dna | Year | NoTab`).

  Texts (en and es): the tab labels, the aria-label, DnaHeading and YearTitle as plain view names
  · Views/Components.fs, Views/Texts.fs
- [ ] T003 The pages:
  - **Dashboard**: masthead, tabs, then the sync and fediverse lines.
  - **DNA**: masthead, tabs, `h1` "Anime DNA" with `data-dna-user`.
  - **Year** (both routes): masthead, tabs, `h1` "{year} in review", intro, pills and note.
  - **Anime**: masthead, tabs (none selected), no "← Dashboard" link, smaller title.
  - **Compare**: masthead (no address), tabs (none selected).

  Keep every hook from the spec's FR-003 · Handlers/Users/{Dashboard,Dna,Year,Anime,Compare}.fs
- [ ] T004 Tests:
  - `ProfileTabsTests`: on each page, `data-profile-tabs` exists with three links in order;
    `aria-current="page"` is on the right one (none on anime and compare); every page has exactly one `h1`; the
    masthead name links to the dashboard except on the dashboard; Spanish labels.
  - Update tests that read the person's name from the `h1` text so they read the hooks.

  · tests/FrunchyBackend.Tests/ProfileTabsTests.fs
- [ ] T005 CHANGELOG `## Unreleased` (Changed: the profile pages share a tab bar), full suite, commit. Then the main
  session takes screenshots at 1280 and 360 in English and Spanish for the designer's review.
