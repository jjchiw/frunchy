# Feature Specification: Members-only profiles, the new default

**Feature Branch**: `021-members-profiles`

**Created**: 2026-10-20

**Status**: Draft

**Input**: "the default should be 'members'" (the owner, choosing three profile levels: Private, Members, Public). Charts stay public: "it's like we can name our group and compete with other Frunchy installations".

## Clarifications

### Session 2026-10-20 (decided with the owner, then in the autonomous run; the owner can overturn any of them)

- Q: The levels? → A: Three, for every page under `/u/{username}`:
  - **Private**: the owner and anyone holding their share link (unchanged).
  - **Members**: anyone signed in to this Frunchy, plus share-link holders.
  - **Public**: everyone.

  Operators get no exception at any level, as before.
- Q: The default? → A: `members`. `Frunchy:Profiles:DefaultVisibility` accepts `private`, `members` or `public`; missing or blank means `members`. Users who never chose follow the default, so on the live site they move from Private to Members; users who chose keep their choice. (Owner decision.)
- Q: A signed-out visitor on a Members profile? → A: A redirect to `/signin?returnUrl=/u/{username}/…`, so a friend who was sent the address signs in and lands on it. This confirms that the username exists; that's accepted, since every member can see the profile anyway. A Private profile keeps answering exactly like an unknown username (017), and a signed-in user is never redirected (they're already a member).
- Q: Charts, series and episode pages? → A: Unchanged and public (owner decision). They never name anyone, and the minimum of 3 people still applies.
- Q: The fediverse? → A: Nothing changes in this feature. A fediverse viewer is never a member, so for them Members behaves like Private. The `discoverable`/`indexable` actor flags for non-public profiles are proposed to Megáfono separately, and a later feature adds them once the contract is agreed. A rule for future features: watch history federates only for Public profiles.
- Q: The live database? → A: `profile_settings.visibility` has a CHECK allowing only `public` and `private`, and the table exists in production with real users. This is the first change to an existing table since the clean bootstrap (014). It's done by a guarded, idempotent statement in `initializeSchema`: replace the CHECK only when its definition lacks `members`, in one transaction, so every later start does nothing. New databases get the new CHECK from the `CREATE TABLE`. The 014 "no ALTER" rule becomes "no ALTER on a fresh database; guarded, idempotent changes for databases that exist", documented in SCHEMA.md and CLAUDE.md.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Members by default (Priority: P1)

On a default instance, a user who never chose has a Members profile: signed-in users see it; a signed-out visitor is sent to sign in and then lands on it.

**Independent Test**: with no setting, a user's dashboard opens for another signed-in user, redirects a signed-out visitor to `/signin?returnUrl=…`, and the private case still answers like an unknown username.

**Acceptance Scenarios**:

1. **Given** the default setting and a user who never chose, **When** another signed-in user (an operator or not) opens any `/u/` route, **Then** 200.
2. **Given** the same, **When** a signed-out visitor opens any `/u/` route, **Then** a redirect to `/signin?returnUrl=<that path>` (local paths only, as today), and after signing in they land on the page.
3. **Given** a Private profile, **When** anyone but its owner or a link holder asks, **Then** the unknown-username 404 (unchanged).
4. **Given** a Members profile and a valid share link, **When** a signed-out browser opens the link, **Then** it sees the profile (grants work at every level).
5. **Given** `Frunchy:Profiles:DefaultVisibility` = `members`, `private`, `public`, blank or missing, **When** Frunchy starts, **Then** it takes that value (blank or missing means `members`). Any other value stops startup naming the key.

---

### User Story 2 - Choosing among three (Priority: P1)

`/account#visibility` offers Public, Members and Private, in that order, each with one line of help. It shows the effective level and whether it comes from the user's choice or the site's default.

**Acceptance Scenarios**:

1. **Given** the owner, **When** they choose Members and save, **Then** it's stored, `profile_visibility_changed` is recorded, and `data-visibility="members"`.
2. **Given** a stored choice, **When** the default changes, **Then** the choice holds.
3. **Given** a value other than the three, **When** posted, **Then** nothing changes (`visibility-invalid`).

---

### User Story 3 - Pages say who can see them (Priority: P2)

**Acceptance Scenarios**:

1. **Given** the owner on their own Members profile, **When** it renders, **Then** a note (`data-profile-members`) says that people signed in to Frunchy can see it, with a link to `/account#visibility`. The copy-link block stays, since members can open the address.
2. **Given** the home page, **When** it renders, **Then** its note about profiles matches the instance default (three texts, English and Spanish).
3. **Given** `user-list`, **When** run, **Then** the visibility column shows `members` too (with `*` when it comes from the default).
4. **Given** an episode comment by a Members user, **When** a signed-in user reads it, **Then** the author's name links to the profile. For a signed-out visitor it's plain text.

### Edge Cases

- A database created before this feature, with the old CHECK: the first start replaces the CHECK; every later start leaves it alone. Rows keep their values.
- Two copies starting at once on the old database: the change runs in one transaction, under the advisory lock that `initializeSchema` already holds (or one it takes), so only one copy applies it.
- `returnUrl` on the redirect is the requested path and query, local only (`WebAuth.localReturnUrl`).

## Requirements *(mandatory)*

- **FR-001**: Visibility MUST be `public`, `members` or `private`. `canView` is true for Public; for Members, for any signed-in viewer; for the owner; and for a grant matching the current link.
- **FR-002**: The default MUST be `members` when the setting is missing or blank; `members` is accepted as a value.
- **FR-003**: A signed-out request to a Members profile route MUST redirect to `/signin?returnUrl=`. A Private profile MUST keep the unknown-username 404.
- **FR-004**: The account section MUST offer the three levels, and the POST MUST accept exactly those three values.
- **FR-005**: The schema MUST accept `members` on new and existing databases, through a guarded, idempotent change, documented in SCHEMA.md and CLAUDE.md.
- **FR-006**: The home note, the owner's note, `user-list` and every text MUST cover the three levels, in English and Spanish.
- **FR-007**: No change to charts, series or episode pages, the API, the extension or the fediverse.

## Success Criteria *(mandatory)*

- **SC-001**: Friends with accounts see each other's profiles without share links, by default.
- **SC-002**: Signed-out visitors still can't read any history unless a profile is Public or shared.
- **SC-003**: The live database upgrades on deploy with no manual step, and its data is unchanged.
