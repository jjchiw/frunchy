# Changelog

What changed in each release, for people who run Frunchy. Newest first. A release's notes on
GitHub are its section here: `.github/scripts/changelog.sh notes <version>` prints it.

Notes are not written here. Each change adds one file in `changelog.d/` (see its README), and the
release gathers those files into the new version's section below and deletes them, so this file holds
released sections only. The release workflow refuses to release when `changelog.d/` has no notes.

Each section has the changes that matter to whoever runs Frunchy, in this order and only as
needed: Added, Changed, Fixed, Security, then Upgrade when an upgrade needs more than deploying the
new image.

## 0.6.1 - 2026-10-28

### Added

- The fediverse on the Caravana deploy. `aspire deploy` now publishes Megáfono, its gateway and its own
  Postgres next to Frunchy and its Postgres, with federation on, so people on Mastodon and other servers
  can follow a Public profile and an anime, and see and answer comments on episodes. The gateway takes
  over the domain: it sends Megáfono's routes to Megáfono and everything else to Frunchy. **The domain
  becomes permanent once someone has followed**: changing it breaks every follow. `Deploy:Federation=false`
  keeps the deploy as it was. See "Upgrade" below and `docs/deploy-caravana.md`.
- "Your seasons" on the Anime DNA: the shows you watched, by the season they aired in (Winter, Spring, Summer
  or Fall of the year of each episode's air date), your top season, and a mark on the shows you watched
  while they aired (an episode fully watched within 7 days of airing). The 8 newest seasons are listed and the
  older ones counted. A "Long run" card shows your top long show (more than 100 episodes, such as One Piece)
  with its episodes, hours and the seasons it spans, once you have watched 24 of its episodes.
- Each Crunchyroll episode's air date is now stored in the catalog, as it arrives with every sync.

### Changed

- Megáfono is pinned at 0.2.0 (was 0.1.0) in the AppHost and in `deploy/compose.yaml`
  (`MEGAFONO_VERSION`). With federation on, Members and Private profiles are now not discoverable and
  not indexable on the fediverse, as Frunchy already asked: 0.1.0 marked every user as discoverable. A
  self-hosted Megáfono 0.1.0 database is upgraded by Megáfono itself on start.
- The Anime DNA and the year in review count seasons, not minutes, for the favourite studio, genres, dominant
  decade and formats: one watched season is a MyAnimeList entry in one airing season, so a 1,000-episode show
  weighs a dozen seasons, not a thousand hours. A decade is the airing year of the watched season. The notes
  say "Based on the 41 of 45 watched seasons with MyAnimeList details". Hours, episodes, the viewing pace, charts and compatibility are unchanged.
  The year in review has no decade card, so it changes its studio, genre and format cards only.
- The Anime insignia skips long runners (more than 100 episodes); they have their own "Long run" card.
- Release notes are now written as one file per change in `changelog.d/`, and the release gathers them into
  the new version's section of `CHANGELOG.md`. Nothing changes for running Frunchy.

### Fixed

- The Anime insignia no longer says "48 of 12 episodes watched" for a show whose seasons are separate
  MyAnimeList entries: the total now adds up every entry of the show you watched, and is left out when one of
  them has no episode count yet (still airing) or no MyAnimeList match.

### Upgrade

- Before releasing a deploy with federation (the default now), add four secrets to Bella
  `frunchy/production` as `Parameters__<name>` keys: `megafono-master-key` (`openssl rand -base64 32`),
  and `megafono-token`, `megafono-webhook-secret` and `megafono-db-password` (each `openssl rand -hex 32`).
  The release stops, naming the parameter, if the master key is missing or is not base64 of 32 bytes.
- Back up the master key apart from the database: it encrypts every actor's private key in Megáfono's
  database, and Caravana's backups do not include secrets.
- Check the Caravana host's memory first: the app now reserves about 960 MB (up to about 2.2 GB at its
  ceilings), up from 512 MB.
