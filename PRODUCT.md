# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

The primary user is a **self-hoster**: someone who deploys Frunchy for their own community (friends, a club, a small anime circle) and operates it. They are the root operator, they create invites, review MyAnimeList matches and run the operator CLI, and they are also a member with a profile of their own.

The people on an instance are **members**: Crunchyroll viewers who install the browser extension once, then open Frunchy to see their own numbers (dashboard, Anime DNA, year in review), look at a friend's profile or compare tastes with them. Most visits come after a sync, or when someone sends them a profile or series link.

Visitors with no account exist only when an owner has made a profile public or shared a link; the default is members-only.

The site is read in English or Spanish, chosen from the browser or the footer switch (feature 024). Spanish copy runs about 20 to 30 percent longer than English.

## Product Purpose

Frunchy builds a person's anime identity from their Crunchyroll watch history, outside Crunchyroll. The browser extension captures history pages; Frunchy turns them into statistics (totals, recent episodes, top genres), the Anime DNA profile, year-in-review pages, per-series pages, taste compatibility between two people, and instance-wide charts of what the community is watching. Episode pages carry comment threads, and, when federation is on, profiles and series are fediverse actors.

Success in the coming year is **other people running their own instance**: the software, its deploy story (Docker image, compose file, Aspire deploy to Caravana), its operator tools and its documentation matter as much as the pages. A good instance is one whose owner can set it up, invite people and keep it running without reading the code.

## Positioning

**Your history belongs to you, outside Crunchyroll.** Raw imports are stored exactly as received and never modified; every figure on the site is rebuildable from them. The identity survives the streaming service, and the accumulated history is the asset nobody else can reconstruct. MyAnimeList and AniList need manual logging; Crunchyroll's own profile stays inside Crunchyroll and can be lost with it.

The automatic, complete capture (no manual logging) is what makes the claim possible, and the fediverse layer is where the community goes later; neither is the core claim.

## Operating Context

- **Sync**: a member installs the Chrome extension (sibling repo `../browser-extension`, its own Claude session), pastes the sync key from `/account`, and opens their Crunchyroll watch history; the extension posts the pages to `/api/imports`. The first sync is heavy, later ones are small deltas and may resend data. Processing is asynchronous; the dashboard updates a moment later.
- **Enrichment**: a background worker matches every Crunchyroll season to MyAnimeList (official API or Jikan). About 93 percent of seasons match; the rest show without MyAnimeList fields, and every figure that depends on MyAnimeList says what share of the history it covers. Operators review uncertain matches on `/review/mal`.
- **Operating an instance**: invite-only sign-up by default (the root can open it), operators managed by the root, settings on `/review/settings`, an operator CLI for accounts, invites, MyAnimeList and the fediverse, a `/healthz` endpoint, and a changelog that every release needs.
- **Sign-in**: Google or Umbral (the owner's self-hosted OpenID Connect provider). Frunchy never asks for a password.
- **Deployment**: self-hosted with `deploy/compose.yaml` (Postgres 18) or `aspire deploy` to Caravana; releases are GitHub images tagged by version, never `latest`.
- **Live instance**: https://frunchy.lacalabaza.xyz, the owner's own.

## Capabilities and Constraints

- **Data that exists** (design only with this): per watched episode, the series, season, episode number and title, thumbnail, duration, when it was watched, progress and whether it was fully watched; per matched season, the MyAnimeList title, poster, community score, genres, themes, demographics, studios, format, year and season, airing status; per account, username, display name, sign-in methods, sync key state, Crunchyroll binding, recent syncs, security activity, visibility and share link.
- **Data that does not exist and must never be shown**: profile photos, levels, badges, followers, personal ratings or reviews, rankings of people, "vs last month" trends on a profile, rewatch counts (only the latest watch of an episode is kept), affinity percentages (compatibility is shown in five words, never a number), image export.
- **Privacy**: a profile is members-only by default; its owner can make it public, private, or share a link. Charts and "watched by N people" never name a person and apply a minimum of people (default 3). A private profile is indistinguishable from an unknown username.
- **Engineering constraints that shape the UI**: server-rendered F# views (Oxpecker) with Tailwind, fonts and icons from CDNs and no build step; Alpine for the little interactivity there is; every page must work without JavaScript (copy buttons fall back to a selectable field, dialogs degrade to plain links). Tests read only `data-*` hooks, never classes, so hooks are part of the interface. One file per endpoint.
- **Scale reference**: 10k users × 5 episodes a day; nothing is designed for more.
- **Terminology**: profile (a person's pages under `/u/{username}`), dashboard, Anime DNA, year in review, series page (public, `/anime/{id}`) versus anime page (one person's history of a series), sync key, invite, operator and root operator, members / public / private visibility, share link.
- **Undecided**: whether a member's language choice moves onto the account; which further statistics members want (the constitution calls this the open question).

## Brand Commitments

- Name **Frunchy**; the mark is the clapperboard whose body is a bar chart (`design/brand/frunchy-mark.svg`), used in the navigation, favicon and extension icons.
- Voice: plain, factual, the site's own words. No "telemetry" voice, no claims the product cannot keep (no "verified", "secure handshake", "realtime"). Every control names its action; every figure says what it is based on.
- Design system **Night Broadcast** (`design/night_broadcast/DESIGN.md`, tokens in `Views/Theme.fs`), shared by name and value with the browser extension through a contract; a token change must be told to the extension session.
- The owner's design decisions are recorded in `design/REVIEW.md`: layout and components may be taken from mockups, copy never; every link must lead somewhere real.

## Evidence on Hand

- Real Crunchyroll captures as test fixtures (`JsonSamples/`, 2022 and 2026) and recorded MyAnimeList and Jikan responses (`JsonSamples/mal/`, `JsonSamples/jikan/`); the snapshot harness (`tools/snapshots/`) renders every page from them with real posters and genres.
- Mockups and screens in `design/` (one folder per screen), the design brief `design/BRIEF.md` and the review `design/REVIEW.md`.
- Feature specs in `specs/###-name/` with contracts; the constitution in `.specify/memory/constitution.md`.
- No testimonials, press, user counts or benchmarks exist; none may be invented.

## Product Principles

1. **The history is the asset.** Nothing on a page may contradict what the raw history says, and every derived figure is rebuildable from it.
2. **Say what it is based on.** A figure that rests on partial data (MyAnimeList coverage, fully watched episodes, a short history) carries its coverage line; a comparison without enough history says whose history is short rather than guessing.
3. **Private by default, shareable by choice.** Aggregates never name a person; owners decide who sees their profile.
4. **Boring and self-hostable.** Plain server-rendered pages that work without JavaScript, no build step, no infrastructure an operator cannot run with Docker and Postgres.
5. **Two languages, one product.** Every string exists in English and Spanish and the layout is designed for the longer one.

## Accessibility & Inclusion

WCAG 2.1 AA on every text and background pair, a visible 2px focus ring, every page usable without JavaScript, links to pages marked with `aria-current` rather than ARIA tab widgets, and colour never the only cue (names are read out beside bars, states have words).
