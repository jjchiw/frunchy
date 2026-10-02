# Quickstart: validate feature 002

Contracts: [web-account](contracts/web-account.md), [configuration](contracts/configuration.md),
[operator-cli](contracts/operator-cli.md). Data rules: [data-model](data-model.md).

## Prerequisites

- Everything from feature 001's quickstart (.NET 10, Docker, Postgres in `postgres-postgres-1`).
- For **manual** sign-in only (the tests use a fake provider), a free OAuth app at each provider,
  registered for local development:
  - **Google**: Google Cloud Console → APIs & Services → Credentials → OAuth client ID (Web).
    Authorized redirect URI: `http://localhost:5099/signin-google`.
  - **Discord**: Discord Developer Portal → New Application → OAuth2. Redirect:
    `http://localhost:5099/signin-discord`.
- Store the credentials **as user secrets, never in a file in the repo**:
  ```bash
  dotnet user-secrets set "Frunchy:Auth:Providers:Discord:ClientId" "<id>"
  dotnet user-secrets set "Frunchy:Auth:Providers:Discord:ClientSecret" "<secret>"
  dotnet user-secrets set "Frunchy:Auth:Providers:Discord:Enabled" "true"
  # same three for Google
  ```

## 1. Automated validation (the gate)

```bash
dotnet test
```

All feature 001 suites still pass (SC-009), plus the new suites:

| Suite | Proves |
|---|---|
| Sign-up tests | invite required and single-use, username rules and reserved names, atomic creation, abandoned sign-up leaves nothing (US1; SC-010) |
| Sign-in tests | linked identity signs in; unknown identity goes to sign-up; return URL is local only; 30-day sliding session; sign-out is per-browser (US1, US3) |
| Claim tests | key claim works only for accounts without identities; generic refusal; `claim_refused` event; rate limit (US2; SC-002, SC-011) |
| Account tests | display name edit; key generation shown once and old key revoked (SC-005); binding reset; link and unlink, never the last one (SC-004); last 10 syncs; last 20 events (SC-013) |
| Fresh-auth tests | key and delete redirect to re-confirmation when sign-in is older than 10 minutes, and continue after it (SC-012) |
| Delete tests | everything removed, signed out, identity can sign up again, other users untouched (US4; SC-006) |
| Configuration tests | an enabled provider without credentials fails startup with the named setting (SC-008); committed appsettings contain no secrets |
| CSRF tests | state-changing POSTs without a token are rejected |
| Localization | every new page in en and es (SC-007) |

## 2. Manual run with real providers

```bash
dotnet run -- invite-create                 # prints inv_… once
dotnet run --launch-profile http            # http://localhost:5099
```

1. Open `/signin` and choose Discord. Coming back for the first time lands on `/signup`.
   - **New person**: enter the invite code, a username and a display name. You land on `/account`.
     Choose "Connect extension" and paste the shown key into the extension's options. `/api/me`
     returns the new username.
   - **You (`jjchiw`)**: choose "I already have an account" and paste your current sync key. The
     account page shows `jjchiw`, and `/u/jjchiw` still shows 327 / 5,921 / 2,367.6 h (SC-002).
2. On `/account`, link Google, then sign out and sign in with Google. It's the same account.
3. Regenerate the key. The extension's old key gets 401 until you paste the new one. The security
   events list shows `sync_key_generated`.
4. `dotnet run -- invite-list` shows the used invite.

## 3. Negative checks

- Use the same invite twice: the second sign-up is refused.
- Claim `jjchiw` again from another Discord account with the key: refused, and `claim_refused`
  appears in `jjchiw`'s security events.
- Set `Frunchy:Auth:Providers:Google:Enabled=true` without a secret: the app refuses to start and
  names the missing setting.
