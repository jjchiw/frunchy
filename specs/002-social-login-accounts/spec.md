# Feature Specification: Accounts with Social Sign-In

**Feature Branch**: `002-social-login-accounts`

**Created**: 2026-10-02

**Status**: Draft

**Input**: User description: "Accounts: sign in and sign up with social login (Discord and Google first), self-service sync keys and account deletion. Today Frunchy users are created by the operator with a command that prints a sync key; there is no web login. This feature lets people get their own Frunchy account and manage it, so a second person can be invited without the operator in the loop."

## Clarifications

### Session 2026-10-02

- Q: Who can sign up? → A: Invite-only. Sign-up needs a one-time invite code created by the operator; it expires after 7 days. Opening sign-up to everyone is later work.
- Q: How does an existing operator-created account get claimed? → A: Signed in with the new provider identity, the owner enters their current sync key once on an "I already have an account" page. This works only for accounts that have no linked identity yet.
- Q: Do sensitive actions need extra confirmation? → A: Generating a sync key and deleting the account require a sign-in within the last 10 minutes; otherwise the user re-confirms with their provider first.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sign up with Discord or Google and connect the extension (Priority: P1)

A new person, invited by the operator with a one-time invite code, opens Frunchy and chooses "Sign in with Discord" or "Sign in with Google". After approving at the provider, they return to Frunchy for the first time and pick a username (their public dashboard address, `/u/{username}`) and a display name. Their account exists. On their account page they choose "Connect extension", get a sync key shown once, paste it into the browser extension, and their history starts syncing. Next time they choose the same provider, they're simply signed in.

**Why this priority**: This is what removes the operator from the loop. Without it, nobody else can join, and that was the condition set for inviting a second user (feature 001).

**Independent Test**: Create an invite code with the operator command. With a test provider, sign up as a new person using the code, pick a username, get a sync key on the account page, and call "who am I" with that key: it returns the new username. Sign out, sign in again with the same identity, and land on the same account without being asked for a username.

**Acceptance Scenarios**:

1. **Given** a visitor not signed in, **When** they open the sign-in page, **Then** they see one button per enabled provider (Discord, Google), with no username or password fields.
2. **Given** a provider identity that Frunchy has never seen, **When** the person returns from the provider, **Then** they're asked for an invite code, a username and a display name before any account exists. They're also offered "I already have an account" (User Story 2).
3. **Given** the username form, **When** the person submits a username that's taken, invalid (same rule as today: 3–30 characters, a–z 0–9 _ -, starting with a letter or digit) or reserved, **Then** they see why, in the page's language, and can pick another. Nothing is created until a valid username is accepted.
4. **Given** a valid unused invite code, a valid free username and a display name, **When** the person submits them, **Then** the account is created, the provider identity is linked to it, the person is signed in, and they land on their account page.
5. **Given** a signed-in user with no sync key yet, **When** they choose "Connect extension", **Then** a sync key is shown exactly once, with instructions to paste it into the extension, and it immediately works for the extension (who-am-I returns their username).
6. **Given** a provider identity already linked to an account, **When** that person signs in with it, **Then** they're signed in to that account directly.
7. **Given** a person who abandons sign-up at the username step, **When** they come back later with the same provider identity, **Then** they're asked for a username again. No half-created account exists in between.
8. **Given** a visitor not signed in, **When** they open the account page directly, **Then** they're sent to the sign-in page, and after signing in they return to the account page.
9. **Given** sign-up without an invite code, or with a code that's unknown, already used, revoked or older than 7 days, **When** the person submits, **Then** sign-up is refused with an explanation in the page's language and nothing is created.
10. **Given** a valid invite code used successfully, **When** anyone tries the same code again, **Then** it's refused: each code creates exactly one account.

---

### User Story 2 - Claim my existing account (Priority: P2)

Someone whose account was created by the operator before this feature (e.g. "jjchiw", who already holds a real five-year history) signs in with Discord or Google for the first time. Instead of creating a new, empty account, they attach that provider identity to their existing account. From then on they sign in normally and keep all their data.

**Why this priority**: The only real user today is an operator-created one. Without this, the owner would have to delete their history and start over, or keep using the operator command forever.

**Independent Test**: Create a user with the operator command and process the 2026 sample for it. Note the dashboard figures. Sign in with a new provider identity and claim that account with its sync key. Confirm the account page shows the existing username, the dashboard figures are identical, and signing in again lands on the same account.

