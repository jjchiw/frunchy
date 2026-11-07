# Design direction: Light theme (feature 032)

By the frunchy_designer session, 2026-11-07. Binding for the build. Night Broadcast stays the system: same roles,
solid fills, no gradients, plain copy. The dark palette is untouched in name and value.

## The idea in one line

Night Broadcast by day: **warm paper**, the same violet-black ink, and amber split in two. A deep burnt amber carries
text, links, figures and focus. A bright amber carries the fills (the primary button, bars, the mark). It does not
lean cool or grey: the light page is the same warm room with the lights on.

## 1. The palette

Every pair below was computed against WCAG 2.1, and all of them pass. The worst cases:

| pair | ratio |
|---|---|
| primary on surface-container-highest | 4.90 |
| outline on surface | 4.98 |
| on-primary on primary-fill | 8.49 |
| on-primary on primary-fixed | 7.00 |
| tertiary pill text on its tint | 6.80 |

### Surfaces and text (same names as today)

| token | dark (unchanged) | light | role on light |
|---|---|---|---|
| surface | #13111a | **#efe8dd** | the page, warm paper |
| surface-container-lowest | #0d0b12 | **#e8e0d3** | wells: recessed, darker than the card (empty poster slots, the invite code box) |
| surface-container-low | #1a1722 | **#fdfaf5** | cards, raised above the page (page/card 1.17:1) |
| surface-container | #1e1a27 | **#f6f1e9** | inputs, raised rows, ghost hover |
| surface-container-high | #2a2435 | **#e7dfd2** | secondary button hover, thumbnail placeholders |
| surface-container-highest | #352e42 | **#ddd3c4** | tracks, hover |
| on-surface | #f1ece4 | **#1f1a24** | text (violet-black ink, the dark page's own hue) |
| on-surface-variant | #d2cabe | **#4c4554** | secondary text |
| outline | #9b9286 | **#6b6155** | placeholders, input borders |
| outline-variant | #4a4255 | **#c8bba8** | dividers. Dark enough that the common `/40` and `/50` still show on a card |
| primary | #ffb547 | **#854700** | **text amber**: links, figures, stat counters, selected tab rule, focus ring, airing mark |
| on-primary | #2a1700 | **#2a1700** | the same value: dark text on the bright amber button |
| primary-container | #e0942a | **#d98a0b** | a fill only (mark, pressed). Never text on light |
| primary-fixed | #ffd08a | **#e8920f** | primary button hover: a deeper amber, the opposite direction of dark |
| secondary | #ff6fa8 | **#9c1655** | rose text: other people, spoilers, warnings |
| secondary-container | #5c2342 | **#f9dfe9** | rose wells (secondary text on it is 6.30) |
| tertiary | #c9bcff | **#5636b8** | lilac text: tags, information |
| error | #ff9b85 | **#a1230f** | coral text and the destructive fill |
| on-error | #4a0b00 | **#ffffff** | |
| error-container | #8c1d0e | **#fbe0d8** | |
| on-error-container | #ffdad3 | **#5c1007** | |

### New: three fill tokens (Frunchy only, defined in both themes)

| token | dark | light | used for |
|---|---|---|---|
| primary-fill | #ffb547 (= primary) | **#f5a623** | the primary button's background, every amber bar and meter, the mark, amber tints |
| secondary-fill | #ff6fa8 (= secondary) | **#e85a94** | rose bars (the other person on compare), the mark's last bar, rose tints |
| tertiary-fill | #c9bcff (= tertiary) | **#9d86ee** | lilac bars (Progress' Watching segment), lilac tints |

Rules for the new tokens:
- **In dark, each fill equals its role's value, so the dark site renders identically, byte for byte in colour.**
- They are not shared with the extension. Keep them in their own list (`Theme.fillColors` and `Theme.lightFillColors`), outside `Theme.colors`, so the parity test keeps comparing only the shared names.
- Add them to the Tailwind config like the others (`bg-primary-fill`, `bg-primary-fill/[0.14]`, and so on).

**The rule for the build, applied once in components:**
- A **solid area or data mark** uses the fill token. That covers `bg-primary`, `bg-secondary`, `bg-tertiary`, `.fr-fill`, `toneBar`, `levelMeter`, chart and pace bars, the months chart, the Progress counts bar, `fr-btn-primary`'s background, and the mark.
- A **tint** (pill backgrounds, `fr-alert-*` backgrounds and borders, selection) uses the fill at the **same alpha as today**. Dark therefore does not move.
- **Text, links, borders that mark a state, the focus ring and icons** keep the role token (`text-primary`, `border-primary`, `:focus-visible`).
- The test pair "on-primary on primary" becomes **on-primary on primary-fill** and on primary-fixed, in both palettes. In dark this is the same check as today, because the fill equals primary.

Why a new token rather than one amber: bright amber as text on paper fails (1.67:1). Deep amber as the button and bars turns the whole site brown, and the candidate showed exactly that. Two values, one role.

## 2. Effects (light values; dark unchanged)

| effect | dark | light |
|---|---|---|
| --border-micro | 1px solid rgb(241 236 228 / 0.06) | 1px solid rgb(31 26 36 / 0.08) |
| --border-ghost | 1px solid rgb(241 236 228 / 0.14) | 1px solid rgb(31 26 36 / 0.20) |
| --edge-highlight | rgb(255 181 71 / 0.35) | rgb(217 138 11 / 0.40) |
| --shadow-level-2 | 0 12px 32px -8px rgb(0 0 0 / 0.6) | 0 12px 32px -8px rgb(70 50 25 / 0.18) |
| --glow-primary | 0 4px 20px -4px rgb(255 181 71 / 0.45) | 0 4px 14px -4px rgb(133 71 0 / 0.30) |
| **--poster-edge** (new) | inset 0 0 0 1px rgb(0 0 0 / 0.4) | inset 0 0 0 1px rgb(31 26 36 / 0.12) |
| **--mark-frame** (new) | var(--color-primary-fill) | var(--color-primary-container) |
| ::selection | rgb(255 181 71 / 0.3) | the primary-fill at 30% (color-mix), text on-surface |
| theme-color meta | #13111a | #efe8dd |

