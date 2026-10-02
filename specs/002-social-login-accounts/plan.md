# Implementation Plan: Accounts with Social Sign-In

**Branch**: `002-social-login-accounts` | **Date**: 2026-10-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-social-login-accounts/spec.md`

## Summary

People sign in with **Discord or Google** through ASP.NET Core's own authentication handlers, with
no identity server, proxy or hosted service. Sign-in and sessions use two cookies:
- a 10-minute external cookie that carries `(provider, provider user id)` from the provider callback
  to `/signin/complete`;
- a 30-day sliding session cookie with `auth_time`, for the signed-in browser.

**Getting an account**:
- **Sign-up**: an unknown identity is sent to `/signup`, which takes an **invite code** (one-time,
  7 days, consumed atomically with user creation), a username and a display name.
- **Claim**: alternatively `/claim` links the identity to an **operator-created account by its
  current sync key**, only while that account has no identity yet.

**The private `/account` page**:
- editable display name;
- "Connect extension": a sync key shown once, which revokes the previous one;
- Crunchyroll binding reset;
- link and unlink providers, never the last one;
- the last 10 syncs and the last 20 security events;
- self-service deletion, confirmed by typing the username.

Generating a key and deleting the account require a sign-in within the last 10 minutes, or a
provider re-confirmation. All forms are antiforgery-protected POSTs. Sign-up, claim and sign-in are
rate-limited with ASP.NET's built-in limiter.

The account actions shared by the web and the CLI (regenerate key, reset binding, delete) move into
one module, so the operator CLI and the website behave identically and both record security events.

Provider credentials come only from user secrets or the environment, and the site refuses to start
with an enabled provider that's missing them. The import API (contract v1.2), the sync-key auth and
the public dashboards are unchanged.

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged)

**Primary Dependencies**:
- Oxpecker 2.1.1 (`GetAntiforgeryInput` and `configureEndpoint` verified in the DLL);
- **new**: `Microsoft.AspNetCore.Authentication.Google` 10.0.x (first-party) and
  `AspNet.Security.OAuth.Discord` 10.0.x (aspnet-contrib community handlers);
- built in, no package: cookie authentication, antiforgery, rate limiting, Data Protection.

**Storage**: PostgreSQL. Two `users` columns become nullable, and there are 3 new tables
(`user_identities`, `invites`, `security_events`), via the idempotent bootstrap.

**Testing**: the existing xUnit v3 + Testcontainers suite, plus a **fake external provider** (the
`test` scheme) registered only by the tests, and a cookie- and antiforgery-aware `Browser` helper.

**Target Platform**: unchanged (Kestrel; dev `http://localhost:5099`).

**Project Type**: web service (server-rendered pages + JSON API + worker + CLI).

**Performance Goals**: sign-up to working key in under 2 minutes excluding provider time (SC-001);
account page in under 1 s.

**Constraints**:
- no passwords, emails or avatars stored;
- secrets never committed;
- every state change is a POST with an antiforgery token;
- local-only return URLs;
- 10-minute fresh-auth rule for key generation and deletion.

**Scale/Scope**: invite-only, a handful of users.

## Constitution Check

Gates from `.specify/memory/constitution.md` (v1.1.0). Checked before research and **re-checked
after design**; both are green.

- [x] **I. Core asset**: raw imports are untouched. Self-service deletion uses the constitution's
      single exception (user deletion), through the same code as the operator command. The new
      tables (identities, invites, security events) are account data, not derived from raw imports.
- [x] **II. Boring tech**: no new service. Identity infrastructure (Keycloak, Dex, oauth2-proxy,
      Casdoor) and hosted providers were evaluated and rejected (research preamble). The only
      additions are two authentication libraries, one first-party. Rate limiting, antiforgery,
      cookies and Data Protection are part of ASP.NET Core.
- [x] **III. Vertical slices**: one file per endpoint in `Handlers/Auth/` and `Handlers/Account/`,
      and one per command in `Operations/`. Cross-slice logic lives in `Infrastructure/`
      (`WebAuth`, `AccountActions`, `SecurityEvents`), justified by FR-018: the CLI and the web
      must stay identical. `Program.fs` only wires things together.
- [x] **IV. Idempotent async ingestion**: unchanged. The import path isn't touched.
- [x] **V. Tests**: integration tests per slice against a real Postgres with a fake provider.
      Feature 001's tests remain the regression net (SC-009).
- [x] **Constraints**:
  - localized UI (all new strings in `Views/Texts.fs`, en and es);
  - no frontend build;
  - schema in `Infrastructure/Database.fs` with `SCHEMA.md` updated;
  - pinned package APIs verified;
  - secrets only in user secrets or the environment.

