# Design brief: the screens Frunchy still needs

Frunchy is a "Last.fm for anime". A browser extension reads a user's Crunchyroll watch history, and
the website turns it into statistics, enriched with MyAnimeList data. Three mockups exist:

| Mockup | Folder | State |
|---|---|---|
| Public dashboard | `dashboard_frunchy/` | ok |
| Anime DNA profile | `anime_dna_perfil_frunchy/` | `screen.png` is broken (the file is the text "FIFE Image failed to fetch"): re-export it |
| Extension popup | `extensi_n_frunchy_popup_sincronizador/` | ok |

Every other page the site already serves has no design and still uses a default theme. This brief
lists them, plus the missing states of the existing three, and gives one prompt per screen.

## 1. Paste this context into every prompt

> Frunchy, a "Last.fm for anime". Dark theme only. Use the "Neo-Scrobble Profile" design system:
> Plus Jakarta Sans everywhere; background `#0f131c`; surfaces `#181c24` / `#1c2028` / `#262a33`;
> text `#dfe2ee`, muted `#bcc9cd`, outline `#3d494c`; primary cyan `#4cd7f6` (live states, key stats,
> main buttons, text on it `#003640`); secondary violet `#c0c1ff` (categories, sub-metrics);
> tertiary pink `#ffb0cd` (highlights); error `#ffb4ab`. Radii: 8px for inputs, badges and
> posters; 16px for cards and modals; pills fully rounded. 12-column grid, max width 1360px.
> Overline labels are 11px bold uppercase with wide tracking; big numbers are 32px extra-bold with
> tabular figures. Copy in English (a Spanish version ships too, about 30% longer: leave room).
> Show only the data listed; no avatars, levels, badges, followers, ratings, rankings or trends.

## 2. Decisions for the design system (answer once)

1. **One set of values.** `neo_scrobble_profile/DESIGN.md` contradicts itself. The YAML says primary
   `#4cd7f6` and secondary `#c0c1ff`; its prose says `#06B6D4` and `#6366F1`. The radius scale in the
   mockups' Tailwind config (`lg` = 0.5rem) differs from the YAML (`lg` = 1rem). **We propose the
   YAML values** (used above); confirm or correct, and fix `DESIGN.md` to match.
2. **Brand**: a logo and wordmark (the site shows a 🎬 emoji today), a favicon, and the extension
   icon at 16, 32, 48 and 128 px.