The light shadow is warm brown at a low alpha, never black. On paper, black shadows look dirty.

## 3. Answers

1. **Palette**: warm paper, not cool and not white. The page is a cream (#efe8dd) and cards are a lighter cream (#fdfaf5) raised above it. Ink is the dark page's own violet-black. It sits as far from the night look as the light switch, and no further.
2. **Amber**: split into text and fill, with the three fill tokens above. The amber button keeps dark text (on-primary is unchanged), so the primary action looks like the same amber pill in both themes.
3. **The footer switch**: a segmented control with **labels only**, no icons.
   - Markup: `<nav aria-label="Theme" data-theme-switch>`, holding a `div class="inline-flex items-center rounded-full p-0.5"` with `border: var(--border-ghost)`, and three links inside.
   - Each link: `inline-flex h-8 items-center rounded-full px-3 text-label-md`.
   - Current one: `bg-surface-container-high text-on-surface` plus `aria-current="true"`. It is a neutral state, so it is not amber.
   - Others: `text-on-surface-variant`, with hover `text-on-surface`.
   - Copy: "Light · Dark · System" / "Claro · Oscuro · Sistema", and aria-label "Theme" / "Tema". No separators are rendered; the pill holds them.
   - Why no icons: "System" has no icon people read reliably (`contrast` and `brightness_auto` both need learning). Three words fit, and the language link already carries the footer's one icon.
   - Placement: the language link and the theme switch sit together in one `flex flex-wrap items-center gap-x-3 gap-y-2` group, the outer row's second child (as 024 left it, `sm:ml-auto`). Order: theme switch, then language.
   - At 360 in Spanish the switch measures about 200px and the language link about 110px. Inside the 328px row they wrap onto two lines, each aligned left with the text above (the `-ml-3.5` on the language link stays). At 1280 they share one line at the right edge.
4. **The mark follows the theme**, through one extra CSS variable so dark does not move.
   - Add `--mark-frame`: dark `var(--color-primary-fill)` (today's #ffb547), light `var(--color-primary-container)` (#d98a0b). Light uses the deeper amber because it stands out more on cream than the bright one does (2.27:1 against 1.67:1). A brand mark is exempt from the ratio, but it has to hold its shape.
   - Clapper and frame use `--mark-frame`.
   - The bars use primary-fill, primary-container, primary-container and secondary-fill, as today.
   - The slats (the cut-outs) use `surface`.
   - **The favicon keeps its dark tile** (spec FR-005).
5. **Shadows and glow on light**:
   - Keep the glow token, but on light it is the small warm offset shadow above, not a halo. A coloured glow on paper reads as a stain.
   - Cards on light still have no border. The 1.17:1 step from page to card carries them, as the step does on dark.
6. **Poster hairline**: yes, lighter, through the new `--poster-edge` effect (12% ink on light). Replace the hard-coded inline `box-shadow` in `Components.fs` with `box-shadow: var(--poster-edge)`.
7. **What the tokens alone won't fix**, all handled above or here:
   - The header's `bg-surface/85` with `backdrop-blur`: it follows the token and is fine on cream. Keep it.
   - The invite dialog's `backdrop:bg-surface/80`: this goes cream at 80% on light, which is right. Its card shadow is the light shadow-level-2.
   - The charts' trend lines use `currentColor` from `text-primary`, so they draw in deep amber on light. Keep them; a line is not a fill.
   - Hard-coded colours to replace: the poster inline shadow, the `::selection` colour, and the `theme-color` meta (two metas with `media` for `system`, as the spec says). Google's "G" stays.
   - Any `text-primary-container` or `text-primary-fixed` in a view would fail on light. Use `text-primary`. (I found none, but grep before merging.)
   - Bars on tracks: bright amber on the track is 1.37:1 by luminance, but it differs strongly in hue. Every bar on the site sits next to its number, so the information never depends on the bar. Accepted. Don't darken the fill to chase the ratio.

## 4. Per page

- **Home**: the headline's "in numbers" is text-primary and becomes deep amber on light. That is right: it is text. The large decorative mark carries the bright amber.
- **Profile pages** (dashboard, DNA, year, progress): the stat counters are text-primary (deep amber), and every bar, the pace chart, the months chart and the Progress counts bar take the fills. The selected tab's rule stays `border-primary` (deep amber, 5.95:1 on the page), so selection reads clearly.
- **Compare**: amber is the viewer and rose is the other person, both as fills on bars and as text on names. Check the legend dots use the fills.
- **Charts**: trend lines in deep amber, and movement colours as their text roles.
- **Sign-in and the invite dialog**: the cream backdrop and the light shadow. The provider buttons are secondary buttons with the ghost border.
- **Footer**: the switch as specified in answer 3, and the mark through the theme's tokens.
- **Error, 404 and message pages**: the icon circle `bg-surface-container-high text-primary` gives a deep amber icon on light beige, which is fine.

## 5. What I will check in the screenshots

Every page at 1280 and 360 in light, plus home, dashboard, DNA, compare, progress and sign-in in dark too, to prove that dark did not move. Also the footer in Spanish at 360 in both themes, and one crop of the mark in the header on light.