- The domain is permanent from the first follow: never change `frunchy-domain`, never wipe Megáfono's
  database, and never run a test instance on the production domain.
- To stay without federation, set `Deploy__Federation=false` for the deploy: the app is `frunchy` and `pg`
  again, with no new secrets needed.
- Air dates are filled in from the history already stored, once, when the new version starts: nothing to run and
  nobody needs to sync again. A second start changes nothing.

## 0.6.0 - 2026-10-27

### Added

- The fediverse on the Caravana deploy. `aspire deploy` now publishes Megáfono, its gateway and its own
  Postgres next to Frunchy and its Postgres, with federation on, so people on Mastodon and other servers
  can follow a Public profile and an anime, and see and answer comments on episodes. The gateway takes
  over the domain: it sends Megáfono's routes to Megáfono and everything else to Frunchy. **The domain
  becomes permanent once someone has followed**: changing it breaks every follow. `Deploy:Federation=false`
  keeps the deploy as it was. See "Upgrade" below and `docs/deploy-caravana.md`.
- "Your seasons" on the Anime DNA: the shows you watched, by the season they aired in (Winter, Spring, Summer
  or Fall of the year of each episode's air date), your top season, and a mark on the shows you watched
  while they aired (an episode fully watched within 7 days of airing). The 8 newest seasons are listed and the
  older ones counted. A "Long run" card shows your top long show (more than 100 episodes, such as One Piece)
  with its episodes, hours and the seasons it spans, once you have watched 24 of its episodes.
- Each Crunchyroll episode's air date is now stored in the catalog, as it arrives with every sync.

### Changed

- The Anime DNA and the year in review count seasons, not minutes, for the favourite studio, genres, dominant
  decade and formats: one watched season is a MyAnimeList entry in one airing season, so a 1,000-episode show
  weighs a dozen seasons, not a thousand hours. A decade is the airing year of the watched season. The notes
  say "Based on the 41 of 45 watched seasons with MyAnimeList details". Hours, episodes, the viewing pace, charts and compatibility are unchanged.
  The year in review has no decade card, so it changes its studio, genre and format cards only.
- The Anime insignia skips long runners (more than 100 episodes); they have their own "Long run" card.
- Megáfono is pinned at 0.2.0 (was 0.1.0) in the AppHost and in `deploy/compose.yaml`
  (`MEGAFONO_VERSION`). With federation on, Members and Private profiles are now not discoverable and
  not indexable on the fediverse, as Frunchy already asked: 0.1.0 marked every user as discoverable. A
  self-hosted Megáfono 0.1.0 database is upgraded by Megáfono itself on start.

### Upgrade

- Air dates are filled in from the history already stored, once, when the new version starts: nothing to run and
  nobody needs to sync again. A second start changes nothing.
- Before releasing a deploy with federation (the default now), add four secrets to Bella
  `frunchy/production` as `Parameters__<name>` keys: `megafono-master-key` (`openssl rand -base64 32`),
  and `megafono-token`, `megafono-webhook-secret` and `megafono-db-password` (each `openssl rand -hex 32`).
  The release stops, naming the parameter, if the master key is missing or is not base64 of 32 bytes.
- Back up the master key apart from the database: it encrypts every actor's private key in Megáfono's
  database, and Caravana's backups do not include secrets.
- Check the Caravana host's memory first: the app now reserves about 960 MB (up to about 2.2 GB at its
  ceilings), up from 512 MB.
- The domain is permanent from the first follow: never change `frunchy-domain`, never wipe Megáfono's
  database, and never run a test instance on the production domain.
- To stay without federation, set `Deploy__Federation=false` for the deploy: the app is `frunchy` and `pg`
  again, with no new secrets needed.

## 0.5.0 - 2026-10-24

### Added