3. **Base components**, on one sheet: buttons (primary, secondary, destructive, and for "Sign in
   with Discord / Google"); text input with hint and error; success and error alerts; an empty
   state; a loading or "processing" state; a confirmation page for destructive actions.
4. **Optional**: an Open Graph image for sharing a profile link (`/u/{username}`).

## 3. What data exists (design only with this)

- **Crunchyroll history**, per episode: series, season, episode number and title, thumbnail,
  duration, when it was watched, how far (progress %), and whether it was fully watched.
- **MyAnimeList**, per matched season: title, poster, community score, genres, themes,
  demographics, studios, format (TV, Movie, OVA, ONA, Special), year and season, airing status.
  About 93% of seasons are matched; the rest show without these fields.
- **Account**: username (public, in the URL), display name, sign-in methods (Discord, Google),
  whether a sync key is active, whether the Crunchyroll profile is bound, recent syncs with their
  status and counts, security activity, account creation date.

Not available, so out of every design: profile photos, levels, PRO, badges, followers,
personal ratings or reviews, rankings, "vs last month" trends, rewatch counts, the DNA radar and
"pillar" scores, affinity %, and image export.

## 4. Pages that exist and have no design

The copy below is the site's real copy; keep it or propose better wording.

### 4.1 Home (`/`)
> [context] Design the Frunchy home page for a signed-out visitor. Logo and wordmark, headline
> "Your anime identity, in numbers", intro "Frunchy turns your Crunchyroll watch history into
> statistics you can share." A "How to sync" card with 3 numbered steps: "Install the Frunchy
> browser extension and paste your sync key in its options.", "Open your watch history on
> crunchyroll.com.", "The extension sends it to Frunchy; your public dashboard updates in a
> moment". A "Sign in" button. Note:
> "Dashboards are public: anyone with the link to a profile can see its numbers." Also show the
> variant with a success banner "Your account and all your data have been deleted." Header has the
> logo and "Sign in"; signed-in variant shows "Account" and "Sign out". Footer: "Frunchy, a Last.fm
> for anime". Desktop and mobile.

### 4.2 Sign in (`/signin`)
> [context] A centered sign-in card: title "Sign in", text "Sign in or create your Frunchy account.
> Frunchy never asks for a password.", two buttons "Sign in with Discord" and "Sign in with Google"
> with their logos. Variants: the error "Sign-in was cancelled or failed; try again." above the
> buttons, and "Sign-in isn't available right now." with no buttons.

### 4.3 Create your account (`/signup`, invite-only)
> [context] Shown after a first sign-in. Title "Create your account", text "First time here with
> this account. Enter your invite code and choose how you'll appear." Fields: Invite code;
> Username with hint "Your public dashboard address: 3-30 characters, a-z 0-9 _ -. It can't be
> changed later." (show the resulting URL live, e.g. frunchy.app/u/animenerd); Display name.
> Button "Create account"; link "I already have a Frunchy account". Variant with field errors:
> "This invite code isn't valid.", "That username is already taken."

### 4.4 Attach to an existing account (`/claim`)
> [context] Title "I already have an account", text explaining: if your account was created for
> you, paste its current sync key (from the extension's options) to attach this sign-in to it.
> One field "Sync key" (long, monospace), button "Attach to my account", link "Create a new account
> instead". Error variant: "We couldn't verify that key."

### 4.5 Your account (`/account`, private)
> [context] The owner's settings page, one column of cards:
> 1. Profile: username (read-only), display name (editable, "Save"), link to the public dashboard.
> 2. Extension: "A sync key is active." or "No sync key yet: connect the extension to start
>    syncing.", button "Connect extension".
> 3. Crunchyroll history: "Bound to a Crunchyroll profile." or "Not bound yet: your first sync
>    binds it.", button "Reset Crunchyroll binding".
> 4. Sign-in methods: Discord and Google rows, each "Link {provider}" or "Remove" (removing the
>    last one is disabled).
> 5. Recent syncs: a list of syncs with time, status (pending, processing, completed, failed) and
>    counts "120 processed · 2 unresolved · 0 unsupported · 0 failed". Empty: "No syncs yet:
>    connect the extension and open your Crunchyroll history."
> 6. Security activity: a short log (sign-in method linked, key created, binding reset…; some
>    "by the operator").
> 7. A quiet danger zone at the bottom: "Delete my account".
> Also show the success banners ("Display name saved.", "Sign-in method linked.").

### 4.6 Connect the extension (`/account/key`)
> [context] Two steps. Step 1, confirm: "Connect the extension", "This creates a new sync key. Any
> previous key stops working immediately.", button "Create sync key". Step 2, the key: title
> "Your sync key", the key in a large monospace box with a copy button, "Paste this key in the
> Frunchy extension's options.", and a strong note "It's shown only once: Frunchy doesn't keep a
> copy." Link "Back to your account".

### 4.7 Confirm it's you (`/signin/reconfirm`)
> [context] An interstitial before sensitive actions (new key, account deletion) when the last
> sign-in is older than 10 minutes: short text and one button to continue with the provider used
> (Discord or Google). Error variant: "We couldn't confirm it's you. Try again with the sign-in
> method you used."

### 4.8 Delete your account (`/account/delete`)
> [context] A destructive confirmation. Title "Delete your account", a warning listing what goes
> (account, synced history including the original submissions, dashboard, sign-in methods and
> security activity) and that it can't be undone. Field
> "Type your username (animenerd) to confirm", destructive button "Delete my account forever",
> cancel link. Error: "That username doesn't match your account."

### 4.9 Message pages (404, error, rate limit, expired form)
> [context] One template for short message pages: "Page not found / There's nothing at this
> address.", "Something went wrong / We couldn't complete your request. Please try again later.",
> "Too many attempts / Please wait a few minutes and try again.", "This form expired / Reload the
> page and try again." Each with "Back to home". A little personality is welcome.

## 5. Missing states of the existing mockups

### 5.1 Dashboard (`/u/{username}`)
> [context] Variants of the existing Frunchy dashboard (four stats: Series, Episodes watched,
> Hours watched, Days of life; "Recently watched" cards; "Top genres"):
> a) a brand-new user: "No history yet" empty state that points to the extension;
> b) "A sync is being processed; the numbers update when it finishes." banner;
> c) recent cards without MyAnimeList data (no score, no genres, no link) next to cards with them;
> d) "Top genres" with its coverage line ("Based on 92% of fully watched episodes. An episode
>    counts in each of its genres.");
> e) "User not found / There's no Frunchy profile with this name.";
> f) mobile.

