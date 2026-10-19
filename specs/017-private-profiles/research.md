# Research: Private Profiles

## R1. Storage: a `profile_settings` table, not new `users` columns

- **Decision**: one row per user who chose a visibility or created a link (`user_id` PK → `users ON DELETE CASCADE`).
  A user without a row follows the instance default and has no link.
- **Rationale**: the bootstrap is `CREATE TABLE IF NOT EXISTS` with no `ALTER` (feature 014), so new columns on
  `users` would break every existing database (the owner's AppHost data included) until it's wiped; a new table is
  created on the next start. It also keeps the share-link hash out of the hot `users` lookups.

## R2. One rule, one module: `Infrastructure/Profiles.fs`

- `type Visibility = Public | Private`, with `parseDefault` for the setting: missing means `Private`, and an
  invalid value raises naming `Frunchy:Profiles:DefaultVisibility`. The `ProfileSettings` singleton holds the default.
- `effective (instanceDefault) (choice: Visibility option)`.
- `canView viewerId (grants: Grant list) (owner: OwnerAccess)`, pure. `OwnerAccess` = the user id, their effective
  visibility and their current link id. It's true for a public profile, the owner, or a grant with the same user id
  and link id.
- `loadAccess db userId : Task<OwnerAccess>`: one primary-key query (`LEFT JOIN profile_settings`).
- `visibleTo (ctx: HttpContext) db userId : Task<bool>`: reads the viewer (`WebAuth.currentUserId`) and the grants
  cookie, then calls `canView`.
- **The 404**: each `/u/` handler already answers an unknown username with `Layout.messagePage ctx t
  t.UserNotFoundTitle t.UserNotFoundBody` and 404. Hidden profiles go through the same call, right after the
  user lookup and before any other query. A test compares the two bodies after removing the antiforgery and nonce
  values.

## R3. Share tokens

- **Token**: `frs_` + base64url of 32 random bytes (`RandomNumberGenerator`). The hash is lowercase-hex SHA-256,
  the `SyncKey` pattern (`Domains/Identity.fs`): reuse its helpers or mirror them in a small `ShareToken` module in
  `Domains/Identity.fs`.
- **Lookup**: `SELECT … FROM profile_settings WHERE share_token_hash = @hash`, unique. An equality lookup on a
  hash of a 256-bit secret leaks nothing useful by timing, the same reasoning as the sync key.
- **Link id**: a fresh `Guid` for each created link. Replacing a link changes both the hash and the id; turning it
  off nulls all three link columns.
- **Show once**: the create POST renders the result page directly, with no redirect, as `/account/key` does for the
  sync key: follow `Handlers/Account/SyncKey.fs`. The page carries `data-share-link` and a copy button.

## R4. Grants: a data-protected cookie

- **Cookie**: `frunchy.shares`, holding a JSON array of `{ "u": userId, "l": linkId, "at": unixSeconds }`.
  - Protected with `IDataProtectionProvider.CreateProtector "Frunchy.ShareGrants.v1"`; 016 makes those keys durable
    and shared.
  - HttpOnly, SameSite=Lax, Secure when the request is https, Path `/`, expires 30 days after the last grant.
  - At most 20 entries: a new grant for the same user replaces its entry, and the oldest goes first.
- **Reading**: unprotect, then parse; any failure means no grants (no error page).
- **Checking**: a grant counts only while `l` equals the user's current `share_link_id`, so replacing or turning off a
  link ends every browser's access without tracking browsers.
- **`GET /s/{token}`** (`Handlers/Share.fs`):
  - the token is checked by shape first (`^frs_[A-Za-z0-9_-]{43}$`), then by hash;
  - valid: add the grant, then 303 to `/u/{username}`. The owner opening their own link is just redirected.
  - invalid: `Layout.messagePage` NotFound 404 and no `Set-Cookie`.
  - limiter `share`, 30 a minute per IP.
- **Why not `?token=` on the profile URL**: every internal link would have to carry it, and it would leak in
  `Referer` and copied addresses. The redirect means the token address is never a document, so it isn't sent as a
  `Referer` either.

## R5. Where the rule is applied

| Place | Change |
|---|---|
| `Users/Dashboard.fs`, `Dna.fs`, `Year.fs` (both routes), `Anime.fs`, `Compare.fs` | `Profiles.visibleTo` after the user lookup, before other queries. Hidden → the unknown-user 404. Owner and private → `Components.privateProfileNote` (`data-profile-private`, link to `/account#visibility`) in place of the page's copy-link block |
| `Compare.fs` | order: `requireSignedIn` → username → lookup → **visibility** → self redirect → `Compatibility.load` |
| `Dashboard.fs` card | unchanged: the card only renders on a page that already passed the check |
| `Episodes/Episode.fs` comments | the comments query adds `LEFT JOIN profile_settings` (visibility, link id); `authorLine` links only when `Profiles.canView` (one cookie read per request, no query per comment) |
| `Review/MalReview.fs` | the season link becomes `/anime/{seriesId}` (`data-series-page-link`); the query drops the latest-watcher username |
| `Home.fs` | `HomeProfilesNote` becomes two texts, picked by the instance default |
| `Operations/ListUsers.fs` | a visibility column (`public`/`private`, `*` when it comes from the default) |
| Federation | no change in `Actors.fs`; the handle line and `advertiseActivityPub` sit on pages that now pass the check |

## R6. Account section

- `Handlers/Account/AccountPage.fs`: new section `visibility` after `profile` (`accountSection "visibility" …`) with:
  - two radio inputs (`public`, `private`), the effective state, and whether it comes from the default;
  - the share-link state: none, or created on a date. Buttons: "Create a share link" or "Replace the link", and
    "Turn off the link".
- New slices:
  - `Handlers/Account/Visibility.fs`: `POST /account/visibility`;
  - `Handlers/Account/ShareLink.fs`: `POST /account/share-link` (create or replace; renders the show-once page) and
    `POST /account/share-link/revoke`.
- All three use `requireSignedIn` → `readValidatedForm` → a transaction with the `profile_settings` upsert and
  `SecurityEvents.record` → a redirect with `?msg=visibility-saved|visibility-invalid|share-link-revoked#visibility`
  (create renders instead). Limiter `account-write`. No `requireFresh`: no credential is exposed.
- Security events, new `SecurityEventType` cases: `profile_visibility_changed`, `share_link_created`,
  `share_link_replaced`, `share_link_revoked`.

## R7. Tests that read other users' profiles

Many earlier tests open a profile signed out (dashboard, DNA, year, anime, compare, charts' "mine"). They keep
passing by making the fixture's default **public** unless a test asks otherwise: `createAppWith` adds
`Frunchy:Profiles:DefaultVisibility=public` before `extraConfig`. The new tests pass `private`. The real default
stays `private`; a test pins that the app's own default (no setting) is private.
