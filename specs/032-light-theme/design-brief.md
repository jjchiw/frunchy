# Design brief: Light theme (feature 032)

For the frunchy_designer session. Spec: `light-theme.spec.md` (same folder). Night Broadcast stays the system
(`design/night_broadcast/DESIGN.md`): same roles (amber = the person the page is about and the primary action, rose
= other people, spoilers, warnings, lilac = tags, coral = errors), solid fills, no gradients, plain copy. Tests read
only `data-*` hooks; the look is yours.

## What is decided (technical)

- Default follows the system (`prefers-color-scheme`); a footer switch Light / Dark / System overrides it (cookie
  `frunchy.theme`), rendered by the server as `<html data-theme>`, so no flash and no script.
- The dark palette keeps every name and value (the browser extension shares it). The light palette gives a value
  for **every** colour token, under the same names, plus light versions of the colour-bearing effects
  (`--border-micro`, `--border-ghost`, `--edge-highlight`, `--shadow-level-2`, `--glow-primary`, and the
  `::selection` tint).
- Every Tailwind colour becomes `var(--color-X)` (with opacity modifiers), so every page follows the palette
  without per-page changes. Pages that use colour roles correctly need nothing else.

## A starting palette (passes AA, please replace freely)

| token | dark (today) | light (candidate) |
|---|---|---|
| surface | #13111a | #faf6f0 |
| surface-container-lowest | #0d0b12 | #ffffff |
| surface-container-low | #1a1722 | #f3ede4 |
| surface-container | #1e1a27 | #ece5da |
| surface-container-high | #2a2435 | #e4dbce |
| surface-container-highest | #352e42 | #dad0c1 |
| on-surface | #f1ece4 | #1d1a24 |
| on-surface-variant | #d2cabe | #4d4656 |
| outline | #9b9286 | #6e655c |
| outline-variant | #4a4255 | #cdc2b3 |
| primary | #ffb547 | #7f4900 |
| on-primary | #2a1700 | #ffffff |
| primary-container | #e0942a | #a86400 |
| primary-fixed | #ffd08a | #965900 |
| secondary | #ff6fa8 | #a01a55 |
| secondary-container | #5c2342 | #f7d3e2 |
| tertiary | #c9bcff | #5a3fb5 |
| error | #ff9b85 | #a02512 |
| on-error | #4a0b00 | #ffffff |
| error-container | #8c1d0e | #ffdad3 |
| on-error-container | #ffdad3 | #5c1007 |

Contrast rules the test enforces in both palettes: on-surface, on-surface-variant, primary, secondary, tertiary and
error at 4.5:1 on surface … surface-container-highest; outline at 4.5:1 (placeholder text) and 3:1 (borders) on
surface, -low and container; on-primary on primary and primary-fixed (the primary button and its hover); on-error
on error; on-error-container on error-container.

The worry with the candidate: amber dark enough to be text on paper reads brown. Options: keep `primary` the text
amber and let fills (bars, the primary button, the mark) use a brighter amber, which would need a new token; or
accept a deeper amber everywhere. Your call; a new token is fine if it earns its place (tell me its name and use).

## Questions

1. The light palette itself (surfaces warm paper? cooler? how far from the night look).
2. Amber as text vs. amber as fill (above).
3. The footer switch: three options next to "Español". A segmented control of three small ghost buttons with icons
   (`light_mode`, `dark_mode`, `contrast`?) and labels, or labels only? The current one marked (`aria-current`).
   It must fit at 360 px in Spanish ("Claro", "Oscuro", "Sistema") together with the language link.
4. The mark in the header and footer: its colours are hard-coded (#ffb547, #e0942a, #ff6fa8 on #13111a slats).
   Should it follow the theme (token colours) on light?
5. Shadows and the glow on light surfaces: keep `--glow-primary` or drop it on light?
6. Poster images get `box-shadow: inset 0 0 0 1px rgb(0 0 0 / 0.4)`; on light, a lighter hairline?
7. Anything else that would look wrong on light that the tokens alone won't fix (`bg-surface/85` header blur,
   charts' trend lines, the invite dialog's backdrop).

## Deliverable

`specs/032-light-theme/design-direction.md` in this worktree
(`/Users/jjchiw/.alta/worktrees/frunchybackend/splendid-izar-mg3m`): the final light values for every token and
effect, answers to the questions, and anything per page. After the build I will send screenshots of every page in
both themes for your sign-off.
