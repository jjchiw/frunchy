module FrunchyBackend.Views.Theme

open System

// ---------------------------------------------------------------------------
// Design tokens: "Night Broadcast" (design/night_broadcast/DESIGN.md, specs/010-night-broadcast).
// Names and values are shared with the browser extension's src/styles/tokens.css
// (specs/010-night-broadcast/contracts/design-system.md); DesignSystemTests keeps the two equal.
// ---------------------------------------------------------------------------

let colors =
    [ "surface", "#13111a"
      "surface-container-lowest", "#0d0b12"
      "surface-container-low", "#1a1722"
      "surface-container", "#1e1a27"
      "surface-container-high", "#2a2435"
      "surface-container-highest", "#352e42"
      "on-surface", "#f1ece4"
      "on-surface-variant", "#d2cabe"
      "outline", "#9b9286"
      "outline-variant", "#4a4255"
      "primary", "#ffb547"
      "on-primary", "#2a1700"
      "primary-container", "#e0942a"
      "primary-fixed", "#ffd08a"
      "secondary", "#ff6fa8"
      "secondary-container", "#5c2342"
      "tertiary", "#c9bcff"
      "error", "#ff9b85"
      "on-error", "#4a0b00"
      "error-container", "#8c1d0e"
      "on-error-container", "#ffdad3" ]

/// Type styles: size, line height, weight, tracking
let typeStyles =
    [ "display-hero", ("48px", "56px", "800", "-0.035em")
      "display-hero-mobile", ("32px", "40px", "800", "-0.03em")
      "headline-xl", ("36px", "44px", "800", "-0.03em")
      "headline-xl-mobile", ("26px", "34px", "700", "-0.02em")
      "headline-lg", ("24px", "32px", "700", "-0.02em")
      "headline-md", ("20px", "28px", "700", "-0.015em")
      "stat-counter", ("32px", "36px", "800", "-0.02em")
      "body-lg", ("16px", "24px", "400", "0em")
      "body-md", ("14px", "20px", "400", "0em")
      "label-md", ("13px", "18px", "600", "0.01em")
      "label-sm", ("11px", "14px", "700", "0.06em") ]

let radii =
    [ "sm", "0.375rem"
      "default", "0.75rem"
      "md", "1rem"
      "lg", "1.25rem"
      "xl", "1.75rem"
      "full", "9999px" ]

let spaces =
    [ "xs", "0.25rem"
      "sm", "0.5rem"
      "md", "1rem"
      "lg", "1.5rem"
      "xl", "2rem" ]

let effects =
    [ "--border-micro", "1px solid rgb(241 236 228 / 0.06)"
      "--border-ghost", "1px solid rgb(241 236 228 / 0.14)"
      "--edge-highlight", "rgb(255 181 71 / 0.35)"
      "--shadow-level-2", "0 12px 32px -8px rgb(0 0 0 / 0.6)"
      "--glow-primary", "0 4px 20px -4px rgb(255 181 71 / 0.45)"
      "--font-family", "\"Figtree\", system-ui, sans-serif"
      "--font-display", "\"Bricolage Grotesque\", \"Figtree\", system-ui, sans-serif"
      "--font-mono", "\"IBM Plex Mono\", ui-monospace, monospace" ]

/// Every custom property, as the extension names them
let tokens: (string * string) list =
    [ for name, value in colors -> $"--color-{name}", value
      for name, (size, line, weight, tracking) in typeStyles do
          yield $"--text-{name}-size", size
          yield $"--text-{name}-line", line
          yield $"--text-{name}-weight", weight
          yield $"--text-{name}-tracking", tracking
      for name, value in radii -> $"--radius-{name}", value
      for name, value in spaces -> $"--space-{name}", value
      yield! effects ]

let tokensCss =
    let declarations = tokens |> List.map (fun (name, value) -> $"{name}: {value};") |> String.concat " "
    $":root {{ color-scheme: dark; {declarations} }}"

// ---------------------------------------------------------------------------
// Tailwind (CDN, no build step): utilities resolve to the same values, so mockup classes such as
// bg-surface-container-low, text-primary/80 or rounded-lg map onto the tokens.
// ---------------------------------------------------------------------------

let private js (s: string) = "'" + s.Replace("'", "\\'") + "'"