No violations, so Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/002-social-login-accounts/
├── plan.md  research.md  data-model.md  quickstart.md
├── contracts/
│   ├── web-account.md      # routes, forms, fresh-auth, messages, test hooks
│   ├── configuration.md    # provider settings, secrets, startup validation
│   └── operator-cli.md     # invite commands + changed behaviour
└── tasks.md                # /speckit-tasks
```

### Source Code (repository root)

```text
FrunchyBackend.fsproj               # + Google and Discord auth packages, UserSecretsId, new files in compile order
appsettings.json                    # + Frunchy:Auth:Providers:{Google,Discord}:Enabled = false (no ids or secrets)
Program.fs                          # + AddAuthentication (session, external, providers), AddAntiforgery, AddRateLimiter,
                                    #   UseAuthentication / UseAntiforgery / UseRateLimiter, new endpoints, invite CLI commands
Domains/Identity.fs                 # + reserved usernames, ProviderIdentity, SecurityEventType
Infrastructure/
├── Database.fs                     # + nullable key columns, user_identities, invites, security_events
├── AuthConfig.fs                   # NEW: provider registry from configuration + startup validation (FR-017)
├── WebAuth.fs                      # NEW: schemes, sign-in of a user, currentUser / requireSignedIn / requireFresh, Nav, local return URL
├── SecurityEvents.fs               # NEW: record (in the caller's transaction) + recent list
└── AccountActions.fs               # NEW: regenerateKey / resetBinding / deleteUser, shared by the CLI and the web, recording events
Views/
├── Texts.fs                        # + sign-in, sign-up, claim, account, delete, errors (en and es)
└── Layout.fs                       # + Nav (sign in, or display name + Account + sign out)
Handlers/Auth/
├── SignIn.fs                       # GET /signin, GET /signin/{provider}
├── Complete.fs                     # GET /signin/complete (sign in / sign up / link / re-confirm modes)
├── SignUp.fs                       # GET+POST /signup (invite + username + display name)
├── Claim.fs                        # GET+POST /claim (sync key, identity-less accounts only)
├── Reconfirm.fs                    # GET /signin/reconfirm
└── SignOut.fs                      # POST /signout
Handlers/Account/
├── AccountPage.fs                  # GET /account
├── DisplayName.fs                  # POST /account/display-name
├── SyncKey.fs                      # GET+POST /account/key (fresh)
├── ResetBinding.fs                 # POST /account/binding/reset
├── Logins.fs                       # GET /account/link/{provider}, POST /account/unlink/{provider}
└── DeleteAccount.fs                # GET+POST /account/delete (fresh)
Operations/
├── InviteCreate.fs  InviteList.fs  InviteRevoke.fs   # NEW
└── RegenerateKey.fs ResetBinding.fs DeleteUser.fs ListUsers.fs CreateUser.fs  # now call AccountActions; user-list shows providers
tests/FrunchyBackend.Tests/
├── Infrastructure/FakeProvider.fs  # `test` external scheme: /signin/test?sub=… → external principal
├── Infrastructure/Browser.fs       # cookie jar, no auto-redirect, antiforgery form helper
├── SignUpTests.fs  SignInTests.fs  ClaimTests.fs  AccountTests.fs  FreshAuthTests.fs
├── DeleteAccountTests.fs  AuthConfigTests.fs  CsrfTests.fs  InviteOperationsTests.fs
SCHEMA.md  README.md  CLAUDE.md  FRONTEND.md   # updated in the polish phase
```

**Structure Decision**: same single project. New web slices are grouped by audience (`Auth/` for
signed-out flows, `Account/` for the signed-in owner). Compile order: Domains → Infrastructure
(Database, CrunchyrollPage, ApiErrors, Auth, AuthConfig, SecurityEvents, AccountActions, WebAuth)
→ Views → Handlers → Operations → Workers → Program.

## Extension coordination

The extension session (`browser-extension-c7`, repo `../browser-extension`; find it with
ListAgents by its cwd) is **not affected by the contract**: import API v1.2, Bearer sync keys and
who-am-I don't change. Per the coordination rule it's still informed:
- **At plan time**: an FYI that sync keys become self-service at `/account` once 002 ships.
- **At the end of implementation**: a "shipped" message saying its invalid/revoked-key message
  *may* link to `{baseUrl}/account`. Optional on its side; no task is required from it.

No extension phase is planned in `tasks.md` beyond that optional item.

## Complexity Tracking

> No Constitution Check violations, so nothing to justify.