### 5.2 Anime DNA profile (`/u/{username}/dna`, feature 004)
> [context] Redo the Anime DNA page with real data only: favourite studio (and runner-up); dominant
> decade with its share; viewing pace (episodes per day over the last 90 days, with a consistency
> label); insignia anime (most watched, poster, episode count); genre distribution (top 5 plus
> "Others", each with its top anime); format split (TV, Movie, OVA, ONA, Special); total hours and
> days. Every MyAnimeList-based block shows a small coverage note. Drop the radar, pillar scores,
> "algorithmic diagnosis", affinity %, badges, followers, ratings and the PNG export; keep a
> "copy link" share button. Variants: "not enough data yet" for a new user, and mobile.

## 6. Browser extension

The extension's own Claude session owns these screens and confirmed this list. The popup and the
options page are **already built** from the popup mockup with the section 2 token values (YAML
primary `#4cd7f6`, secondary `#c0c1ff`, `lg` = 1rem), so the designer reviews and refines them
rather than starting over. Screenshots of every state are available on request from the extension session: they show the owner's real watch history, so they are kept out of both public repositories.
What the extension needs most from the designer is section 2's brand item: the extension icon at
16, 32, 48 and 128 px (it still ships the template's placeholder icons) and a mark for the
popup header (today a styled "F").

### 6.1 Popup states
> [context] The extension popup: 380px wide, and Chrome caps it at 600px tall, so every state
> must fit without scrolling, Spanish included. Layout: a header (mark, "Frunchy", sync status
> "Sync on / off / issue", the Frunchy display name, a settings button), one action card, the 3
> most recent episodes, and a footer (link to the web dashboard; icon actions "Sync stored pages",
> "Download JSON" and "Clear", where Clear asks inline before deleting; the version).
> The action card has one primary button that follows the active tab: **not on the history tab**
> ("Open Crunchyroll history"); **history tab opened before the extension was installed or
> updated** ("Reload tab"); **ready to scrape**, either first time or again, with the stored count
> and newest date ("Scrape history"); **scraping** (a live count of stored entries, "Stop"; it also
> stops by itself at the end of the history or where the last sync left off). The card also shows
> the Crunchyroll profile being scraped, as a chip.
> Under the button, a sync line: **sync off** (no key: "Set up" opens the options page);
> **synced** (last sync time; pages waiting to upload; last import "98 processed · 2 without
> episode details", "Importing…" or "failed on the server"); **error** (network or server: the
> text says it retries by itself, with no retry button, because retries are automatic from 30 s
> to 30 min; wrong or revoked key: opens Settings, and uploads pause until a key is saved);
> **different Crunchyroll profile** (`history_owner_mismatch` / `history_owner_taken`: explain it;
> a link to "Reset Crunchyroll binding" on the account page).
> Each recent episode: thumbnail with a watched-progress bar, series, "Ep. 6 · 24 min · 2 h ago",
> and a status: Synced, Waiting (queued for upload) or Stored (kept locally, not sent).
> Also design: **first run** (nothing stored and no key: no episode list, no footer actions).

Links to the website use fixed paths under the backend URL (contract `import-api.md` v1.3):
`/account` holds "Reset Crunchyroll binding", and `/account/key` is "Connect extension".

### 6.2 Options page
> [context] The extension's options page, shown inside Chrome's extension settings dialog: a
> backend URL (today it defaults to `http://localhost:5099`; the public site once one exists); a
> sync key (password field, hint "Find it on your Frunchy account page, under 'Connect
> extension'"); one button, "Save and check", which asks Chrome for access to a non-default
> server, checks the key with `GET /api/me`, and then shows "Connected as {display name}" or the
> error. Language follows the browser (no switcher).
> Proposed additions, not built yet: a show/hide toggle on the key, and an "Open dashboard"
> link after a successful check. "Download JSON" and "Clear" stay in the popup, next to the data
> they act on; don't repeat them here.

## 7. What to send back

For each screen: `code.html` and a working `screen.png`, in a folder under `design/` named after
the screen (`signin/`, `account/`, …), desktop and mobile where it applies, plus the updated
`DESIGN.md` if the design system changes.
