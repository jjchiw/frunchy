# Page snapshots for design reviews

Screenshots of Frunchy's pages, signed in where they need it, at 1280 and 360 wide in English and Spanish. A designer
session can look at them without signing in anywhere. Both steps are opt-in: a normal `dotnet test` skips the test.

1. Save the pages' HTML (needs Docker, like every test). One app with a root operator `animenerd` (the 2026 sample,
   enriched with the scripted MyAnimeList catalog), a second user `sakura` with a shorter history, a third who signed up
   with an invite, and active and used invites:

   ```bash
   FRUNCHY_SNAPSHOT_DIR=<abs path> dotnet test --project tests/FrunchyBackend.Tests --filter-class "*PageSnapshots*"
   ```

   It writes `<abs path>/html/<name>-<lang>.html`. Root-relative URLs (`src="/…"`, `href="/…"`, `url(/…)`) are rewritten
   to `http://snapshot.invalid/…`; Tailwind, fonts and Alpine come from their CDNs.

2. Shoot them with headless Chrome (macOS Chrome path and Node 24 are defaults; override with `CHROME` and `NODE`):

   ```bash
   bash tools/snapshots/shoot.sh <abs path>
   ```

   It writes `<abs path>/<name>-<width>-<lang>.png`: full page, 1280 at scale 1, 360 at scale 2 (mobile). The Chrome
   profile is a temp folder under `<abs path>` and is deleted at the end. Requests to `snapshot.invalid` are aborted, so
   a click on a link never leaves the saved page.

Names: `home`, `signin` (signed out), `dashboard`, `dna`, `year-2026`, `anime`, `compare`, `account` (no share link),
`account-shared` (with one), `share-link-created` (the show-once page), `review-invites`, `invite-created` (the show-once
code page), `review-settings`.

`signin` also gets two viewport-sized shots per width and language, from real mouse clicks (CDP
`Input.dispatchMouseEvent`) on the first provider link:

- `signin-dialog-…`: first visit, the invite-only dialog must open (`dialog.open === true`).
- `signin-seen-…`: after `localStorage frunchy.inviteNoticeSeen = '1'`, the dialog must stay shut and the link is followed
  (the aborted navigation to `snapshot.invalid` is the proof).

The shooter prints those eight results, any page wider than the viewport at 360 (with the first offending elements) and
console errors per page, and exits 1 if a sign-in check fails.