- A public install guide at `/guide`, in English and Spanish, for people who got an invite: installing the
  extension, signing up with the invite, connecting the extension, syncing the history, the dashboard and
  the Anime DNA, profile visibility and deleting the account, with screenshots. The home, sign-up and
  sync-key pages link to it. Frunchy now serves the files in `wwwroot` (the guide's screenshots) with a
  one-day browser cache.
- "What to watch next" at `/next`, for signed-in people with at least 15 fully watched episodes: the series
  that the people with the most similar taste (level "Good" or better, from the profiles they can already
  open) watched through and they haven't started, with who watched it through and the MyAnimeList score
  and genres. A private profile only counts for someone holding its current share link. Your dashboard
  links to it. Nothing to do on upgrade.

### Changed

- Changing a profile's visibility now records which level it became ("Profile made public", "Profile
  set to members", "Profile made private") in the account's security activity, instead of "Profile
  visibility changed". Earlier entries keep their old wording. Nothing to do on upgrade.
- In Spanish the dashboard is always "panel", the word of its tab; the home steps, sign-in, sign-up
  and account pages said "dashboard".

### Fixed

- Frunchy waits up to 60 s for its database at start instead of exiting when Postgres is not ready
  yet (on a first Caravana deploy the app and its database start together, which cost a restart). The
  operator commands wait too. `Frunchy:Database:StartupWaitSeconds` sets the limit; `0` fails at once.
- A poster or thumbnail that fails to load shows a plain placeholder box instead of the browser's
  broken-image icon, on every page that has one. All posters are now decorative, with an empty alt
  text (the title is beside them).
- On `/account` the dashboard row is labelled "Your dashboard" ("Tu panel") whatever the profile's
  visibility; it said "Public dashboard" even for Members and Private profiles.
- A comment or profile change that Megáfono refuses for good (a 4xx that will never succeed, for
  example a thread that cannot be created) now leaves the fediverse queue instead of being retried
  every hour for ever: it is marked `failed` with its reason, the comments waiting on that thread
  fail with it, and `fediverse-status` lists them apart from what is still waiting. The author of
  such a comment sees "Not sent to the fediverse; it's still here on Frunchy." under it. Network
  errors, 5xx, 408, 409, 429 and 401 are still retried. The outbox gains the `failed` state, added
  automatically on start.

## 0.4.0 - 2026-10-22

### Added

- The home page links the Frunchy extension in the Chrome Web Store ("Get the Chrome extension")
  and Frunchy's source code on GitHub.
- The setup invite: while Frunchy has no accounts, every start prints a one-time invite in the
  console output (`docker logs`, Caravana's Logs tab). Sign in, enter it, and that account becomes
  an operator, so the first account no longer needs a console command. A restart prints a new
  invite and cancels the old one; once an account exists nothing is printed. Set
  `Frunchy:SetupInvite:Enabled=false` to turn it off. The database gains `invites.is_setup`,
  added automatically on start.
- A language link in the footer of every page: it offers the other language (English or Español), remembers
  the choice in a cookie for a year and brings you back to the same page. Without the choice, the browser's
  language still decides, as before.

### Changed

- The profile pages share one top: the person's name, then a tab bar (Dashboard, Anime DNA, Year in
  review) that shows which view you are on. It replaces the buttons that were scattered on each page
  and is on the series and comparison pages too, with nothing selected there.

## 0.3.0 - 2026-10-21

### Added

- Members-only profiles: a profile can now be Public, Members or Private. Members means anyone
  signed in to this Frunchy can see it; a signed-out visitor is sent to sign in and lands on the page
  afterwards. Owners choose on `/account#visibility`, and a share link still opens any profile.
  Private profiles still answer like a username that doesn't exist. Charts, series and episode
  pages are unchanged.

### Changed

- The default profile visibility is now `members` (it was `private`): users who never chose move
  from Private to Members when you deploy this, and users who chose keep their choice.
  `Frunchy:Profiles:DefaultVisibility` accepts `private`, `members` or `public`; set it to
  `private` to keep the old behaviour.
- With federation on, users whose profile isn't Public are marked as not discoverable and not
  indexable for the fediverse (honoured by Mastodon; other servers may not), and changing a
  profile's visibility refreshes the actor on Megáfono.

### Upgrade

- The database upgrades by itself on start: the check on `profile_settings.visibility` is replaced
  to accept `members`, once, with no data change. Two copies starting together take turns. Nothing
  to run by hand.

## 0.2.0 - 2026-10-21

### Added

- Invites on the web: operators create sign-up invites, see recent ones with their state and revoke
  active ones on `/review/invites`, linked from `/account`. The code is shown once, when it is
  created. The `invite-*` commands do the same and are unchanged.
- Open sign-up: the root operator chooses on `/review/settings` ("Site settings", linked from
  `/account`) whether new accounts need an invite code (the default) or anyone who signs in with a
  provider can create one. The change applies at once, with no redeploy, and needs a sign-in within
  the last 10 minutes. Existing accounts sign in the same way in both modes. On an invite-only
  Frunchy the sign-in page now says so, with a note and a dialog before leaving for the provider
  ("Don't show this again on this device" is remembered in the browser only).
- Every page's footer says Frunchy is not affiliated with Crunchyroll or MyAnimeList.

## 0.1.0 - 2026-10-20

### Added

- Watch history from Crunchyroll: the Frunchy browser extension sends a user's history pages, and
  Frunchy keeps them as received and turns them into watch events. Re-sending or reordering pages
  gives the same result, and `user-rebuild` rebuilds a user's figures from the stored pages.
- Profile pages under `/u/{username}`: a dashboard (series, episodes, hours, recent episodes, top
  genres), an Anime DNA page, a year in review, and a page per anime with the episodes watched.
- MyAnimeList details (score, genres, studios, format, year) matched to each Crunchyroll season by
  a background worker, from MyAnimeList's official API or Jikan. Operators fix uncertain matches
  on `/review/mal` or with the `mal-*` commands.
- Public catalog pages: a page per series (`/anime/{seriesId}`) and per episode with a comment
  thread. Signed-in users comment, mark spoilers, and edit or delete their own comments; operators
  hide and unhide comments.
- Charts (`/charts`): the most watched series this week, this season and of all time, ranked by
  people, with "Watched by N people" on series and episode pages. Nothing is shown below
  `Frunchy:Charts:MinPeople` people (3 by default), and no chart names anyone.
- Taste compatibility: a signed-in user sees how their taste matches someone else's, as a level
  in words, on that person's dashboard and on `/u/{username}/compare`.
- Accounts: invite-only sign-up through Google or Umbral (OpenID Connect), sync keys for the
  extension, linked sign-in providers, account deletion, and a security activity list.
- Private profiles: every page under `/u/{username}` is visible only to its owner unless the owner
  makes it public or shares a link. `Frunchy:Profiles:DefaultVisibility` (`private` by default)
  sets what users who haven't chosen get. A share link opens the whole profile in the browser
  that uses it, until the owner replaces or turns it off.
- Operators: one root operator (`Frunchy:RootOperator`) adds and removes operators on the web or
  with `operator-add` / `operator-remove`.
- The fediverse, behind `Frunchy:Federation:Enabled` (off by default): users and series are
  fediverse accounts through Megáfono, comments go both ways with Mastodon, and users and
  operators block remote accounts and servers.
- English and Spanish, chosen from the browser's language.
- Deployment: `GET /healthz` for orchestrators; a container image; a self-host compose file
  (`deploy/compose.yaml`); and `aspire deploy` to Caravana, which a `v*` tag runs after the tests.

### Security

- ASP.NET data-protection keys are stored in Postgres, encrypted with
  `Frunchy:DataProtection:EncryptionKey` (the base64 of 32 bytes), which Production requires. Keep
  the key apart from the database backups: losing it signs everyone out.
- Sync keys and share-link tokens are stored only as SHA-256 hashes.
