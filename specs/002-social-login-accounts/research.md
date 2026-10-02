# Research: Accounts with Social Sign-In

**Feature**: [spec.md](spec.md) · **Plan**: [plan.md](plan.md) · **Date**: 2026-10-02

The approach was chosen in the discussion before the spec: social sign-in inside the app, using
ASP.NET Core's own authentication. Alternatives considered and rejected there:
- Keycloak (maintenance);
- Auth0/Clerk (hosted);
- handmade passwords (risk);
- ASP.NET Core Identity (needs EF Core or custom stores, plus email; kept as a later upgrade path);
- Dex (no Discord; alpha connectors);
- oauth2-proxy (one provider per instance, no Discord, header trust);
- Casdoor (a full identity platform with critical 2026 advisories).

The notes are in `.private/idea/auth-dumb-pipe.md`. Below are the decisions inside that approach.

## R1. Sign-in mechanics

- **Decision**: ASP.NET Core authentication with **two cookie schemes plus provider handlers**:
  - `frunchy.session`: the default scheme, the signed-in browser session (R2);
  - `frunchy.external`: a short-lived (10 min) cookie holding only the provider result
    `(provider, stable user id)` between the provider callback and our own
    `/signin/complete` page (R3);
  - **Google**: `Microsoft.AspNetCore.Authentication.Google` 10.0.x (first-party);
  - **Discord**: `AspNet.Security.OAuth.Discord` 10.0.x, from the aspnet-contrib
    AspNet.Security.OAuth.Providers project, the de-facto set of community handlers.

  Both handlers set `SignInScheme = frunchy.external` and use their default callback paths
  (`/signin-google`, `/signin-discord`). The stable id is the `NameIdentifier` claim (Google `sub`,
  Discord user `id`). Scopes are reduced to the minimum: Google `openid`, Discord `identify`.
  Email and avatar are neither requested nor stored (FR-021).
- **Rationale**: no new infrastructure (constitution II). The handlers deal with the OAuth state
  parameter, the correlation cookie, PKCE (Google) and the token exchange. All Frunchy owns is the
  mapping "provider + id → user".
- **Note to verify during implementation**: that Google sign-in works with only the `openid` scope.
  If the handler needs `profile`, keep it but still store nothing from it.

## R2. Sessions

- **Decision**: the `frunchy.session` cookie is HttpOnly, `SameSite=Lax`, `Secure` (same as the
  request in dev over http), `ExpireTimeSpan = 30 days` and `SlidingExpiration = true` (FR-005).
  Its claims are `sub` (Frunchy user id), `auth_time` (UTC sign-in time) and `amr_provider` (the
  provider used).

  `OnValidatePrincipal` checks with one cheap query that the user still exists. A deleted user's
  other browsers are signed out on their next request (edge case). Signing out deletes only that
  browser's cookie (FR-006).
- **Data Protection** keys encrypt the cookies. Development uses ASP.NET's default per-user key
  ring (`~/.aspnet/DataProtection-Keys`). Production must persist them, configured with
  `Frunchy:DataProtection:KeysPath`; otherwise every restart signs everyone out.
- **Rationale**: stateless sessions need no table. Per-browser sign-out is enough
  (signing out other sessions remotely is out of scope).

## R3. Pending sign-up and the provider callback

