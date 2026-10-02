---

description: "Task list for feature 002: accounts with social sign-in"
---

# Tasks: Accounts with Social Sign-In

**Input**: Design documents from `/specs/002-social-login-accounts/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: REQUIRED (constitution Principle V). Integration tests run against a real Postgres
(Testcontainers) and sign in through the **fake `test` provider**, which only the test project
registers (research R12). Within each story, write the tests first and see them fail. Feature 001's
88 tests must stay green throughout (SC-009).

**Organization**: by user story: US1 sign up + connect extension (P1), US2 claim (P2), US3 manage
(P2), US4 delete (P3). The extension is unaffected (no contract change). The only coordination item
is an optional "shipped" message in Polish.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: US1–US4 from spec.md
- **F# compile order**: add every new file to `FrunchyBackend.fsproj` (or the test fsproj) in this
  order: Domains → Infrastructure (Database, CrunchyrollPage, ApiErrors, Auth, AuthConfig,
  SecurityEvents, AccountActions, WebAuth) → Views → Handlers → Operations → Workers → Program.
- `Views/Texts.fs` and `Program.fs` are shared by many tasks, so tasks touching them are never
  [P] with each other.
- Paths are relative to the repo root.

---

## Phase 1: Setup

- [X] T001 Update `FrunchyBackend.fsproj`:
  - add `Microsoft.AspNetCore.Authentication.Google` `10.0.*` and `AspNet.Security.OAuth.Discord`
    `10.0.*`;
  - add `<UserSecretsId>frunchy-backend</UserSecretsId>` to the PropertyGroup (research R11).

  `dotnet build` must still have 0 warnings.
- [X] T002 [P] Add to `appsettings.json` a section
  `"Frunchy": { "Auth": { "Providers": { "Google": { "Enabled": false }, "Discord": { "Enabled": false } } } }`,
  with **no** `ClientId` or `ClientSecret` keys (contracts/configuration.md).

---

## Phase 2: Foundational (blocks every story)

- [X] T003 Extend `initializeSchema` in `Infrastructure/Database.fs` (data-model.md):
  - `ALTER TABLE users ALTER COLUMN sync_key_hash DROP NOT NULL` and the same for
    `sync_key_created_at` (idempotent);
  - `CREATE TABLE IF NOT EXISTS user_identities` with PK `(provider, provider_user_id)`, an FK to
    users `ON DELETE CASCADE`, and an index on `user_id`;
  - `invites` with a unique `code_hash` and `used_by_user_id` `ON DELETE SET NULL`;
  - `security_events` with an identity PK, an FK `ON DELETE CASCADE`, and an index on
    `(user_id, occurred_at DESC)`.

  Then make `Infrastructure/Auth.fs` tolerate a NULL `sync_key_hash` (lookup unchanged).
- [X] T004 [P] Update `SCHEMA.md` with the changed `users` columns and the three new tables:
  personal vs not personal, the cascade on delete, and that invites survive as `used`.
- [X] T005 [P] Extend `Domains/Identity.fs`:
  - a `reservedUsernames` set (data-model.md "Validation rules");
  - `Username.tryCreate` refuses reserved names;
  - `ProviderIdentity = { Provider: string; ProviderUserId: string }`;
  - `SecurityEventType` DU with `toDb` (the 10 types of research R10);
  - an `Actor` DU (`User | Operator`) with `toDb`.

  Also an `Invite` code helper: `generate()` gives `inv_` + 12 Crockford base32 characters,
  `normalize` (trim, uppercase the body), and `hash` (SHA-256 lowercase hex).
- [X] T006 [P] Create `Infrastructure/SecurityEvents.fs`:
  - `record (connection) (tx) (userId) (event) (provider: string option) (actor)`, which inserts in
    the caller's transaction;
  - `recent (db) (userId) (limit)`, which returns the newest first.
- [X] T007 Create `Infrastructure/AccountActions.fs` (research R9), moving the core SQL out of
  `Operations/RegenerateKey.fs`, `Operations/ResetBinding.fs` and `Operations/DeleteUser.fs`:
  - `regenerateKey (db) (userId) (actor) : Task<string>` and `resetBinding (db) (userId) (actor)`,
    each recording its security event in the same transaction;
  - `deleteUser (db) (userId) : Task<Deleted>`, with the same lock-imports-first ordering as
    feature 001;
  - plus `tryUserIdByUsername (db) (username)`.

  Rewrite the three Operations files to parse args → `AccountActions` → print/exit codes. Add a
  linked-providers column to `Operations/ListUsers.fs`, and make `Operations/CreateUser.fs` refuse
  reserved names (exit 3). Feature 001's `OperationsTests` must still pass unchanged.
- [X] T008 [P] Create `Infrastructure/AuthConfig.fs`:
  - read `Frunchy:Auth:Providers:{Google,Discord}` into `ProviderSettings` records;
  - `validate (settings) : Result<unit, string>` returns the exact message of
    contracts/configuration.md for an enabled provider missing `ClientId` or `ClientSecret`;
  - a `FrunchyProvider` record (`Name`, `DisplayName`, `Scheme`, `ReconfirmPrompt`) used as the
    provider registry type.
- [X] T009 Create `Infrastructure/WebAuth.fs` (research R2, R3, R6):
  - scheme name constants `frunchy.session` and `frunchy.external`;
  - `signInUser (ctx) (userId) (provider)`, which issues the session with claims `sub`, `auth_time`
    (UTC) and `amr_provider`, and updates `user_identities.last_used_at`;
  - `currentUserId ctx` and `requireSignedIn (handler)`, which redirects to
    `/signin?returnUrl=<path>`;
  - `isFresh ctx` (`auth_time` within 10 min) and `requireFresh (returnUrl) (handler)`, which
    redirects to `/signin/reconfirm?returnUrl=…`;
  - `localReturnUrl (raw) (fallback)` (`/…` but not `//` or `/\`);
  - `Nav` (`SignedOut | SignedIn of displayName`) and `currentNav ctx`;
  - `validatePrincipal` (signs out when the user no longer exists);
  - the external-cookie "mode" item (`signin | link | reconfirm`) set on challenge properties.
