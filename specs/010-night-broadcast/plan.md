# Implementation Plan: Night Broadcast Design System

**Branch**: `010-night-broadcast` | **Date**: 2026-10-15 | **Spec**: [night-broadcast.spec.md](night-broadcast.spec.md)

## Summary

- **One file holds most of it**: the Night Broadcast values replace Neo-Scrobble's in `Views/Theme.fs`,
  under the same token names plus `--font-display`, so every page restyles through tokens and the
  shared component layer.
- **New fonts**: Bricolage Grotesque for headings, Figtree for text, IBM Plex Mono for figures and
  handles.
- **Shapes and charts**: radii grow, buttons go fully round, and the genre bar loses its gradient.
- **Mark and favicon**: recoloured.
- **Pages**: a restyling pass per page group removes leftovers that tokens can't fix (eyebrows,
  hard-coded sizes), and an `impeccable` audit covers every page.
- **Extension**: the new values go into a contract and a handoff message for its session.
- **Tests**: only the design tests' expected values change.

## Technical Context

**Language/Version**: F# on .NET 10.

**Primary Dependencies**: Oxpecker.ViewEngine, the Tailwind CDN and Google Fonts. No new package.

**Storage**: none.

**Testing**: xUnit v3 and Testcontainers; `DesignSystemTests` covers contrast and every page's
hooks.

**Target Platform**: server-rendered HTML.

**Project Type**: web service with a server-rendered UI.

**Performance Goals**: one font stylesheet request, as today.

**Constraints**:
- no build step; fonts come from the CDN;
- WCAG 2.1 AA;
- every `data-*` hook kept, and no content change.

**Scale/Scope**: ~20 page views, 1 theme file, 1 components file, 1 mark.

## Constitution Check

| Principle | Assessment |
|---|---|
| I. Core asset | PASS. No data is touched. |
| II. Boring tech | PASS. Same CDN stack, no package. |
| III. Vertical slices | PASS. The look lives in `Views/Theme.fs` and `Views/Components.fs`. Slices only lose local leftovers. |
| IV. Ingestion | PASS. Not touched. |
| V. Tests | PASS. `DesignSystemTests` runs on the new values: contrast, every page, no leftovers. The rest of the suite stays green, unchanged. |
| Constraints | PASS. CDN-only frontend, no text changes, the pinned packages. |

Re-checked after design: no change.

## Project Structure

```text
Views/Theme.fs            # colours, type styles, radii, effects, fonts (+ --font-display), Tailwind config, component CSS, mark, favicon
Views/Layout.fs           # theme-color; wordmark in font-display
Views/Components.fs       # metric value in font-mono; any component markup the new shapes need
Handlers/**               # per page group: leftovers only (eyebrows, local radius/size clashes)
design/brand/frunchy-mark.svg, design/night_broadcast/DESIGN.md (new), design/neo_scrobble_profile/DESIGN.md (superseded note)
tests/FrunchyBackend.Tests/DesignSystemTests.fs   # expected values; no old values; no gradient
CLAUDE.md, FRONTEND.md, README.md                 # design-system lines
specs/010-night-broadcast/contracts/*            # shared contract + extension handoff
```

## Risks and review points (main session)

- **Scope creep**: page diffs must be styling only. Any text, order or structure change is
  rejected in review (FR-006).
- **Hooks**: a restyle must never drop a `data-*` attribute. The suite catches this; the review
  reads every handler diff for it.
- **Contrast**: values come from research R1 and are tested.
- **Extension parity**: the expected failure in the main checkout is documented, not hidden. Don't
  skip or loosen the test.

## Delegation

Sonnet agents implement. The main session reviews every diff, runs the suite, and does the final
audit pass with `impeccable`.

## Complexity Tracking

None.