let tailwindConfig =
    let colorEntries = colors |> List.map (fun (n, v) -> $"{js n}: {js v}") |> String.concat ", "

    let fontSizes =
        typeStyles
        |> List.map (fun (n, (size, line, weight, tracking)) ->
            $"{js n}: [{js size}, {{ lineHeight: {js line}, fontWeight: {js weight}, letterSpacing: {js tracking} }}]")
        |> String.concat ", "

    let radiusEntries =
        radii
        |> List.map (fun (n, v) -> $"""{js (if n = "default" then "DEFAULT" else n)}: {js v}""")
        |> String.concat ", "

    $"""tailwind.config = {{ theme: {{ extend: {{
  colors: {{ {colorEntries} }},
  fontSize: {{ {fontSizes} }},
  borderRadius: {{ {radiusEntries} }},
  fontFamily: {{ sans: ['"Figtree"', 'system-ui', 'sans-serif'], display: ['"Bricolage Grotesque"', '"Figtree"', 'system-ui', 'sans-serif'], mono: ['"IBM Plex Mono"', 'ui-monospace', 'monospace'] }},
  maxWidth: {{ page: '1360px' }},
  boxShadow: {{ 'level-2': 'var(--shadow-level-2)', 'glow-primary': 'var(--glow-primary)' }}
}} }} }};"""

/// Shared components (Views/Components.fs) and the browser surfaces nobody draws
let componentCss =
    """
@layer base {
  html { background: var(--color-surface); -webkit-font-smoothing: antialiased; }
  body { @apply bg-surface text-on-surface font-sans text-body-lg; font-variant-numeric: tabular-nums; }
  ::selection { background: rgb(255 181 71 / 0.3); color: var(--color-on-surface); }
  * { scrollbar-color: var(--color-surface-container-highest) var(--color-surface); }
  :focus-visible { outline: 2px solid var(--color-primary); outline-offset: 2px; border-radius: var(--radius-sm); }
  a { text-underline-offset: 3px; }
  h1, h2, h3 { font-family: var(--font-display); }
  input, textarea { caret-color: var(--color-primary); }
}
@layer components {
  .fr-card { @apply bg-surface-container-low rounded-lg p-6 md:p-8; }
  .fr-btn { @apply inline-flex h-11 items-center justify-center gap-2 rounded-full px-5 text-body-md font-semibold whitespace-nowrap; }
  .fr-btn:disabled, .fr-btn[aria-disabled="true"] { @apply opacity-50 cursor-not-allowed; }
  .fr-btn-primary { @apply bg-primary text-on-primary; }
  .fr-btn-secondary { @apply bg-transparent text-on-surface; border: var(--border-ghost); }
  .fr-btn-ghost { @apply text-on-surface-variant; }
  .fr-btn-destructive { @apply bg-error text-on-error; }
  .fr-btn-sm { @apply h-9 px-3.5 text-label-md; }
  .fr-label { @apply block text-label-sm uppercase text-on-surface-variant mb-1.5; }
  .fr-input { @apply w-full h-11 rounded bg-surface-container border border-outline px-3.5 text-body-md text-on-surface placeholder:text-outline; }
  .fr-input:focus { @apply border-primary outline-none; }
  .fr-input[aria-invalid="true"] { @apply border-error; }
  .fr-hint { @apply mt-1.5 text-label-md font-normal text-on-surface-variant; }
  .fr-field-error { @apply mt-1.5 flex items-center gap-1.5 text-label-md text-error; }
  .fr-alert { @apply flex items-start gap-3 rounded p-4 text-body-md border; }
  .fr-alert-success { @apply bg-primary/10 border-primary/30 text-on-surface; }
  .fr-alert-error { @apply bg-error/10 border-error/40 text-error; }
  .fr-alert-info { @apply bg-secondary/10 border-secondary/30 text-on-surface; }
  .fr-pill { @apply inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-label-md; }
  .fr-pill-primary { @apply bg-primary/[0.14] text-primary border-primary/25; }
  .fr-pill-secondary { @apply bg-secondary/[0.16] text-secondary border-secondary/25; }
  .fr-pill-tertiary { @apply bg-tertiary/[0.14] text-tertiary border-tertiary/25; }
  .fr-pill-muted { @apply bg-surface-container-high text-on-surface-variant border-outline-variant; }
  .fr-overline { @apply text-label-sm uppercase text-on-surface-variant; }
  .fr-track { @apply h-1.5 w-full overflow-hidden rounded-full bg-surface-container-highest; }
  .fr-fill { @apply h-full w-full rounded-full origin-left bg-primary; }
  .fr-link { @apply text-primary underline decoration-primary/40 hover:decoration-primary; }
  .fr-mono { @apply font-mono; }
  .fr-stat, .text-stat-counter { font-family: var(--font-mono); }
  .text-display-hero, .text-display-hero-mobile, .text-headline-xl, .text-headline-xl-mobile, .text-headline-lg, .text-headline-md { font-family: var(--font-display); }
  .material-symbols-outlined { font-size: 1.25em; line-height: 1; vertical-align: -0.2em; user-select: none; }
}
@media (prefers-reduced-motion: no-preference) {
  .fr-btn, .fr-input, .fr-link, .fr-nav-link { transition: background-color 160ms cubic-bezier(0.16, 1, 0.3, 1), border-color 160ms cubic-bezier(0.16, 1, 0.3, 1), box-shadow 200ms cubic-bezier(0.16, 1, 0.3, 1), color 160ms; }
  .fr-btn-primary:hover { box-shadow: var(--glow-primary); }
  .fr-fill { transition: transform 600ms cubic-bezier(0.16, 1, 0.3, 1); }
}
.fr-btn-primary:hover { @apply bg-primary-fixed; }
.fr-btn-secondary:hover { @apply bg-surface-container-high; }
.fr-btn-ghost:hover { @apply text-on-surface bg-surface-container; }
.fr-btn-destructive:hover { @apply brightness-110; }
"""

