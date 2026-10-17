# Mockup review: what to build from each design

The designer delivered the screens of `BRIEF.md` on 2026-10-08. The two Stitch zips are unpacked
into the folders below. This file is the working guide for feature 004 (and for the extension
session's screens).

## Rules for every page

- **Take layout and components; never take copy.** Texts stay the site's plain texts in
  `Views/Texts.fs`, in English and Spanish. The mockups' "telemetry" voice ("Telemetry Ingestion",
  "Scrobble checkpoints", "node", "protocol", "v2.4") doesn't ship.
- **Never ship a false claim**: "zero-knowledge secure handshake", "end-to-end telemetry token",
  "CR Webhook", "Realtime Sync Connected", "Verified Scrobbler", "Metadata verified".
- **Never show what has no data source** (constitution): avatars, provider usernames or emails,
  rankings, versions or build numbers, fake samples or charts, invented ids, "last tracked stream"
  on pages without a user.
- **Every link must work.** Drop every link without a real route: Live Scrobbles, Charts, Community,
  Docs, How it works, search, Leaderboards, Telemetry API, Privacy & Terms, Explore public demo,
  View documentation, Manual batch import.
- **Tokens**: use ours (`Views/Theme.fs`, the same names as the extension). The radius `lg` is
  **1rem**, even though the mockups' configs use 0.5rem. JetBrains Mono appears only for keys,
  usernames and URLs (the `--font-mono` token).
