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

## Brand & Style

This design system defines an immersive, telemetry-driven anime tracking and social profile platform. Combining the algorithmic intimacy of music tracking services with high-fidelity streaming dashboard aesthetics, the visual direction merges deep slate cosmic canvases with luminous cyan and neon accents. 

The mood communicates precision, digital connoisseurship, and cultural fandom. Rather than flat data grids or playful cartoon styling, the aesthetic embraces glass-morphic surfaces, subtle volumetric gradients, micro-borders with spectral cyan luminescence, and rich visual artwork frames. It evokes the feeling of a futuristic media console—tailored for modern anime enthusiasts who view their watch history as a curated personal identity.

## Colors

The palette is engineered specifically for deep-space dark mode, maximizing the luminescent impact of cover posters and dynamic playback telemetry.

- **Primary (`#06B6D4`)**: High-luminance neon cyan. Serves as the primary indicator for active scrobbling, live telemetry, critical interactive triggers, and high-impact stat meters.
- **Secondary (`#6366F1`)**: Electric violet. Utilized for category segmentation, analytical progress fills, sub-metrics, and secondary callouts.
- **Tertiary (`#EC4899`)**: Hot fuchsia. Reserved for expressive milestones, favorites, heart reactions, and high-energy identity highlights.
- **Neutral (`#0B0F17`)**: Cosmic deep slate. Forms the foundation of the viewport, paired with `#131B2A` for container cards and `#1E293B` for elevated surface interactions.
- **Success / Status (`#10B981`)**: Radiant emerald. Indicates real-time sync status, completed series tags, and upward viewership trends.

Surfaces rely on transparent alpha gradations of slate overlaid with crisp 1px borders at 8%–15% opacity to maintain layered depth without visual clutter.

## Typography

Typography relies uniformly on **Plus Jakarta Sans**, utilizing its geometric clarity, tight counters, and contemporary alternate cuts to deliver a tech-forward media dashboard look.

- **Numerics & Analytics**: `stat-counter` and `display-hero` styles apply tabular figure properties (`font-variant-numeric: tabular-nums;`) to prevent layout shifts during live scrobbling updates and timestamp increments.
- **Labels & Micro-data**: Overline tags, episode formats, and metadata indicators leverage `label-sm` with uppercase casing and expanded tracking to guarantee legibility against dark translucent cards.
- **Hierarchical Balance**: Primary page headlines use tight negative tracking (`-0.02em` to `-0.03em`) to anchor viewports cleanly above dynamic data streams.

## Layout & Spacing

Layout adheres to a 12-column adaptive fluid grid structured for dense information architecture and modular widgets.

- **Desktop (≥1200px)**: 12 columns, 24px gutters, max-width 1360px centered canvas. Supports split telemetry views (e.g., 8 columns for recent feed/scrobbles, 4 columns for "Anime DNA" and live listening/watching card).
- **Tablet (768px - 1199px)**: 8 columns, 16px gutters, 24px outer margins. Sidebar aggregates into stacked horizontal scroll trays or compact banner modules.
- **Mobile (<768px)**: 4 columns, 16px gutters, 16px outer margins. Metric tiles wrap into 2x2 grids, and episode lists drop secondary metadata in favor of poster-title-timestamp stacks.

Rhythm is managed through unified 4px base increments, prioritizing visual grouping with `space-sm` for inline badges and icons, `space-md` for standard card padding, and `space-xl` for section separation.

## Elevation & Depth

Elevation does not use traditional muddy drop shadows; instead, it utilizes translucent dark vitreous layering, diffuse colored glows, and crisp micro-outlines.

- **Level 0 (Canvas)**: Solid base background (`#0B0F17`).
- **Level 1 (Card Baseline)**: Translucent surface `#131B2A` at 85% opacity with `backdrop-filter: blur(12px)` and a 1px inner stroke `rgba(255, 255, 255, 0.06)`.
- **Level 2 (Active/Hover Containers)**: Elevated surface `#1E293B` at 90% opacity with a refined cyan edge highlight: 1px top stroke of `rgba(6, 182, 212, 0.35)` and a soft ambient shadow `0 12px 32px -8px rgba(0, 0, 0, 0.6)`.
- **Level 3 (Floating Overlays & Popovers)**: `#131B2A` at 95% opacity with an intense radial bloom of primary cyan behind the container `0 0 40px -10px rgba(6, 182, 212, 0.25)`.
- **Accent Glow**: High-priority interactive elements (e.g., live "Scrobbling Now" badges) employ micro outer blurs: `box-shadow: 0 0 16px rgba(6, 182, 212, 0.45)`.

## Shapes

With a roundedness level of `2`, the shape vocabulary balances crisp digital precision with modern ergonomics:

- **Base Radius (0.5rem / 8px)**: Input fields, episode grid items, stat blocks, and micro badges.
- **Container Radius (1rem / 16px)**: Primary telemetry cards, anime showcase panels, and modal containers.
- **Pill Radius (9999px)**: Reserved strictly for status badges ("Scrobbling", "Completed"), category chips, quick-filter pills, and round avatar cutouts.
- **Artwork Framing**: Anime key visuals and poster thumbnails retain a tighter 8px corner radius with an inset 1px dark border to preserve edge integrity against varied palette imagery.

## Components

### Buttons
- **Primary**: Solid `#06B6D4` fill with dark slate typography (`#080C14`), bold 14px weight, 8px border radius, and subtle hover transition to an outer glow `0 0 20px rgba(6, 182, 212, 0.4)`.
- **Secondary / Ghost**: Translucent background `rgba(255, 255, 255, 0.04)` bordered by `rgba(255, 255, 255, 0.12)`, text `#F8FAFC`. On hover: border tints to `#06B6D4` and background to `rgba(6, 182, 212, 0.08)`.
- **Icon Actions**: 36x36px square with 8px radius, transparent background, containing 18px icons in muted slate `#94A3B8`, brightening to white on hover.

### Chips & Badges
- **Status Pills**: Height 24px, pill-shaped (9999px), background `rgba(6, 182, 212, 0.12)`, border 1px solid `rgba(6, 182, 212, 0.3)`, text `#06B6D4` (`label-sm`).
- **Live Indicator**: Includes an 6px pulsating dot utilizing primary cyan with keyframe opacity fading.

### Data Cards & Media Panels
- **Metric Tiles**: Single-stat cards containing an overline label in muted zinc, a large numeric display using `stat-counter` highlighted in electric violet or cyan, and an integrated sparkline or micro-icon at top right.
- **Recent Watch Entry**: Horizontal row or card featuring a 48x64px poster asset (rounded-sm), episode counter (`Ep. 17`), relative time stamp (`2h ago`), title in bold white, and an animated audio/video equalizer bar on active scrobbles.

### Progress & Stat Meters
- **Genre DNA Bars**: Base tracks rendered in `rgba(255, 255, 255, 0.06)` with 6px height and fully rounded caps. Progress fills utilize dual-stop gradients (e.g., `#6366F1` to `#06B6D4`) accompanied by right-aligned percentage metadata.

### Form Inputs & Search
- **Search Bar**: 44px height, background `#131B2A`, border `1px solid rgba(255, 255, 255, 0.08)`, placeholder `#64748B`. Focus state transitions border to `#06B6D4` with `0 0 0 3px rgba(6, 182, 212, 0.15)`.