**Acceptance Scenarios**:

1. **Given** an operator-created account with history, **When** its owner signs in with a new provider identity and completes the claim, **Then** the identity is linked to that account and no new account is created.
2. **Given** a completed claim, **When** the dashboard is opened, **Then** its figures and recent episodes are identical to those before the claim.
3. **Given** a claim attempt, **When** the claimant can't prove they own that account, **Then** nothing is linked and the attempt is refused with an explanation.
4. **Given** a person signed in with a new provider identity on the "I already have an account" page, **When** they enter the current sync key of an account that has **no linked identity**, **Then** the identity is linked to that account and they're signed in to it. No invite code is needed.
5. **Given** an account that already has at least one linked identity, **When** someone enters its sync key on the claim page, **Then** the claim is refused, even with the correct key. Further identities can only be added from the account page while signed in.
6. **Given** a wrong or revoked key, **When** it's entered on the claim page, **Then** the claim is refused without revealing whether the key or the account exists, and repeated attempts are limited.

---

### User Story 3 - Manage my account (Priority: P2)

A signed-in user manages their account in one place:
- see their username and a link to their public dashboard;
- generate a new sync key, which revokes the old one, for example after losing it or reinstalling the extension;
- see whether their account is bound to a Crunchyroll history (account and profile), and reset that binding themselves if they changed profile;
- link a second provider (add Google to a Discord-created account, or the reverse);
- unlink a provider, as long as one remains;
- sign out.

**Why this priority**: These are the things only the operator can do today. Letting users do them is most of the point of having accounts.

**Independent Test**: Signed in, regenerate the key: the old key gets "unauthorized" and the new one works. Link a second test provider and sign in with it: same account. Unlink the first: it no longer signs in. Try to unlink the last provider: refused. Reset the binding, and a submission from another Crunchyroll profile is then accepted. Sign out: the account page needs sign-in again.

**Acceptance Scenarios**:

