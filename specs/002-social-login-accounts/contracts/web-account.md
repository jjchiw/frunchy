# Contract: Sign-in and account pages (HTML)

**Consumers**: people with a browser, and the integration tests (through the fake `test` provider).

General rules:
- Server-rendered pages in English or Spanish, language from `Accept-Language` (as in feature 001).
- Every state change is a **POST form** with an antiforgery token (`GetAntiforgeryInput`).
- `returnUrl` is only honoured when it is local (`/…`, not `//…`).
- Rate-limit groups are those of research R8; a rejection returns 429 with a localized page.

## Routes

| Method & route | Signed in? | Effect |
|---|---|---|
| `GET /signin?returnUrl=` | no | one button per enabled provider; "sign-in unavailable" if none |
| `GET /signin/{provider}?returnUrl=` | no | challenge that provider (`google`, `discord`) → provider → `/signin-{provider}` → `/signin/complete` |
| `GET /signin/complete` | — | reads the external cookie: linked → session + redirect to `returnUrl` (default `/account`); unknown and signed out → `/signup`; link mode → link and redirect to `/account`; re-confirm mode → refresh `auth_time` and redirect back |
| `GET /signup` | pending identity | form: invite code, username, display name; a link to `/claim` |
| `POST /signup` | pending identity | validates, consumes the invite and creates user + identity atomically, then signs in → `/account` |
| `GET /claim` | pending identity | form: current sync key ("I already have an account") |
| `POST /claim` | pending identity | links the identity to the account owning that key **only if it has no identity** → signs in → `/account`; otherwise a generic refusal |
| `POST /signout` | yes | deletes this browser's session → `/` |
| `GET /account` | yes | username, display name (editable), dashboard link, linked logins, "key active" flag, Crunchyroll binding, last 10 syncs, last 20 security events |
| `POST /account/display-name` | yes | 1–60 characters → back to `/account` |
| `GET /account/key` | yes, **fresh** | confirmation page ("Generate a new key? The current one stops working"), with a button that POSTs |
| `POST /account/key` | yes, **fresh** | generates a key (revoking the old one) and renders it **once** on the response page, with paste instructions; never shown again |
| `POST /account/binding/reset` | yes | clears the Crunchyroll binding → `/account` |
| `GET /account/link/{provider}` | yes | challenge in link mode |
| `POST /account/unlink/{provider}` | yes | refused if it's the last identity |
| `GET /account/delete` | yes | confirmation page: type your username |
| `POST /account/delete` | yes, **fresh** | username must match → deletes everything → signed out → `/` with a "deleted" notice |
| `GET /signin/reconfirm?returnUrl=` | yes | challenges the session's provider (`prompt=select_account` for Google, `prompt=consent` for Discord); back through `/signin/complete` |

**Fresh**: `auth_time` within the last 10 minutes. Otherwise the user is redirected to
`/signin/reconfirm?returnUrl=<the confirmation page>` and returns there afterwards (FR-014).

Unauthenticated requests to `/account…` redirect to `/signin?returnUrl=<path>` (FR-015).

## Navigation

The layout navbar shows "Sign in" when signed out. When signed in it shows the display name, an
"Account" link and a "Sign out" button (a POST form). Public dashboards (`/u/{username}`) render the
same as in feature 001 apart from the navbar.

## Messages (localized, from `Views/Texts.fs`)

- **Sign-up refusals**: invite missing, invalid, used, revoked or expired (one message: "This invite
  code isn't valid"); username taken, invalid or reserved; display name length.
- **Claim refusal**: "We couldn't verify that key" (identical for an unknown key and an already-linked
  account).
- **Link refusals**: "That Google/Discord account is already linked to another Frunchy account";
  "You can't remove your last sign-in method".
- **Provider cancelled or failed**: "Sign-in was cancelled or failed; try again".

## Test hooks

Account page sections carry these attributes:
- `data-account="username|display-name|dashboard|key-active|binding"`;
- `data-identity="{provider}"`, `data-sync`, `data-security-event`.

The page that shows a new sync key has `data-new-sync-key`.
