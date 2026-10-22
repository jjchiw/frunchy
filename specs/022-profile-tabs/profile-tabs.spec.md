# Feature Specification: Profile tabs

**Feature Branch**: `022-profile-tabs`

**Created**: 2026-10-22

**Status**: Draft

**Input**: "the dashboard … anime dna is in yellow like I am in anime dna … year in review that it also weird. I think we should use three buttons Dashboard, Anime DNA, Year in review that would work as 'tabs' … it should look also good in mobile. You have to work with the designer." (the owner, on the live site)

**Design**: the frunchy_designer session's direction for feature 022 (mock and screenshots in `design/profile_tabs/`:
`code.html`, `screen-1280.png`, `screen-360.png`). That direction is binding. This spec restates it as requirements.

## Clarifications

### Session 2026-10-22 (the designer's direction)

- Q: Where do the tabs go? → A: Every page under `/u/{username}` gets the same three-row top: (1) the profile masthead, (2) the tab bar, (3) the view's own heading. The tab bar is then in the same place on every page.
- Q: The masthead? → A: A shared component.
  - **Left**: the display name (headline-xl-mobile / md:headline-xl), `@username` in mono label-md on-surface-variant, then the notes (the public note as today; the owner's members note moves here from the right column).
  - **Right**: `profileAddress` unchanged (Copy link, the private note, or nothing). Copy link copies the current page's address. The compare page passes no address.
  - **The name**: the `h1` on the dashboard. On every other page it's a `p` whose text links to `/u/{username}` (hover: underline primary/60, offset 6px), so each page keeps one `h1`.
  - **Layout**: `flex-col gap-5 md:flex-row md:items-end md:justify-between`; on 360px it stacks name, @username, note, button, then the tabs.
- Q: The tab bar? → A: `<nav class="fr-tabs" aria-label="Profile pages" data-profile-tabs>` with three `<a class="fr-tab" data-tab="dashboard|dna|year">`, in the order Dashboard · Anime DNA · Year in review, `aria-current="page"` on the selected one (page links, not an ARIA tablist).
  - **CSS**: `.fr-tabs` and `.fr-tab` go in `Theme.componentCss`, copied from the mock's `<style>`.
  - **Selected state**: styled only from `[aria-current="page"]`, so a tab can't look selected without being announced as selected.
  - **Focus ring**: `outline-offset: -2px` on the tabs, so the bar's overflow clipping can't cut it.
  - **Motion**: the colour transition joins the existing reduced-motion-gated rule.
  - **No icons.**
  - **Labels**: Dashboard · Anime DNA · Year in review, and in Spanish Panel · ADN anime · Resumen del año (it matches the year page's h1, "Resumen de 2026", as the English pair does). aria-label "Profile pages" / "Páginas del perfil".
- Q: The dashboard's sync and fediverse lines? → A: They move under the tabs, as the first block of the dashboard, with unchanged markup.
- Q: The view headings? → A:
  - **DNA**: `h1` "Anime DNA" (headline-lg) with `data-dna-user="{DisplayName}"` kept, then the intro. The page `<title>` keeps "X's Anime DNA".
  - **Year**: `h1` "2026 in review" (headline-lg), then the intro, the unchanged year pills and the rewatch note (`space-y-4`, `space-y-2` between the h1 and the intro).
  - The display-hero size leaves these two pages.
- Q: The anime and compare pages? → A: Masthead and tab bar with nothing selected.
  - **Anime page**: loses its "← Dashboard" link. Keeps "Watch on Crunchyroll" (secondary) and "About this series" (ghost), and its title drops to headline-xl-mobile / md:headline-xl next to the poster.
  - **Compare page**: no address block; its card and `h1` stay as they are.

## Requirements *(mandatory)*

- **FR-001**: One shared masthead and one shared tab bar (`Views/Components.fs`), used by the dashboard, DNA, year (both routes), anime and compare pages.
- **FR-002**: The selected tab is the current view (dashboard, DNA or year), marked with `aria-current="page"`. Anime and compare select none.
- **FR-003**: Every existing hook stays: `data-dna-link`, `data-year-link`, `data-dashboard-link` (on the three tab anchors on every page, the selected one included), `data-copy-link`, `data-year-picker`, `data-year-option`, `data-profile-private`, `data-profile-members`, `data-dna-user`, `data-year-user`, `data-anime-title`. New hooks: `data-profile-tabs`, `data-tab`, `data-profile-masthead`, `data-profile-name`.
- **FR-004**: The tabs fit 360px with no horizontal page overflow in English and Spanish. The bar itself scrolls if it ever overflows.
- **FR-005**: All copy lives in `Views/Texts.fs`, in English and Spanish. No shared token changes (only `componentCss`), so the extension's parity test is untouched.
- **FR-006**: No change to routes, visibility rules, data or the API.

## Success Criteria *(mandatory)*

- **SC-001**: On every profile page, a visitor can tell which view they're on from the tab bar alone.
- **SC-002**: The designer's review of the rendered pages (1280 and 360, English and Spanish) passes.
