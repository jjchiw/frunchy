# Contract: the shared design system, Night Broadcast (website ↔ browser extension)

Supersedes the **values** of `specs/004-anime-dna-design-system/contracts/design-system.md`. Every
token **name** there stays. One name is added: `--font-display`. The rules on components, test hooks
and accessibility in 004's contract still apply.

## Tokens both sides define (name → value)

```css
:root {
  color-scheme: dark;

  --color-surface: #13111a;
  --color-surface-container-low: #1a1722;
  --color-surface-container: #1e1a27;
  --color-surface-container-high: #2a2435;
  --color-surface-container-highest: #352e42;
  --color-on-surface: #f1ece4;
  --color-on-surface-variant: #d2cabe;
  --color-outline: #9b9286;
  --color-outline-variant: #4a4255;
  --color-primary: #ffb547;
  --color-on-primary: #2a1700;
  --color-primary-fixed: #ffd08a;
  --color-secondary: #ff6fa8;
  --color-tertiary: #c9bcff;
  --color-error: #ff9b85;
  --color-error-container: #8c1d0e;
  --color-on-error-container: #ffdad3;

  --font-family: "Figtree", system-ui, sans-serif;            /* text */
  --font-display: "Bricolage Grotesque", "Figtree", system-ui, sans-serif;  /* headings, wordmark (new) */
  --font-mono: "IBM Plex Mono", ui-monospace, monospace;      /* figures, usernames, keys, URLs */

  --radius-sm: 0.375rem;
  --radius-default: 0.75rem;
  --radius-md: 1rem;
  --radius-lg: 1.25rem;
  --radius-full: 9999px;
}
```

**Type styles** (`--text-<style>-size|line|weight|tracking`) keep their sizes and line heights;
`headline-md` weight is `700` and tracking `-0.015em`. Spacing values are unchanged.

**Website-only tokens** (the extension takes the same name and value if it ever needs one):
`surface-container-lowest` `#0d0b12`, `primary-container` `#e0942a`, `secondary-container`
`#5c2342`, `on-error` `#4a0b00`, `radius-xl` `1.75rem`. The effects are:
- `--border-micro`: `1px solid rgb(241 236 228 / 0.06)`
- `--border-ghost`: `1px solid rgb(241 236 228 / 0.14)`
- `--edge-highlight`: `rgb(255 181 71 / 0.35)`
- `--glow-primary`: `0 4px 20px -4px rgb(255 181 71 / 0.45)`
- `--shadow-level-2`: unchanged

## Fonts

| Side | How |
|---|---|
| Website | Google Fonts: `Bricolage Grotesque` (opsz 12–96, 600–800), `Figtree` (400, 600, 700), `IBM Plex Mono` (500, 600), `display=swap` |
| Extension | self-hosted, e.g. `@fontsource-variable/bricolage-grotesque`, `@fontsource-variable/figtree`, `@fontsource/ibm-plex-mono` (its call). `--font-family` may name the variable family (`"Figtree Variable"`), as today; the parity check ignores `--font-family` |

## Rules that both sides follow

- **Colour roles**: amber (`primary`) is the person the screen is about, and the primary action.
  Rose (`secondary`) is other people, plus spoilers and warnings. Lilac (`tertiary`) is tags and
  information. Errors use coral (`error`), never rose.
- **Fills**: solid only, no gradients, the genre bar included.
- **Shapes**: buttons and pills are fully rounded; cards use `radius-lg`.
- **Labels**: no eyebrow labels or decorative overlines above headings.
- **Accessibility**: WCAG 2.1 AA on every text/background pair, and a visible 2px amber focus ring.

## Clarifications (agreed with the extension session, 2026-10-15)

- **"Figures" in IBM Plex Mono** means *standalone* figures: metric tiles, stat counters, a big
  number on its own. Numbers inside a sentence ("3 pages waiting", "1,204 processed", date ranges)
  stay in the text font with `tabular-nums`. Mono is also used for keys, URLs and usernames.
- **Your own data that sits next to a primary action** is shown as a lilac tag (`tertiary`), so
  amber stays with the action. Example: the extension's Crunchyroll profile pill.
- **Neutral states that need attention but aren't warnings** use lilac (`tertiary`). Examples:
  "waiting to upload", "processing". Rose (`secondary`) is for real warnings and for spoilers.
  Synced or done is amber, and stored or idle is `on-surface-variant`.
- **Section headings** are sentence case, never uppercase-tracked. A small uppercase label is only
  allowed as the label of a value (a metric tile's name), never as a heading or above one.
- **Selection** is `::selection { background: rgb(255 181 71 / 0.3); color: var(--color-on-surface); }`.
- **Tints** can be derived from tokens with `rgb(from var(--color-primary) r g b / a)`. The website
  uses Tailwind's `primary/10` for the same thing. There are no tint tokens.

## Check

`DesignSystemTests.every token shared with the extension has the same value`: every token in the
extension's `tokens.css`, except `--font-family`, exists on the website with the same value.