// ---------------------------------------------------------------------------
// Fonts, icons and the mark
// ---------------------------------------------------------------------------

/// Material Symbols used by the views (Components.icon); the font is subset to exactly these
type Icon =
    | ArrowBack
    | ArrowForward
    | CalendarMonth
    | CheckCircle
    | ContentCopy
    | DeleteForever
    | ErrorMark
    | ExploreOff
    | Extension
    | Fingerprint
    | History
    | Home
    | Info
    | Key
    | Link
    | LinkOff
    | LocalFireDepartment
    | Lock
    | OpenInNew
    | ProgressActivity
    | Schedule
    | Shield
    | Star
    | Sync
    | Warning

    /// The Material Symbols ligature
    member this.Name =
        match this with
        | ArrowBack -> "arrow_back"
        | ArrowForward -> "arrow_forward"
        | CalendarMonth -> "calendar_month"
        | CheckCircle -> "check_circle"
        | ContentCopy -> "content_copy"
        | DeleteForever -> "delete_forever"
        | ErrorMark -> "error"
        | ExploreOff -> "explore_off"
        | Extension -> "extension"
        | Fingerprint -> "fingerprint"
        | History -> "history"
        | Home -> "home"
        | Info -> "info"
        | Key -> "key"
        | Link -> "link"
        | LinkOff -> "link_off"
        | LocalFireDepartment -> "local_fire_department"
        | Lock -> "lock"
        | OpenInNew -> "open_in_new"
        | ProgressActivity -> "progress_activity"
        | Schedule -> "schedule"
        | Shield -> "shield"
        | Star -> "star"
        | Sync -> "sync"
        | Warning -> "warning"

let iconNames =
    Reflection.FSharpType.GetUnionCases typeof<Icon>
    |> Array.map (fun case -> (Reflection.FSharpValue.MakeUnion(case, [||]) :?> Icon).Name)
    |> List.ofArray

let fontStylesheets =
    [ "https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,600..800&family=Figtree:wght@400;600;700&family=IBM+Plex+Mono:wght@500;600&display=swap"
      // display=block: the ligature names never flash as words while the font loads
      "https://fonts.googleapis.com/css2?family=Material+Symbols+Outlined:opsz,wght,FILL,GRAD@24,400,0,0&display=block&icon_names="
      + (iconNames |> List.sort |> String.concat ",") ]

/// The Frunchy mark (design/brand/frunchy-mark.svg): a clapperboard whose body is a bar chart
let markSvg =
    """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" fill="none" aria-hidden="true" focusable="false"><g transform="rotate(-14 6 15)"><rect x="6" y="9" width="35" height="7" rx="2" fill="#ffb547"/><path d="M14 9 L11 16 M23 9 L20 16 M32 9 L29 16" stroke="#13111a" stroke-width="2.6" stroke-linecap="square"/></g><path d="M6 18 H40 M6 18 V42 H11" stroke="#ffb547" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"/><rect x="14" y="31" width="4" height="11" rx="1" fill="#ffb547"/><rect x="21" y="26" width="4" height="16" rx="1" fill="#e0942a"/><rect x="28" y="21" width="4" height="21" rx="1" fill="#e0942a"/><rect x="35" y="23" width="4" height="19" rx="1" fill="#ff6fa8"/></svg>"""

/// Favicon: the mark on a dark tile, so it stays visible on light browser tabs (as the extension's icons)
let faviconHref =
    let inner = markSvg.Replace("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" fill="none" aria-hidden="true" focusable="false">""", "").Replace("</svg>", "")

    let tile =
        $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="-4 -4 56 56"><rect x="-4" y="-4" width="56" height="56" rx="12" fill="#13111a"/>{inner}</svg>"""

    "data:image/svg+xml;base64," + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes tile)
