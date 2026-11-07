# Tasks: Light theme (feature 032)

Spec: `light-theme.spec.md`. Direction (binding): `design-direction.md` (frunchy_designer, 2026-11-07).

## Phase 1: Design
- [x] T001 Design brief with a candidate AA palette and questions (`design-brief.md`)
- [x] T002 Designer's direction: final light palette, fill tokens, effects, footer switch (`design-direction.md`)

## Phase 2: Mechanism (FR-002, FR-003, FR-004)
- [x] T003 `Infrastructure/ThemePreference.fs`: `frunchy.theme`, `Mode`, `tryParse`, `current`, `switchHref`
- [x] T004 `Handlers/Theme.fs`: `GET /theme/{mode}` (cookie, 303 to a local returnUrl, unknown mode = plain 404), registered in `Program.endpoints`
- [x] T005 `Views/Layout.fs`: `<html data-theme>`, `color-scheme` and `theme-color` metas, the footer `nav[data-theme-switch]` before the language link
- [x] T006 `Views/Texts.fs`: `ThemeSwitchLabel`, `ThemeLight`, `ThemeDark`, `ThemeSystem` (en, es)

## Phase 3: Palette (FR-001, FR-005)
- [x] T007 `Views/Theme.fs`: `lightColors`, `lightEffects`, `fillColors` (= roles in dark), `lightFillColors`, `--poster-edge`, `--mark-frame`; `tokensCss` with the light and system blocks
- [x] T008 Tailwind colours resolve to `color-mix(… var(--color-X) …)` (checked in headless Chrome, both palettes, opacity modifiers)
- [x] T009 The fill rule applied in components and views (buttons, bars, meters, charts, Progress bar, tints, checkboxes); the mark on tokens, the favicon on dark literals; `::selection`, poster edge

## Phase 4: Tests and snapshots (FR-001, FR-007)
- [x] T010 `DesignSystemTests`: WCAG AA over both palettes, light names = dark names, fills Frunchy-only and equal to roles in dark; parity scoped to the dark tokens
- [x] T011 `ThemeSwitchTests`: cookie, redirects, 404, `data-theme`, the footer switch (en, es), the CSS blocks, no hex in the Tailwind config or the mark
- [x] T012 Snapshot harness: every page in both themes (`-light` files), the guide pinned to dark
- [x] T013 Docs: `DESIGN.md` light section, `CLAUDE.md`, `changelog.d/032-light-theme.md`

## Phase 5: Sign-off
- [x] T014 Designer sign-off on the screenshots (frunchy_designer, 2026-11-07: signed off as built, no fixes)
