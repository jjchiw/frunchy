# Feature Specification: Night Broadcast Design System

**Feature Branch**: `010-night-broadcast`

**Created**: 2026-10-15

**Status**: Draft

**Input**: "Move the whole site from the Neo-Scrobble look to the 'Night Broadcast' direction chosen from the visual pitches (row 2; kept in `design/pitches/visual-directions/`): warm dark surfaces, amber and rose accents, Bricolage Grotesque titles, Figtree text, IBM Plex Mono figures. Restyle only, before taste compatibility is built on it. The browser extension shares the tokens and follows in its own session."

## Clarifications

### Session 2026-10-15 (decided in the autonomous run; the owner can overturn any of them)

- Q: Do token names change? → A: No. The names stay those of `contracts/design-system.md` (feature 004); only their values change. The names are the contract with the extension, so renaming would break it for no visible gain.
- Q: Hours shown as `486:12`, as in the pitch? → A: No. Figures keep their current format and wording. That was a pitch flourish; changing number formats is a content change, outside a restyle.
- Q: The pitch's "ON AIR" and "NOW SHOWING" labels? → A: Dropped. They are eyebrow labels, which the design rules forbid, and they state nothing true.
- Q: Charts? → A: Solid fills, no gradients: amber for the person the page is about, rose for anyone else (used by later features). The genre bar loses its secondary→primary gradient.
- Q: The brand mark? → A: The mark's shape stays (`design/brand/frunchy-mark.svg`); it takes the new colours.
- Q: Light mode? → A: Out of scope. The site stays dark-only.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The whole site in the new look (Priority: P1)

Every page a visitor or user can reach shows the Night Broadcast look: warm near-black surfaces, amber for primary actions and the page's own figures, rose for warnings like spoilers, Bricolage Grotesque for titles, Figtree for text, and IBM Plex Mono for figures, usernames, keys and addresses. Pages keep their content, order and behaviour.

**Why this priority**: it is the feature. A partial restyle would leave the site looking like two products.

**Independent Test**: open every page in the route list (signed out, signed in, as an operator, in English and Spanish, at 1280px and 360px) and check it uses only the new tokens and fonts, keeps every `data-*` hook, and that the full test suite passes unchanged.

**Acceptance Scenarios**:

1. **Given** any page, **When** it is rendered, **Then** it uses only the new token values and the three new fonts, and no Neo-Scrobble value or font remains anywhere in the site's styles.
2. **Given** any page, **When** it is rendered, **Then** every `data-*` hook it had before is still present on the equivalent element, and every existing test passes without being changed.
3. **Given** any text and background pair the site uses, **When** contrast is computed, **Then** it meets WCAG 2.1 AA (4.5:1 for text, 3:1 for large text and interface parts).
4. **Given** any interactive element, **When** it gets keyboard focus, **Then** a visible focus ring in the new style shows.
5. **Given** a page at 360px wide, in Spanish, **When** it is shown, **Then** nothing overflows horizontally.

---

### User Story 2 - Shared components carry the look (Priority: P1)

Buttons, pills, cards, fields, alerts, empty states, the genre bar, metric tiles, the time element, the spoiler toggle and the navigation are restyled once, in the shared components, so every page and every later feature gets the look by using them.

**Why this priority**: it is how the restyle stays consistent and how taste compatibility (next) inherits it.

**Independent Test**: the design-system test page check covers every shared component in its states (default, hover, focus, disabled, error) in the new style.

**Acceptance Scenarios**:

1. **Given** a shared component, **When** any page uses it, **Then** it looks the same everywhere.
2. **Given** the genre bar, **When** it is drawn, **Then** it is a solid amber fill on a warm track, with no gradient.
3. **Given** primary buttons, **When** they are drawn, **Then** they are amber with dark text, fully rounded; secondary buttons are outlined.

---

### User Story 3 - The extension can follow (Priority: P2)

The browser extension uses the same token names. This feature publishes the new values as the shared contract, so the extension's own session applies them, and the parity check between the two passes again once it has.

**Why this priority**: until the extension follows, the extension and the site look different, and the parity check fails where both repositories are checked out side by side.

**Independent Test**: with the extension's tokens updated to the contract, the parity check passes; before that, the contract document lists every changed value and the check names each difference.