- **Reviewer toggles** in the mockups ("Toggle session", "Reviewer state simulator", "Toggle empty
  view", "Switch variants") aren't UI. They show the states each page must support.

## Brand

| Asset | Decision |
|---|---|
| `brand/frunchy-mark.svg` | **The mark**, chosen by the owner: the designer's clapperboard + bars (`frunchy_brand_logo_wordmark/`), redrawn as SVG in the tokens (primary, primary-container, secondary). Used in the navigation next to the "Frunchy" wordmark, as the favicon, and for the extension icons. |
| `frunchy_brand_logo_wordmark/` | Reference raster only; drop the "telemetry tracking" tagline. |
| `frunchy_brand_logo/` (gradient "F" SVG) | Not used (off-palette purple and pink). |
| The components sheet's list glyph and "F"/"Fr" squares | Not used. |

## Shared

| Folder | Use for | Keep | Drop |
|---|---|---|---|
| `frunchy_base_components_sheet/` | `Views/Components.fs` | primary, secondary, ghost and destructive buttons; Google and Umbral buttons (Discord was removed in feature 014); field with hint and error; success and error alerts; processing line; empty state with action; destructive confirmation | "YAML Token Compliant" badge, "telemetry" wording in examples |
| `frunchy_public_dashboard_u_username/` (navigation and footer) | `Views/Layout.fs` | sticky navigation with mark + wordmark, "Sign in" or "Account / Sign out"; footer with "Frunchy, a Last.fm for anime" | "Telemetry Ingestion Active", nav links other than the dashboard (when signed in) and the DNA page (on a profile), search, avatar, versions, © lines with invented company names |

## Website pages

| Page (route) | Folder | Keep | Drop or change |
|---|---|---|---|
| Home (`/`) | `frunchy_home/` | hero "Your anime identity, in numbers" + intro; "Sign in" CTA; the public-dashboards note; "How to sync" as 3 numbered cards (our 3 steps); the deletion banner state; the signed-in variant | the "Telemetry sample" stats and velocity chart, "Explore public demo", key/URI/status chips in the steps, the "Private ingestion" block's "View documentation" (keep one plain sentence: no Crunchyroll password is ever asked), footer links |
| Sign in (`/signin`) | `frunchy_sign_in_signin/` | centered card, overline + title + intro, error alert, Google and Umbral buttons, "Back to home" | — |
| Sign up (`/signup`) | `frunchy_create_account_signup/` | the three fields with errors; the live "frunchy…/u/{username}" preview (Alpine, the real host); "I already have a Frunchy account" | "Invite-only beta" chip only if the copy exists in texts (it's true: keep as a pill) |
| Claim (`/claim`) | `frunchy_attach_existing_account_claim/` | sync key field (mono), error, the two links | — |
| Confirm it's you (`/signin/reconfirm`) | `frunchy_confirm_identity_signin_reconfirm/` | card, error state, "Continue with {provider}", return link | — |
| Account (`/account`) | `frunchy_your_account_account/` | card stack: profile (username locked, dashboard link, display name + Save), extension (key state + Connect extension), Crunchyroll binding (+ Reset), sign-in methods (provider rows, Link, Remove; Remove disabled on the last), recent syncs (time, status pill, counts), security activity, danger zone; success banners | provider usernames and emails (we store none), "OAuth2 Primary Token", "SSO Authenticator", batch timings, "NODE // 0x" labels, "Toggle empty view" |
| Connect extension (`/account/key`) | `frunchy_connect_extension_account_key/` | step 1 confirm (title, warning, Create sync key, cancel); step 2 the key in a mono box with copy, "shown only once" warning, the 3 next steps, back link | "zero-knowledge secure handshake token", "API Authorization Secret", "Live Key", emoji |
| Delete account (`/account/delete`) | `frunchy_delete_account_account_delete/` | warning header, the list of what is deleted (our `DeleteWarning` items), type-your-username field and error, destructive button, cancel | "Telemetry ID // 0x4829", "telemetry clusters", "Purge manifest" wording |
| Message pages (404, 500, 429, expired form) | `frunchy_system_message_pages_404_500_rate_limit_expired/` | centered card: icon, status overline (e.g. "404"), title, body, "Back to home" | "SYNC_FAIL", "// CARRIER_DROP…", "System Telemetry Matrix", node/version, "Last tracked stream" card |
| Dashboard (`/u/{username}`) | `frunchy_public_dashboard_u_username/` (screenshot broken; read `code.html`) | header with display name and **View Anime DNA** + **Copy profile link**; processing banner; 4 metric tiles; recent cards with thumbnail, progress, relative time, title + episode, **studio, format and year**, MAL score pill and genres; the **unmatched** card variant (no MAL data); Top genres bars with the coverage note; empty and not-found states | tile sub-captions ("Confirmed Unique Franchises", "Total Scrobble Checkpoints", …), "Scrobbled via Extension", "Raw Stream" / "Telemetry Pending Database Match" wording (use a plain "Not on MyAnimeList yet") |
| Anime DNA (`/u/{username}/dna`) | `frunchy_anime_dna_profile_u_username_dna/` (screenshot broken; read `code.html`) | header + Copy link; tiles: total watch time (hours + days), fully watched episodes, MyAnimeList coverage %; insignia card (poster, title, format, studio, season, fully watched count / MAL episodes, hours); pace card (episodes per day, steady/in bursts, **a 90-day bar chart of episodes per day**); studio with runner-up and coverage; decade with the full decade breakdown; genres top 5 + Others with each top anime; formats; the cold-start state (count out of **15**, "Open extension setup" → the home page's how-to-sync) | "Verified Scrobbler #", "Profile Diagnostic", "Telemetry Engine", "Consumption velocity", "Active track", "82% active days" (we use 9 of 13 weeks), "CR Webhook", "Metadata verified via entry #", "Manual batch import", the genre note's "across fully watched episodes" (ours: by watch time) |
| `anime_dna_perfil_frunchy/`, `dashboard_frunchy/` | — | superseded by the two rows above (kept for history) | — |

## Extension (owned by the extension session)

| Folder | Notes |
|---|---|
| `frunchy_extension_popup_states_360px/` | Drawn at 360px; the real popup is 380 × max 600px. Same rules: plain copy, no invented data. |
| `frunchy_extension_options_preferences/` | Options page proposals; see BRIEF.md §6.2 for what's built. |
| `extensi_n_frunchy_popup_sincronizador/` | The original popup mockup (screenshot now fixed). |
| `brand/frunchy-mark.svg` | Source for the extension icons (16, 32, 48, 128) and the popup header mark. |
