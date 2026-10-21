# Changelog

What changed in each release, for people who run Frunchy. Newest first. A release's notes on
GitHub are its section here: `.github/scripts/changelog.sh notes <version>` prints it, and the
release workflow refuses a tag when there is nothing under Unreleased to release.

Each section has the changes that matter to whoever runs Frunchy, in this order and only as
needed: Added, Changed, Fixed, Security, then Upgrade when an upgrade needs more than deploying the
new image. Add to "Unreleased" as you work; a release renames it.

## Unreleased

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
- Invites on the web: operators create sign-up invites, see recent ones with their state and revoke
  active ones on `/review/invites`, linked from `/account`. The code is shown once, when it is
  created. The `invite-*` commands do the same and are unchanged.
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
