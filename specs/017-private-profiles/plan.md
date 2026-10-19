# Implementation Plan: Private Profiles

**Branch**: `017-private-profiles` | **Date**: 2026-10-19 | **Spec**: [private-profiles.spec.md](private-profiles.spec.md)

## Summary

Profiles (every page under `/u/{username}`) become private by default:
- **Instance default**: `Frunchy:Profiles:DefaultVisibility` picks private or public.
- **Owner's choice**: each owner can choose public or private on `/account`.
- **Share link**: each owner can create one link, `/s/{token}`. It gives a browser a signed grant for the whole
  profile until the link is replaced or turned off.
- **One rule everywhere**: everything that links into a profile (comment authors, compatibility, the MyAnimeList
  review) follows the same rule.
- **Same 404**: a hidden profile answers exactly like an unknown username.

## Technical Context

- **Stack**: F# on .NET 10, Oxpecker, raw Npgsql, Postgres, and xUnit v3 with Testcontainers.
- **Depends on 016**: the grant cookie is protected with data-protection keys, which 016 made durable and shared
  across copies.
- **New**:
  - `profile_settings` table;
  - `Infrastructure/Profiles.fs`;
  - `Handlers/Share.fs`, `Handlers/Account/Visibility.fs`, `Handlers/Account/ShareLink.fs`;
  - `ShareToken` in `Domains/Identity.fs`.
- **Changed**:
  - every `Handlers/Users/*.fs`, `Handlers/Episodes/Episode.fs`, `Handlers/Review/MalReview.fs`, `Handlers/Home.fs`;
  - `Handlers/Account/AccountPage.fs`, `Operations/ListUsers.fs`;
  - `Views/Texts.fs`, `Views/Components.fs`;
  - `Program.fs` (setting, routes, `share` limiter);
  - `PostgresFixture.fs`.
- **Performance**: one primary-key query per profile request (`loadAccess`), and one cookie read. Episode comments
  get a `LEFT JOIN`, with no query per comment.

## Constitution Check

- **Simplicity**: one table, one cookie, and no new framework or package. Pass.
- **Watch history is the core asset**: it is protected rather than changed, and aggregates are unchanged. Pass.
- **SQL guardrail**: literal SQL with parameters. Pass.
- **Tests (V)**: every route is tested for strangers, the owner, link holders and operators, plus the
  indistinguishable 404. Pass.
- **Localization**: every new string is in `Texts.fs`, in en and es. Pass.
- **Design system**: new UI uses the existing components (`accountSection`, `postButton`, the copy button) and adds
  `data-*` hooks only (contract). Pass.

## Project Structure

See [research.md](research.md) R5 and R6 for each file's change, and [tasks.md](tasks.md) for the order.

## Review points (main session)

1. **Check placement**: on every `/u/` route, the check runs right after the user lookup and before any other
   query. A hidden profile costs no history query.
2. **The 404 body** is the unknown-user one: same function, same arguments.
3. **Tokens**: only hashes are stored, the token is shown once, and the shape is checked before the hash lookup.
4. **Grants**: they name the link id, so replacing or revoking a link ends access. The cookie is data-protected,
   HttpOnly and SameSite=Lax.
5. **Operators get no exception.**
6. **The extension contract is untouched** (`/api/*`).
7. **Fixture default `public`**: the real default stays `private`, and a test pins it.

## Delegation

Two Sonnet agents in sequence: phase 1 (T001–T009), then phase 2 (T010–T017). The main session reviews each diff,
runs the suite, and walks the share flow in a browser under the AppHost.

## Complexity Tracking

None.