- [X] T010 Wire auth into `Program.fs`:
  - `AddAuthentication(frunchy.session)`:
    - session cookie with `ExpireTimeSpan 30 days`, `SlidingExpiration`, HttpOnly, `SameSite Lax`,
      `OnValidatePrincipal = WebAuth.validatePrincipal`;
    - external cookie with a 10-minute expiry;
    - Google and Discord handlers **only when enabled**, with `SignInScheme = frunchy.external`,
      scopes reduced to `openid` / `identify`, and ids and secrets read from configuration;
  - register each enabled provider as a `FrunchyProvider` singleton;
  - call `AuthConfig.validate` before building and throw with its message;
  - `AddAntiforgery()`, `AddRateLimiter` with fixed-window policies `signin`, `signup`, `claim`
    and `account-write` partitioned by remote IP (research R8; rejection 429 with a localized
    page), and `AddDataProtection` persisting to `Frunchy:DataProtection:KeysPath` when it's set;
  - middleware order: `UseRequestLocalization → UseRouting → UseAuthentication → UseRateLimiter →
    UseAntiforgery → errorHandler → UseOxpecker → notFound`.

  The CLI path skips provider validation.
- [X] T011 Add a `nav: WebAuth.Nav` parameter to `Views/Layout.fs` `page` and `messagePage`. The
  navbar shows "Sign in" (→ `/signin`) when signed out. Signed in, it shows the display name, an
  "Account" link and a "Sign out" button: a POST form to `/signout` with `ctx.GetAntiforgeryInput()`.
  Since the form needs `ctx`, `page` takes `ctx`. Update the callers in `Handlers/Home.fs`,
  `Handlers/Users/Dashboard.fs` and `Program.fs` (404/500), and add the nav strings in en and es to
  `Views/Texts.fs`.
- [X] T012 Create the test infrastructure:
  - `tests/FrunchyBackend.Tests/Infrastructure/FakeProvider.fs`: an `AuthenticationHandler` scheme
    `test` whose challenge immediately signs an external principal (`NameIdentifier` = query
    `sub`) into `frunchy.external`, keeps the challenge properties' mode and `returnUrl`, and
    redirects to `/signin/complete`. Also `registerFakeProvider (builder)`, which adds the scheme
    plus a `FrunchyProvider` named `test`.
  - `tests/FrunchyBackend.Tests/Infrastructure/Browser.fs`: a `Browser` over the TestServer
    handler, with a `CookieContainer`, no auto-redirect, `get`, `followRedirects`, and
    `postForm (path) (fields)`, which first GETs the form page and copies the antiforgery hidden
    field and cookie.
  - Extend `PostgresFixture.createApp` to accept extra configuration and call
    `registerFakeProvider`.
