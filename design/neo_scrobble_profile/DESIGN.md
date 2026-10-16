<!-- Superseded by design/night_broadcast/DESIGN.md (feature 010). Kept for history. -->
---
name: Neo-Scrobble Profile
colors:
  surface: '#0f131c'
  surface-dim: '#0f131c'
  surface-bright: '#353942'
  surface-container-lowest: '#0a0e16'
  surface-container-low: '#181c24'
  surface-container: '#1c2028'
  surface-container-high: '#262a33'
  surface-container-highest: '#31353e'
  on-surface: '#dfe2ee'
  on-surface-variant: '#bcc9cd'
  inverse-surface: '#dfe2ee'
  inverse-on-surface: '#2c3039'
  outline: '#869397'
  outline-variant: '#3d494c'
  surface-tint: '#4cd7f6'
  primary: '#4cd7f6'
  on-primary: '#003640'
  primary-container: '#06b6d4'
  on-primary-container: '#00424f'
  inverse-primary: '#00687a'
  secondary: '#c0c1ff'
  on-secondary: '#1000a9'
  secondary-container: '#3131c0'
  on-secondary-container: '#b0b2ff'
  tertiary: '#ffb0cd'
  on-tertiary: '#640039'
  tertiary-container: '#ff79b4'
  on-tertiary-container: '#780047'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#acedff'
  primary-fixed-dim: '#4cd7f6'
  on-primary-fixed: '#001f26'
  on-primary-fixed-variant: '#004e5c'
  secondary-fixed: '#e1e0ff'
  secondary-fixed-dim: '#c0c1ff'
  on-secondary-fixed: '#07006c'
  on-secondary-fixed-variant: '#2f2ebe'
  tertiary-fixed: '#ffd9e4'
  tertiary-fixed-dim: '#ffb0cd'
  on-tertiary-fixed: '#3e0022'
  on-tertiary-fixed-variant: '#8c0053'
  background: '#0f131c'
  on-background: '#dfe2ee'
  surface-variant: '#31353e'
typography:
  display-hero:
    fontFamily: Plus Jakarta Sans
    fontSize: 48px
    fontWeight: '800'
    lineHeight: 56px
    letterSpacing: -0.03em
  display-hero-mobile:
    fontFamily: Plus Jakarta Sans
    fontSize: 32px
    fontWeight: '800'
    lineHeight: 40px
    letterSpacing: -0.02em
  headline-xl:
    fontFamily: Plus Jakarta Sans
    fontSize: 36px
    fontWeight: '700'
    lineHeight: 44px
    letterSpacing: -0.02em
  headline-xl-mobile:
    fontFamily: Plus Jakarta Sans
    fontSize: 26px
    fontWeight: '700'
    lineHeight: 34px
    letterSpacing: -0.01em
  headline-lg:
    fontFamily: Plus Jakarta Sans
    fontSize: 24px
    fontWeight: '700'
    lineHeight: 32px
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Plus Jakarta Sans
    fontSize: 20px
    fontWeight: '600'
    lineHeight: 28px
    letterSpacing: -0.01em
  stat-counter:
    fontFamily: Plus Jakarta Sans
    fontSize: 32px
    fontWeight: '800'
    lineHeight: 36px
    letterSpacing: -0.02em
  body-lg:
    fontFamily: Plus Jakarta Sans
    fontSize: 16px
    fontWeight: '400'
    lineHeight: 24px
  body-md:
    fontFamily: Plus Jakarta Sans
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  label-md:
    fontFamily: Plus Jakarta Sans
    fontSize: 13px
    fontWeight: '600'
    lineHeight: 18px
    letterSpacing: 0.01em
  label-sm:
    fontFamily: Plus Jakarta Sans
    fontSize: 11px
    fontWeight: '700'
    lineHeight: 14px
    letterSpacing: 0.06em
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  gutter: 1.5rem
  gutter-sm: 1rem
  margin: 2rem
  margin-sm: 1rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 1rem
  space-lg: 1.5rem
  space-xl: 2rem
  space-2xl: 3rem
---

## Source of truth

The YAML above is the design system. Its tokens are implemented once on each side, with the same
names and values: the website in `Views/Theme.fs` (`--color-*`, `--text-<style>-*`, `--radius-*`,
`--space-*`, mapped into the Tailwind config) and the browser extension in
`browser-extension/src/styles/tokens.css`. `DesignSystemTests` keeps them equal. Where earlier
prose or a mockup's local Tailwind config disagreed with the YAML (a `#06B6D4` / `#6366F1` palette,
`lg` = 0.5rem), the YAML wins (feature 004, research R16, R17). `design/REVIEW.md` says what to take
from each mockup.

## Brand & Style

A dark, precise home for a person's anime history: a Last.fm for anime. Deep surfaces let cover art
and numbers carry the colour; cyan marks what's live or important, violet groups categories, pink is
for rare highlights. The copy is plain: no "telemetry" voice, no invented data. The mark is a
clapperboard whose body is a bar chart (`design/brand/frunchy-mark.svg`), shown next to the
"Frunchy" wordmark; on light backgrounds (browser tabs, toolbars) it sits on a `surface` tile.

## Colors

- **Primary `primary` (#4cd7f6)**: live states, key figures, main actions. Text on it is `on-primary`.
- **Secondary `secondary` (#c0c1ff)**: categories, sub-metrics, the start of share-bar gradients.
- **Tertiary `tertiary` (#ffb0cd)**: rare highlights (e.g. "in bursts").
- **Surfaces**: `surface` (#0f131c) is the page; `surface-container-low` / `surface-container` /
  `surface-container-high` / `-highest` are cards, inputs and raised elements, in that order.
- **Text**: `on-surface` for body, `on-surface-variant` for secondary text, `outline` only for
  placeholders and borders (it fails AA on `surface-container-highest`).
- **Error**: `error` text and borders; `error-container` / `on-error-container` for filled states.

Every text/surface pair the components use meets WCAG 2.1 AA; `DesignSystemTests` checks them.

## Typography

**Plus Jakarta Sans** throughout; **JetBrains Mono** (`--font-mono`) only for keys, usernames and
URLs. Numbers use tabular figures. `label-sm` is uppercase with wide tracking and labels figures;
it is never an eyebrow above a heading.

## Layout & Spacing

A 12-column grid, max width 1360px (`max-w-page`), 16px outer margins on phones and 32px from
`md`. Pages work from 360px wide. Cards stack on phones and share rows from `lg`.

## Elevation & Depth

Depth comes from surface steps and `--border-micro` (1px white at 6%), not from heavy shadows.
`--shadow-level-2` is for raised overlays; `--glow-primary` is the primary button's hover.

## Shapes

`radius-default` 0.5rem for inputs, buttons and posters;
`radius-lg` 1rem for cards; `radius-full` for status pills and round icon wells.

## Components

The shared components live in `Views/Components.fs` (styles in `Theme.componentCss`): card, metric
tile, pill (primary / secondary / tertiary / muted), share bar (6px track, secondary → primary
fill), buttons (primary, secondary, ghost, destructive; Discord and Google keep their own colours),
field (label, input, hint, error, aria-wired), alert (success, error, notice), processing line,
empty state, and the mark. The components sheet mockup is
`design/frunchy_base_components_sheet/`.