1. **Given** a signed-in user, **When** they open their account page, **Then** they see their username, display name, dashboard link, linked providers, whether a sync key is active (never the key itself), and whether their history is bound to a Crunchyroll account and profile.
2. **Given** a signed-in user with an active key, **When** they generate a new key and confirm, **Then** the new key is shown exactly once and the previous key stops working immediately.
3. **Given** a signed-in user, **When** they link another enabled provider whose identity isn't linked to any account, **Then** it's linked and they can sign in with either.
4. **Given** a signed-in user, **When** they try to link a provider identity that's already linked to a different Frunchy account, **Then** it's refused with an explanation, and neither account changes.
5. **Given** a user with two linked identities, **When** they unlink one, **Then** it can no longer be used to sign in. **When** they try to unlink the last one, **Then** it's refused.
6. **Given** a bound user, **When** they reset their Crunchyroll binding and confirm, **Then** their history is kept, the binding is cleared, and the next accepted submission binds again (same effect as the operator's reset).
7. **Given** a signed-in user, **When** they sign out, **Then** that browser is no longer signed in. The extension's sync key keeps working, because signing out doesn't revoke it.

---

### User Story 4 - Delete my account (Priority: P3)

A user can delete their own account and everything Frunchy holds about them, without asking the operator.

**Why this priority**: It's a privacy right now that profiles are public, and it replaces the manual request to the operator. It's used rarely.

**Independent Test**: Sign up, sync the 2026 sample and delete the account with confirmation. Confirm the dashboard address shows "user not found", the sync key is rejected, signing in with the same identity starts a fresh sign-up, and another user's data is untouched.

**Acceptance Scenarios**:

1. **Given** a signed-in user, **When** they choose to delete their account, **Then** they must confirm explicitly by typing their username, and they're told it can't be undone and removes their raw submissions too.
2. **Given** a confirmed deletion, **When** it completes, **Then** all of their data is removed (same effect as the operator's deletion in feature 001, linked provider identities included), they're signed out, their dashboard shows "user not found", and their sync key is rejected.
3. **Given** a deleted account, **When** the same person signs in again with the same provider identity, **Then** they're treated as new (User Story 1) and can pick the same username if it's still free.
4. **Given** a user whose last sign-in was more than 10 minutes ago, **When** they start "delete account" or "generate a new sync key", **Then** they're first sent to their provider to re-confirm, and returned to the same action afterwards. A user who signed in within the last 10 minutes goes straight to the on-page confirmation.

---

### Edge Cases

- **Provider sign-in cancelled or failing** (user cancels at Discord, or the provider is down): back on the sign-in page with a clear message in the page's language; nothing is created or linked.
- **Provider returns an identity without email or with a changed display name**: sign-in still works. The account is keyed on the provider's stable user identifier, never on email or name.
- **A provider is disabled later by the operator**: its button disappears. Users linked only to it can't sign in on the web until the operator helps (their extension key keeps working). The sign-in page doesn't reveal which accounts are affected.
- **Two people race for the same username**: exactly one gets it; the other is told it's taken and can choose again.
- **Reserved usernames**: names that would collide with Frunchy's own pages or be misleading (e.g. `admin`, `api`, `account`, `login`, `signin`, `settings`, `support`, `frunchy`) are refused at sign-up.
- **Same person, different provider, not linked yet**: signing in with Google when the account was created with Discord creates a *new* account offer (they're different identities). The sign-up page reminds them they can instead sign in with their existing provider and link Google from their account page.
- **Signed in on several browsers**: each browser has its own session; signing out on one doesn't sign out the others.
- **Account deleted while signed in elsewhere**: the other browser's next request finds no account and is signed out.
- **Sync key lost**: generate a new one on the account page; the old one is revoked.
- **Lost sync key on an unclaimed account**: the claim path needs the key, so the operator regenerates it (`user-regenerate-key`) and gives the new one to the owner, who then claims.
- **Invite code problems** (typo, expired, used, revoked): sign-up is refused with the reason; the operator can create a new code.
- **Re-confirmation cancelled at the provider**: the sensitive action isn't performed; the user stays signed in.
- **Operator commands**: they keep working as a recovery tool and stay consistent with self-service actions. For example, an operator-regenerated key also invalidates the one shown on the account page.

## Requirements *(mandatory)*

### Functional Requirements

**Sign-in and sign-up**

- **FR-001**: Users MUST be able to sign in with an external provider. This feature ships Discord and Google. Frunchy MUST NOT ask for, store or accept passwords.
- **FR-002**: Each provider identity MUST be identified by the provider plus that provider's stable user identifier, never by email or name. One provider identity belongs to at most one Frunchy account.
- **FR-003**: The first sign-in with an unknown provider identity MUST lead to sign-up, where the person chooses a username (existing rule, unique, reserved names refused) and a display name (1–60 characters). The account is created only when both are valid.
- **FR-004**: Sign-up MUST require a valid invite code: one that exists, isn't used, revoked or older than 7 days. A code MUST create at most one account.
- **FR-004a**: The operator MUST be able to create an invite code (shown once), list invites with their state, and revoke an unused one.
- **FR-005**: A signed-in session MUST last until the user signs out or 30 days after their last visit. It MUST be specific to the browser.
- **FR-006**: Users MUST be able to sign out, which ends that browser's session only.
- **FR-007**: The sign-in, sign-up and account pages MUST be protected so that another website can't trigger sign-in, linking, key generation, binding reset or deletion on the user's behalf.

**Existing accounts**

- **FR-008**: A person signed in with a new provider identity MUST be able to claim an existing account by entering its current sync key, **only if that account has no linked identity**. The claim links the identity and keeps all data. A failed claim MUST link nothing, MUST NOT reveal whether the account or key exists, and repeated failures MUST be limited.

**Account management (signed-in user only)**

- **FR-009**: The account page MUST show:
  - username and display name;
  - a dashboard link;
  - linked providers;
  - whether a sync key is active (never the key itself);
  - the Crunchyroll binding status.
- **FR-010**: Users MUST be able to generate a sync key ("Connect extension"). The key is shown exactly once, and generating a new one immediately revokes the previous one. The sync key keeps working for the extension exactly as in the import contract (v1.2); signing in or out doesn't affect it.
- **FR-011**: Users MUST be able to reset their own Crunchyroll binding, with the same effect as the operator command.
- **FR-012**: Users MUST be able to link another enabled provider identity that's not linked elsewhere. They MUST be able to unlink a provider only if at least one other remains.
- **FR-013**: Users MUST be able to delete their account after typing their username to confirm. The effect is the same as the operator deletion (all data, raw submissions included, linked identities included; constitution I exception), and it signs them out.
- **FR-014**: Generating a sync key and deleting the account MUST require a sign-in within the last 10 minutes. Otherwise the user MUST re-confirm with their provider first and then return to the action.
- **FR-015**: Account pages MUST be reachable only by the signed-in owner. Unauthenticated visitors MUST be redirected to sign-in and returned afterwards.

**Operation and configuration**

- **FR-016**: Each provider MUST be individually enabled by the operator. Its credentials MUST come only from the server's environment or the developer's local secret storage, never from files in the repository.
- **FR-017**: The site MUST refuse to start when a provider is enabled but its credentials are missing, with a message naming the provider and the missing setting.
- **FR-018**: The operator commands from feature 001 MUST keep working (as a recovery tool) and stay consistent with self-service actions.

**Unchanged and cross-cutting**

- **FR-019**: Public dashboards, the import contract (v1.2) and the extension's sync-key authentication MUST be unchanged.
- **FR-020**: All new user-facing text MUST exist in English (default) and Spanish, chosen by the browser's language like the rest of the site.
- **FR-021**: Frunchy MUST store only what this feature needs from providers: the provider, the stable user identifier, and when it was linked and last used. Email addresses and avatars MUST NOT be stored.

### Key Entities

- **User**: existing entity from feature 001 (username, display name, sync key, Crunchyroll binding), now also reachable by signing in.
- **Linked Identity**: a provider (Discord, Google) plus that provider's stable user identifier, belonging to exactly one user, with linked-at and last-used times. A user has one or more once they've signed in; operator-created users have none until they claim.
- **Session**: a signed-in browser. It belongs to one user, ends at sign-out or after 30 days of inactivity, and is never shared with the extension.
- **Pending Sign-up**: a provider identity that has authenticated but has no account yet, waiting for a username. It is short-lived and not an account.
- **Invite**: a one-time code created by the operator. It's valid for 7 days, can be revoked, and once used records which account it created.
- **Claim**: proof of ownership of an operator-created account (its current sync key), accepted only while the account has no linked identity.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new person goes from the home page to a working sync key in under 2 minutes, excluding time spent at the provider's consent screen.
- **SC-002**: The owner of an operator-created account claims it with zero data loss: dashboard figures and recent episodes identical before and after.
- **SC-003**: 100% of account-page requests without a valid session are redirected to sign-in, and no account data is shown.
- **SC-004**: No provider identity is ever linked to two accounts, and no account ever ends up with zero identities through unlinking (0 cases in tests).
- **SC-005**: After a key is regenerated, 100% of requests with the previous key are rejected.
- **SC-006**: After a self-service deletion, none of the user's data remains and other users' figures are unchanged (as SC-010 of feature 001).
- **SC-007**: Every new interface text exists in English and Spanish (0 untranslated strings).
- **SC-008**: With a provider enabled but its credentials missing, the site never starts (100% of tested configurations).
- **SC-009**: Import, status, who-am-I and public dashboard behave exactly as before (all feature 001 tests still pass).
- **SC-010**: 100% of sign-up attempts without a valid invite code are refused, and every code creates at most one account.
- **SC-011**: The sync-key claim succeeds only for accounts with no linked identity (0 claims accepted on already-linked accounts in tests).
- **SC-012**: 100% of key generations and deletions attempted more than 10 minutes after sign-in go through provider re-confirmation first.

## Assumptions

- **Providers**: Discord and Google in this feature; AniList and others later (the identity model allows adding providers without changing accounts).
- **Usernames can't be changed** after creation in this feature: the dashboard address must stay stable. Display names are set at sign-up; editing either is later work.
- **No email** is collected or stored; Frunchy doesn't send email in this feature.
- **Operator commands stay** as a recovery tool (for example for a user whose only provider was disabled, or an unclaimed account whose key was lost). New operator commands: create, list and revoke invites.
- **Opening sign-up** to everyone (no invite) is later work; the invite rule is the only gate in this feature.
- **One account per person** is expected but not enforced: someone could create two accounts with two different providers. Linking from the account page is the intended way to have both.
- **The extension** doesn't change in this feature: users still paste the key. A smoother "Connect extension" hand-off (e.g. the extension opening the account page and receiving the key) is possible later, and would be coordinated with the extension session through the contract.
- **Abuse protection**: sensible limits on sign-in and key generation attempts; no CAPTCHA in this feature.
- **Privacy**: provider sign-in shares only a stable identifier with Frunchy for this purpose. Public dashboards stay as decided in feature 001.
- **Out of scope**: passwords, passkeys, email verification/magic links, 2FA, private profiles, roles/admin UI, AniList/MAL login, username changes, signing out other sessions remotely.