- [X] T013 [P] Foundational tests:
  - `tests/FrunchyBackend.Tests/AuthConfigTests.fs`:
    - enabled Google without a secret → the exact error message;
    - disabled → ok;
    - **guardrail**: parse every `appsettings*.json` in the repo and assert no non-empty
      `ClientId`/`ClientSecret`;
    - with providers enabled but credentials missing, `Program.buildApp` throws (SC-008).
  - `tests/FrunchyBackend.Tests/AccountActionsTests.fs`: the operator's `regenerateKey` and
    `resetBinding` record `sync_key_generated` and `binding_reset` with `actor = operator`;
    reserved usernames are refused by `user-create`.
  - The navbar renders "Sign in" on `/` and `/u/{username}` in en and es.

**Checkpoint**: `dotnet test` is green: feature 001's 88 tests plus the foundational ones.
*(Done 2026-10-02: 104/104. Deviations: rate limits are partitioned by IP only (no extra
per-identity partition for `claim`; IP already covers it at this scale). The provider name travels in
the challenge properties rather than the principal's authentication type. Antiforgery failures render
a localized 400 page.)*

---

## Phase 3: User Story 1 - Sign up with Discord or Google and connect the extension (Priority: P1) 🎯 MVP

**Goal**: an invited person signs up with a provider, picks a username, and gets a working sync key
on `/account`. A returning identity is just signed in.

**Independent Test**: `invite-create` → Browser: `/signin/test?sub=new1` → `/signup` → submit code
+ username + display name → `/account` → "Connect extension" → the key shown works on `/api/me`.
Sign out, then `/signin/test?sub=new1` → straight to `/account`.

### Tests for User Story 1 (write first, must fail)

- [X] T014 [P] [US1] `tests/FrunchyBackend.Tests/InviteOperationsTests.fs`:
  - `invite-create` prints `inv_` + 12 characters once and stores only the hash;
  - `--days 1` sets `expires_at`;
  - `invite-list` shows active/used/revoked/expired states;
  - `invite-revoke`: exit 0, 2 for an unknown id, 3 for one that isn't active.
- [X] T015 [P] [US1] `tests/FrunchyBackend.Tests/SignInTests.fs`:
  - **`/signin` page**: lists the `test` provider only; with no providers it shows the unavailable
    message;
  - **unknown identity** → redirect to `/signup`, and no user row exists;
  - **linked identity** → session issued, redirect to `returnUrl`; non-local
    `returnUrl=//evil.example` falls back to `/account`;
  - **`/account` signed out** → redirect to `/signin?returnUrl=%2Faccount`;
  - **session cookie**: HttpOnly, SameSite=Lax, expiry about 30 days;
  - **bookkeeping**: `last_used_at` updated and a `signed_in` event;
  - **sign-out**: POST `/signout` ends only that Browser's session (a second Browser stays signed
    in);
  - **language**: en and es texts on `/signin`.
- [X] T016 [P] [US1] `tests/FrunchyBackend.Tests/SignUpTests.fs`:
  - **invite refusals**: missing, unknown, used, revoked and expired codes are each refused with the
    single localized message, and no user, identity or event row is created (SC-010);
  - **username refusals**: taken, invalid and reserved usernames are refused; a display name that's
    empty or over 60 characters is refused;
  - **success**: creates user + identity, sets `invites.used_by_user_id`, records `account_created`,
    signs in and redirects to `/account`;
  - **atomicity**: two Browsers racing the same code → exactly one account;
  - **abandoned sign-up**: GET `/signup` then nothing → no rows;
  - **new account**: has no sync key (`sync_key_hash` NULL).
- [X] T017 [P] [US1] `tests/FrunchyBackend.Tests/SyncKeyTests.fs`:
  - a freshly signed-up user: GET `/account/key` → confirm → POST → a page with `data-new-sync-key`
    whose key gives 200 on `/api/me`;
  - reloading `/account` never shows the key again;
  - a second generation revokes the first (401 with the old key) (SC-005);
  - a `sync_key_generated` event with `actor = user`.

### Implementation for User Story 1