**Acceptance Scenarios**:

1. **Given** the contract document, **When** the extension session reads it, **Then** it has every token name with its new value, the three fonts and how to load them.
2. **Given** both repositories checked out side by side with the extension updated, **When** the parity check runs, **Then** it passes.

### Edge Cases

- **Long titles and names** (series titles, display names, URLs) wrap without overflow in the new fonts, at 360px.
- **Fonts that don't load**: system fallbacks keep the page readable and the layout intact.
- **Spanish**: longer labels fit in buttons and pills.
- **Reduced motion**: any transition respects the viewer's reduced-motion setting.
- **Thumbnails and posters**: images sit on the new surfaces without looking cut out (a border or radius consistent with cards).
- **Error and destructive states** stay clearly distinct from rose (spoiler) and amber (primary).

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The site's design tokens MUST keep their current names and take the Night Broadcast values: warm near-black surfaces, warm light text, amber primary, rose secondary, and error colours distinct from both.
- **FR-002**: Titles MUST use Bricolage Grotesque, text Figtree, and figures, usernames, keys and addresses IBM Plex Mono, each with a system fallback, loaded from a font CDN with no build step.
- **FR-003**: Radii MUST grow to the pitch's rounder shapes (cards and panels clearly rounded, buttons and pills fully rounded).
- **FR-004**: Every shared component MUST be restyled in one place so pages inherit the look; no page may restyle a shared component locally.
- **FR-005**: Every page in the route list MUST use the new look; no Neo-Scrobble colour value or font may remain in the site's styles.
- **FR-006**: Page content, order, routes, texts, figures and their formats MUST NOT change. Layout may change only where the new style needs it (spacing, radius, type scale).
- **FR-007**: Every `data-*` hook MUST be kept on its equivalent element, and every existing test MUST pass unchanged.
- **FR-008**: Every text and background pair and every interface part MUST meet WCAG 2.1 AA, checked by the design-system tests on the new values.
- **FR-009**: Charts MUST use solid fills: amber for the page's own person, rose reserved for other people; no gradients.
- **FR-010**: No eyebrow labels, no emoji icons, no decoration that claims something untrue (the pitch's "ON AIR" and "NOW SHOWING" are not built).
- **FR-011**: The brand mark and favicon MUST take the new colours, keeping the mark's shape.
- **FR-012**: The shared design-system contract MUST list every token's new value, the fonts and how to load them, and MUST be sent to the extension's session, which applies it in its own repository.
- **FR-013**: The design reference in `design/` MUST be updated: a document for Night Broadcast replaces the Neo-Scrobble values as the source of truth, and the pitch canvas is linked.

### Key Entities

- **Design tokens**: named values (colours, type styles, radii, spacing, effects) shared by the site and the extension.
- **Shared components**: the building blocks every page uses.
- **Design-system contract**: the document both repositories follow.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of pages in the route list render with the new tokens and fonts, and none references an old value or font.
- **SC-002**: The full test suite passes with no existing test changed, apart from the design-system tests' expected token values.
- **SC-003**: Every token pair the site uses meets WCAG 2.1 AA.
- **SC-004**: An impeccable audit of every page (signed out, signed in, operator; English and Spanish; 1280px and 360px) has no blocking finding.
- **SC-005**: Once the extension applies the contract, the parity check between the two repositories passes.

## Assumptions

- The extension's session is not running now; the contract and a ready-to-send message are prepared, and the owner starts or resumes that session. Until it follows, the parity check fails only where both repositories sit side by side.
- No new data, routes or texts; this is a restyle. Taste compatibility (feature 011) is designed in this look afterwards.
- Pages the pitch didn't draw (DNA, year, anime, account, sign-in, review, message pages) are restyled from the shared components and the pitch's rules, then audited.
- Fonts come from Google Fonts, like the current ones (the constitution allows CDN fonts).

## Verbatim Constraints

- Direction: "Night Broadcast", row 2 of the visual-direction pitches (`design/pitches/visual-directions/`)
- Fonts: Bricolage Grotesque (titles), Figtree (text), IBM Plex Mono (figures, usernames, keys, URLs)
- Accents: amber `#ffb547` (primary), rose `#ff6fa8` (others, spoilers); surfaces from `#13111a`