- **Decision**: after the provider callback, `/signin/complete` reads the external cookie:
  - identity linked → issue the session and redirect to `returnUrl`;
  - not linked, user signed out → redirect to `/signup`, which offers "Create account" (invite +
    username + display name) or "I already have an account" (claim);
  - **link mode** (started from the account page, user signed in) → link the identity to the
    current user, or refuse if it's linked elsewhere;
  - **re-confirm mode** (R6) → refresh `auth_time` if the identity belongs to the current user.

  The external cookie is deleted once used and expires after 10 minutes. No account exists until
  `/signup` succeeds (US1 #7).
- **Return URLs** are accepted only when local: they start with `/` and not `//` or `/\`.
  This prevents open redirects.

## R4. Invites

- **Decision**: a code is `inv_` + 12 characters of Crockford base32 (60 bits). Only its SHA-256 is
  stored, as with sync keys. Rows have `id`, `code_hash`, `created_at`, `expires_at` (+7 days),
  `revoked_at`, `used_at` and `used_by_user_id` (`ON DELETE SET NULL`).
- Consumption happens in the **same transaction** as user creation:
  `UPDATE invites SET used_at = now(), used_by_user_id = @u WHERE code_hash = @h AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now() RETURNING id`.
  Zero rows means the sign-up is refused and the transaction rolled back, so a code creates at most
  one account even in a race (FR-004, SC-010).
- CLI: `invite-create [--days N]` (default 7; prints the code once), `invite-list` (id, created,
  expires, state) and `invite-revoke <id>`.

## R5. Claim (operator-created accounts)

- **Decision**: `POST /claim` with the sync key, rate-limited (R8):
  1. Look the key up by hash.
  2. If found and the account has **no** rows in `user_identities`, link the identity, record a
     `claimed` event and sign in.
  3. If found but the account already has identities, refuse and record `claim_refused` on that
     account (US2 #9 of the clarify session).
  4. If not found, refuse.

  Steps 3 and 4 return the **same** generic message (FR-008: no account enumeration).
- **Rationale**: the owner of an operator-created account already holds the key. Closing the path
  once any identity is linked means a leaked key can never take over a claimed account.

## R6. Recent-sign-in rule for sensitive actions

- **Decision**: actions with `RequireFresh` (generate key, delete account) check
  `auth_time ≥ now − 10 min`. When it's stale, the action redirects to
  `/signin/reconfirm?returnUrl=…`, which challenges the session's provider with
  `prompt=select_account` (Google) or `prompt=consent` (Discord), so the user actively confirms
  rather than going through a silent redirect. On return, the external identity must be one
  of the **current user's** identities. Then `auth_time` is refreshed, a `reconfirmed` event is
  recorded, and the user is sent back. A mismatch or a cancellation changes nothing.
- **Rationale**: FR-014 / SC-012. Neither provider offers a guaranteed "force re-enter password",
  so this is the strongest standard prompt each one supports. It defeats stolen or idle session
  cookies used from another browser.

## R7. CSRF and form handling

- **Decision**: `AddAntiforgery()` and `UseAntiforgery()` (between `UseRouting` and
  `UseOxpecker`). Every state-changing form renders `ctx.GetAntiforgeryInput()`, and every
  state-changing web endpoint is a POST that binds with `BindForm`, which enforces the token under
  Oxpecker. Sign-out is a POST too.
- `/api/*` endpoints read JSON with `JsonDocument` and authenticate with Bearer keys, so
  antiforgery doesn't apply to them, and the extension contract v1.2 is unchanged.
- Session cookies are `SameSite=Lax`, a second layer (FR-007).

## R8. Rate limiting

- **Decision**: ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` (part of the shared
  framework, so no new package), fixed windows partitioned by client IP:
  - `claim`: 5 per 15 min, plus 5 per 15 min per pending identity;
  - `signup`: 10 per 15 min;
  - `signin`: 30 per minute;
  - `account-write`: 30 per minute.

  Rejections return 429 with a localized page. Behind a reverse proxy, forwarded headers must be
  configured (documented, not needed locally).

## R9. One implementation of the account actions for CLI and web

- **Decision**: move the core of `regenerateKey`, `resetBinding` and `deleteUser` from
  `Operations/*.fs` into `Infrastructure/AccountActions.fs`. The operator CLI and the new account
  slices both call it. Each action records its security event with the actor (`user` or
  `operator`). The `Operations/*.fs` files keep only argument parsing, printing and exit codes.
- **Rationale**: FR-018 requires CLI and web to behave identically. One implementation guarantees
  that and keeps feature 001's tests as the regression net. It's shared infrastructure, not one
  slice calling another's private SQL (constitution III).

## R10. Security events

- **Decision**: a `security_events` table (`id`, `user_id` FK `ON DELETE CASCADE`, `occurred_at`,
  `event`, `provider` NULL, `actor`) with an index on `(user_id, occurred_at DESC)`. Event types:
  - `account_created`, `signed_in`, `claimed`, `claim_refused`;
  - `identity_linked`, `identity_unlinked`;
  - `sync_key_generated`, `binding_reset`, `reconfirmed`, `display_name_changed`.

  `display_name_changed` was added: it's cheap and also useful for spotting a takeover. Events are
  recorded through `Infrastructure/SecurityEvents.record`, inside the same transaction as the action.
  The account page shows the last 20. No IP or device data is stored (FR-022).

## R11. Configuration and secrets

- **Decision**: settings under `Frunchy:Auth:Providers:{Google,Discord}` with `Enabled`, `ClientId`
  and `ClientSecret`. `appsettings.json` commits only `"Enabled": false` and no ids or secrets.
  - Development: `dotnet user-secrets` (a `UserSecretsId` is added to the fsproj; the values live
    in `~/.microsoft/usersecrets/<id>/secrets.json`, outside the repo).
  - Production: environment variables (`Frunchy__Auth__Providers__Google__ClientSecret`).
- **Startup validation** (FR-017): if a provider is `Enabled` and its id or secret is empty, startup
  fails with "Provider 'Google' is enabled but Frunchy:Auth:Providers:Google:ClientSecret is
  missing". If no provider is enabled the site still runs, and the sign-in page says sign-in is
  unavailable.
- **Guardrail test**: no `appsettings*.json` in the repo contains a non-empty `ClientId` or
  `ClientSecret`.

## R12. Testing external sign-in

- **Decision**: real providers aren't called in tests. The test project registers a **fake
  external scheme `test`** through `Program.buildApp`'s customize hook, never in app code. Its
  handler turns `GET /signin/test?sub=abc` into an external principal immediately, signed into
  `frunchy.external`, exactly like a real provider callback.
- The app's provider registry lists the schemes registered as Frunchy providers, so the test
  provider appears only in tests.
- A `Browser` test helper keeps cookies, doesn't follow redirects automatically, and extracts
  antiforgery tokens from forms.
- Provider configuration validation (R11) is unit-tested on the configuration alone, without
  starting real handlers.

## R13. Layout and navigation

- **Decision**: `Views/Layout.page` takes a `Nav` value: signed out → a "Sign in" link; signed in
  → display name + "Account" link + a sign-out form. Handlers get it from
  `WebAuth.currentNav ctx`. Public dashboards stay public and unchanged apart from the navbar
  (FR-019). All new strings are added to `Views/Texts.fs` in en and es.

## R14. Schema changes

- **Decision**: `users.sync_key_hash` and `users.sync_key_created_at` become **nullable** (a new
  sign-up has no key until "Connect extension"), applied idempotently with
  `ALTER TABLE users ALTER COLUMN … DROP NOT NULL`. The unique index still works because Postgres
  allows many NULLs.
- New tables `user_identities`, `invites` and `security_events` (data-model.md), all with
  `CREATE TABLE IF NOT EXISTS`. Existing data (`jjchiw`) is untouched. `SCHEMA.md` is updated.

## R15. Extension

- **Decision**: no contract change. Import, status and who-am-I stay as in v1.2, and keys keep the
  same format and Bearer use. One informational message goes to the extension session: keys are now
  self-service at `/account`, so its "invalid or revoked key" text can point there. That's optional
  on its side.