- [X] T018 [P] [US1] Create `Operations/InviteCreate.fs`, `Operations/InviteList.fs` and
  `Operations/InviteRevoke.fs` per contracts/operator-cli.md (they use the `Invite` helpers from
  T005), and add `invite-create`, `invite-list` and `invite-revoke` to the CLI map in `Program.fs`.
- [X] T019 [US1] Add to `Views/Texts.fs` (en and es) the strings for the sign-in page, sign-up form
  and its refusal messages, "I already have an account" link text, the account page basics, the
  "Connect extension" confirmation, the key-shown-once page with paste instructions, provider
  cancel/failure, and 429.
- [X] T020 [US1] Create `Handlers/Auth/SignIn.fs`:
  - `GET /signin` lists the registered `FrunchyProvider`s;
  - `GET /signin/{provider}` challenges the provider's scheme with mode `signin` and a local
    `returnUrl`;
  - rate limit `signin` via `configureEndpoint`.
- [X] T021 [US1] Create `Handlers/Auth/Complete.fs` (`GET /signin/complete`), with the `signin`
  mode only for now:
  1. authenticate `frunchy.external` and read `(provider, NameIdentifier)`;
  2. delete the external cookie when finished;
  3. if the identity is linked → `WebAuth.signInUser`, record `signed_in`, redirect to the local
     `returnUrl`;
  4. otherwise keep the external cookie and redirect to `/signup`.

  A missing or failed external result → `/signin` with the cancel/failure message.
- [X] T022 [US1] Create `Handlers/Auth/SignUp.fs`:
  - `GET /signup` needs a pending external identity (else → `/signin`) and renders the form
    (invite code, username, display name), with antiforgery and a link to `/claim`;
  - `POST /signup` (`BindForm`, rate limit `signup`) validates, then in **one transaction**
    consumes the invite with the conditional `UPDATE … RETURNING` (research R4), inserts the user
    (NULL key), the identity and `account_created`; on any refusal it rolls back and re-renders
    with the message;
  - on success: sign in, delete the external cookie, redirect to `/account`.
- [X] T023 [US1] Create `Handlers/Auth/SignOut.fs` (`POST /signout`, `BindForm` for antiforgery,
  sign out of `frunchy.session`, redirect `/`).
- [X] T024 [US1] Create `Handlers/Account/AccountPage.fs` (`GET /account`,
  `WebAuth.requireSignedIn`):
  - sections for username, display name, the dashboard link `/u/{username}`, the key-active flag,
    the linked identities and the Crunchyroll binding status;
  - a "Connect extension" button to `/account/key`;
  - the `data-account` and `data-identity` hooks from contracts/web-account.md.
- [X] T025 [US1] Create `Handlers/Account/SyncKey.fs`:
  - `GET /account/key`: confirmation page with a POST form;
  - `POST /account/key`: `BindForm`, rate limit `account-write`, calls
    `AccountActions.regenerateKey db user User`, and renders the key once with `data-new-sync-key`
    and paste instructions.

  Both are wrapped in `WebAuth.requireFresh "/account/key"`; the reconfirm page comes in US3, and a
  new user is fresh anyway.
- [X] T026 [US1] Register the US1 endpoints in `Program.fs` and the files in the fsproj, then run
  T014–T017 green, plus all earlier tests.

**Checkpoint**: an invited person can join and connect the extension without the operator.
*(Done 2026-10-02: 127/127, stable over 3 runs. Notes:
- Sign-up inserts the user first and then consumes the invite in the same transaction; the invite's FK
  to the user isn't deferrable. A race on one code still yields exactly one account.
- POST handlers validate antiforgery explicitly (`WebAuth.readValidatedForm`).
- Test infra: the test Postgres runs with `max_connections=500` and small per-database pools; 127
  tests with one database each had hit the 100-connection default.)*

---

## Phase 4: User Story 2 - Claim my existing account (Priority: P2)

**Goal**: an operator-created account (e.g. `jjchiw`) gets a social login through its sync key,
with no data lost.

**Independent Test**: `user-create` + process the 2026 sample, then capture the dashboard. Browser:
`/signin/test?sub=owner` → `/signup` → "I already have an account" → POST the key → `/account`
shows the existing username, and the dashboard is identical.

### Tests for User Story 2 (write first, must fail)

- [X] T027 [P] [US2] `tests/FrunchyBackend.Tests/ClaimTests.fs`:
  - **success**: claim with the key of an identity-less account → linked, signed in, `claimed`
    event; `/u/{username}` stats and recent list identical before and after (SC-002);
  - **already-linked account**: correct key → refused with the generic message, and a
    `claim_refused` event on that account (SC-011);
  - **wrong or revoked key**: the identical generic message and no event;
  - **rate limit**: the 6th attempt within 15 minutes → 429;
  - **no invite needed**: `invites` untouched;
  - **no pending identity**: `/claim` without one → `/signin`.

### Implementation for User Story 2

- [X] T028 [US2] Add the claim strings (en and es) to `Views/Texts.fs`.
- [X] T029 [US2] Create `Handlers/Auth/Claim.fs`:
  - `GET /claim`: needs a pending external identity; a form for the sync key;
  - `POST /claim`: `BindForm`, rate limit `claim`. Then:
    1. look the user up by `SyncKey.hash`;
    2. in one transaction, if the user has 0 rows in `user_identities` → insert the identity +
       `claimed` → commit → sign in → `/account`;
    3. if the user has identities → `claim_refused` (`actor = user`) and the generic message;
    4. if not found → the same generic message.
- [X] T030 [US2] Register the claim endpoints in `Program.fs` and the fsproj, then run T027 green
  plus everything earlier.

**Checkpoint**: you can attach Discord or Google to `jjchiw` and keep the 5,921 episodes.

---

## Phase 5: User Story 3 - Manage my account (Priority: P2)

**Goal**: display name, key regeneration with fresh-auth, binding reset, link and unlink providers,
the last 10 syncs and last 20 security events, per-browser sign-out.

**Independent Test**: signed in, rename the display name (the dashboard shows it), regenerate the
key after the 10-minute window (re-confirm → back), link a second test identity and sign in with
it, unlinking the last one is refused, reset the binding, and the syncs and events lists show the
expected rows.

### Tests for User Story 3 (write first, must fail)

- [X] T031 [P] [US3] `tests/FrunchyBackend.Tests/AccountTests.fs`:
  - **display name**: valid → the dashboard shows it immediately and `display_name_changed` is
    recorded; invalid → refused;
  - **binding reset** → binding cleared, history kept, `binding_reset` event;
  - **linking**: link a second identity (`/account/link/test?sub=second` in link mode) → both sign
    in to the same account, `identity_linked` event; linking an identity owned by another user →
    refused, neither account changes (SC-004);
  - **unlinking**: unlink → that identity no longer signs in, `identity_unlinked` event; unlinking
    the last identity → refused;
  - **lists**: the syncs list shows the last 10 submissions with status and counts (seed 12, see 10
    newest); the security events list shows the last 20 newest first (SC-013);
  - **deleted elsewhere**: the user deleted by the CLI while signed in → the next `/account` request
    is signed out and redirected.
- [X] T032 [P] [US3] `tests/FrunchyBackend.Tests/FreshAuthTests.fs`:
  - with `auth_time` older than 10 minutes, GET `/account/key` → redirect to `/signin/reconfirm` →
    test provider round trip with the **same** identity → back to `/account/key`, `reconfirmed`
    event (SC-012);
  - re-confirming with a **different** identity → no refresh and still blocked;
  - cancelling (the fake provider returns a failure) → no action and still signed in.

  Shift `auth_time` with a test clock through `TimeProvider`: `WebAuth` uses `TimeProvider` from
  DI, and tests register a `FakeTimeProvider`-style implementation.

### Implementation for User Story 3

- [X] T033 [US3] Add the strings for display name, binding reset, logins and link/unlink messages,
  syncs and security-event labels (one per event type), and re-confirm to `Views/Texts.fs` (en and
  es).
- [X] T034 [P] [US3] Create `Handlers/Account/DisplayName.fs` (`POST /account/display-name`,
  `BindForm`, rate limit `account-write`, 1–60 characters, update + `display_name_changed` in one
  transaction).
- [X] T035 [P] [US3] Create `Handlers/Account/ResetBinding.fs` (`POST /account/binding/reset`,
  calls `AccountActions.resetBinding db user User`).
- [X] T036 [US3] Create `Handlers/Account/Logins.fs`:
  - `GET /account/link/{provider}` challenges with mode `link`;
  - `POST /account/unlink/{provider}` does a conditional delete (only when the count > 1) +
    `identity_unlinked`, and refuses the last one.

  Extend `Handlers/Auth/Complete.fs` with **link mode**: if the identity is free → insert +
  `identity_linked`; if it's owned by someone else → refuse with the message; redirect to
  `/account`.
- [X] T037 [US3] Create `Handlers/Auth/Reconfirm.fs` (`GET /signin/reconfirm?returnUrl=`,
  requireSignedIn): challenge the session's provider with mode `reconfirm` and its prompt
  (`select_account` for Google, `consent` for Discord, via `FrunchyProvider.ReconfirmPrompt`).
  Extend `Handlers/Auth/Complete.fs` with **reconfirm mode**: the identity must belong to the
  current user → re-issue the session with a new `auth_time` + `reconfirmed` → redirect to the local
  `returnUrl`; otherwise no change and back to `/account` with a message. Switch `WebAuth` to
  `TimeProvider`.
- [X] T038 [US3] Extend `Handlers/Account/AccountPage.fs` with the display-name form, the binding
  reset form, the link buttons (for providers not linked yet) and unlink buttons, the last 10
  syncs (`data-sync`) and the last 20 security events (`data-security-event`, via
  `SecurityEvents.recent`).
- [X] T039 [US3] Register the US3 endpoints in `Program.fs` and the fsproj, then run T031–T032
  green plus everything earlier.

**Checkpoint**: everything the operator could do for a user, the user can now do themselves.
*(Done 2026-10-02: 147/147. Decisions:
- one sign-in identity per provider per account, so "unlink provider X" is unambiguous; the tests
  register a second fake provider `test2` for cross-provider linking;
- `Layout.timeElement` is now shared by the dashboard and account page;
- account actions report through `?msg=` notices.)*

---

## Phase 6: User Story 4 - Delete my account (Priority: P3)

**Goal**: self-service deletion with typed confirmation and the fresh-auth rule. It has the same
effect as the operator deletion.

**Independent Test**: sign up, sync the 2026 sample, wait past the fresh window → re-confirm → type
the username → deleted. Then `/u/{username}` → 404, key → 401, the same identity → `/signup`
again, and another user is untouched.

### Tests for User Story 4 (write first, must fail)

- [X] T040 [P] [US4] `tests/FrunchyBackend.Tests/DeleteAccountTests.fs`:
  - **wrong username** typed → refused and nothing deleted;
  - **success**: every row of the user is gone, including `user_identities` and `security_events`
    (SC-006); the invite stays `used` with `used_by_user_id` NULL;
  - **after deletion**: the Browser is signed out; `/u/{username}` → 404; the sync key → 401; the
    same test identity → `/signup` (treated as new); the username can be taken again;
  - **other users**: figures unchanged;
  - **fresh rule**: a stale session goes through re-confirmation first.

### Implementation for User Story 4

- [X] T041 [US4] Add the delete-confirmation, warning and "account deleted" notice strings to
  `Views/Texts.fs` (en and es).
- [X] T042 [US4] Create `Handlers/Account/DeleteAccount.fs`, both endpoints wrapped in
  `WebAuth.requireFresh "/account/delete"`:
  - `GET /account/delete`: warning + "type your username" form;
  - `POST /account/delete`: `BindForm`, rate limit `account-write`, username must match
    (case-insensitive) → `AccountActions.deleteUser` → sign out → redirect `/?deleted=1`, where the
    home page shows the notice.

  Register it in `Program.fs` and the fsproj, then run T040 green plus everything earlier.

**Checkpoint**: all four stories work independently, and `dotnet test` is green.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T043 [P] `tests/FrunchyBackend.Tests/CsrfTests.fs`: every state-changing POST (`/signup`,
  `/claim`, `/signout`, `/account/display-name`, `/account/key`, `/account/binding/reset`,
  `/account/unlink/{p}`, `/account/delete`) **without** an antiforgery token is rejected and
  changes nothing. `/api/imports` with a Bearer key still works without a token (contract v1.2
  unchanged).
- [ ] T044 [P] `tests/FrunchyBackend.Tests/AccountLocalizationTests.fs`: every new page (`/signin`,
  `/signup`, `/claim`, `/account`, `/account/key`, `/account/delete`, `/signin/reconfirm`, the 429
  page) renders with `lang="es"` and Spanish text for `Accept-Language: es`, and English for `fr`
  (SC-007).
- [ ] T045 Check that no user-facing string literal is left in `Handlers/Auth/*.fs`,
  `Handlers/Account/*.fs` or `Views/Layout.fs` outside `Views/Texts.fs`, and fix any found
  (FR-020).
- [ ] T046 [P] Update `README.md`:
  - an "Accounts" section: sign-in, invites, claiming;
  - Getting Started: registering the Google and Discord apps, `dotnet user-secrets set …` (never in
    appsettings), `invite-create`;
  - Roadmap: 002 → Implemented.
- [ ] T047 [P] Update `FRONTEND.md` (navbar states, `/signin`, `/signup`, `/claim`, `/account*`
  pages, fresh-auth behaviour) and `CLAUDE.md`: turn the 002 "Active feature" note into the
  architecture (auth schemes, `WebAuth`, `AccountActions`, the fake test provider), list the new
  operator commands, and repeat the secrets rule.
- [ ] T048 Run `specs/002-social-login-accounts/quickstart.md` section 1 (`dotnet test`), plus
  section 3's negative checks with the fake provider, on a fresh database, and record the results
  in a "Validation log" in `specs/002-social-login-accounts/quickstart.md`.
- [ ] T049 **(user, manual)** Register the Discord and/or Google OAuth apps, set the user secrets,
  run `dotnet run --launch-profile http`, and **claim `jjchiw` in `frunchydb` for real** (quickstart
  section 2). Expected: `/u/jjchiw` still shows 327 / 5,921 / 2,367.6 h. Record the result in the
  quickstart validation log.
- [ ] T050 Send the extension session (find it with ListAgents by cwd `../browser-extension`) a
  "002 shipped" message. Sync keys are self-service at `{baseUrl}/account`; its 401 text *may* link
  there. Optional for it; no contract change.

---

## Dependencies & Execution Order

- **Setup (T001–T002)**: T002 runs in parallel with T001.
- **Foundational (T003–T013)**: T005, T006 and T008 in parallel with T003 and T004 → T007 (needs T005
  and T006) → T009 (needs T008) → T010 (needs T008 and T009) → T011 → T012 → T013. This phase
  **blocks every story**.
- **US1 (T014–T026)**: tests T014–T017 in parallel → T018 in parallel with T019 → T020 → T021 →
  T022 → T023 → T024 → T025 → T026.
- **US2 (T027–T030)**: needs US1's `Complete.fs` and `SignUp.fs` (pending identity + the link to
  `/claim`).
- **US3 (T031–T039)**: needs US1 (`AccountPage`, `Complete`). T034 and T035 in parallel; T036 →
  T037 (both extend `Complete.fs`) → T038 → T039.
- **US4 (T040–T042)**: needs US3's re-confirmation (T037) for the fresh rule.
- **Polish (T043–T050)**: after the stories. T049 is done by you, by hand, with real provider apps.

US2, US3 and US4 all build on US1's sign-in flow. US2 and US3 are independent of each other.

## Parallel Examples

```text
# Foundational
T005 Domains/Identity.fs   T006 Infrastructure/SecurityEvents.fs   T008 Infrastructure/AuthConfig.fs   T004 SCHEMA.md

# US1 tests
T014 InviteOperationsTests   T015 SignInTests   T016 SignUpTests   T017 SyncKeyTests

# US3 slices
T034 Handlers/Account/DisplayName.fs   T035 Handlers/Account/ResetBinding.fs

# Polish
T043 CsrfTests   T044 AccountLocalizationTests   T046 README.md   T047 FRONTEND.md + CLAUDE.md
```

## Implementation Strategy

1. **MVP = Phases 1–3 (US1)**: invite + sign-up + "Connect extension". A second person can join
   with no operator steps beyond `invite-create`.
2. **+ US2**: claim `jjchiw` (T049 does it for real).
3. **+ US3**: self-service management and re-confirmation.
4. **+ US4**: self-service deletion.
5. **Polish**: the CSRF and localization sweeps, docs, quickstart, and the "shipped" message to the
   extension.

## Notes

- Never commit provider credentials. They belong in user secrets or the environment, and T013's
  guardrail test enforces it.
- Commit after each task or logical group on `002-social-login-accounts`.
- Keep the import API, sync-key format and dashboards byte-for-byte compatible. Feature 001's tests
  are the proof.
