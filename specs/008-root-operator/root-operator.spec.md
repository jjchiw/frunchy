# Feature Specification: Operators Managed by a Root Operator

**Feature Branch**: `008-root-operator`

**Created**: 2026-10-10

**Status**: Draft

**Input**: "Replace the list of operator usernames in configuration with one root operator in configuration, who adds and removes other operators from the website; operators are stored in the database."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The root operator manages operators (Priority: P1)

The site's owner is named once in configuration as the root operator. Signed in, they open an operators page and see:
- every current operator, with their username, display name, who added them, and when;
- a way to add someone by username;
- a way to remove anyone listed.

Adding or removing someone needs a recent sign-in, like creating a sync key does. Each change shows up in the security activity of both the root and the person affected. Operators review MyAnimeList matches (feature 007) but can't manage operators. The root can never be removed, or added to the list, from the website: configuration is always the way back in.

**Why this priority**: it is the feature. Handing review work to a trusted second person must not need a configuration change and a restart.

**Independent Test**: with a root configured and two other users, sign in as root, add one user, check that they can open the review page, remove them, and check that they can't any more. Both accounts' security activity shows each change.

**Acceptance Scenarios**:

1. **Given** the root signed in recently, **When** they add an existing user by username, **Then** that user becomes an operator at once, appears in the list with "added by" and the time, and both accounts record the change.
2. **Given** an operator in the list, **When** the root removes them, **Then** they lose access at once and both accounts record the change.
3. **Given** the root's last sign-in is older than 10 minutes, **When** they add or remove someone, **Then** they are sent to confirm it's them first, and the change happens only after.
4. **Given** the root, **When** they try to add their own username, a username that doesn't exist, or someone already an operator, **Then** nothing changes and a notice says why.
5. **Given** an operator who isn't root, **When** they open the operators page or post to it, **Then** they get "page not found", while the review page still works for them.

### User Story 2 - Operators know what they are (Priority: P2)

On the account page, an operator reads "You're an operator", with the link to the review page. The root also gets a link to the operators page.

**Why this priority**: people should know what they can do, and the root needs a way in. It's small and follows the first story.

**Independent Test**: view the account page as root, as an operator and as a regular user, and check which line and links each sees.

**Acceptance Scenarios**:

1. **Given** an operator, **When** they open their account page, **Then** it says "You're an operator" and links to the review page.
2. **Given** the root, **When** they open their account page, **Then** it also links to the operators page.
3. **Given** a regular user, **When** they open their account page, **Then** neither line nor link appears.

### User Story 3 - A way back in from the server (Priority: P2)

From a terminal on the server, `operator-add <username>`, `operator-remove <username>` and `operator-list` do what the page does, running the same code.

**Why this priority**: if the website is unreachable or the root's sign-in breaks, the server still manages operators.

**Independent Test**: run each command, then check the page shows the same result, and that the commands refuse what the page refuses.

**Acceptance Scenarios**:

1. **Given** a terminal, **When** `operator-add <username>` runs, **Then** the user becomes an operator exactly as from the page, and their account records it as done by the operator from the server.
2. **Given** a terminal, **When** `operator-list` runs, **Then** it prints the root (from configuration) and every operator with who added them and when.

### Edge Cases

- **No root configured**: nobody is root and the operators page is "not found" for everyone. Operators already in the table keep reviewing.
- **Root changed in configuration**: the new root takes over at once. If the old root was never in the table, they stop being an operator.
- **Root's username also in the table**: the root is still root. The list shows the root separately and can't remove it.
- **Deleted user**: their operator row goes with the account. If they had added others, those keep their status, and "added by" reads as a deleted account.
- **Usernames in any case**: they are matched case-insensitively, as everywhere else.
- **Re-adding a removed operator**: works like any other addition.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The root operator MUST be the single username in the `Frunchy:RootOperator` setting. An empty setting means no root. The old `Frunchy:Operators` setting MUST no longer grant anything.
- **FR-002**: An operator MUST be the root, or a user listed as an operator in the database. The check MUST NOT add a query to a request: it joins the user lookup each signed-in request already does.
- **FR-003**: Operators MUST keep everything feature 007 gives them: the review pages and actions.
- **FR-004**: The site MUST serve a root-only operators page at `/review/operators`, listing each operator's username, display name, who added them, and when, plus the root shown separately.
- **FR-005**: The root MUST be able to add an operator by username, and remove one.
- **FR-006**: Adding or removing MUST require a sign-in within the last 10 minutes, using the existing re-confirmation flow.
- **FR-007**: The operators page and its actions MUST answer "page not found" (404) to everyone but the root, before any form is read. They are never rate-limited for anyone but the root. The root's actions pass the existing forgery protection and a rate limit.
- **FR-008**: Adding the root, an unknown username or an existing operator, or removing someone who isn't an operator, MUST change nothing and show a notice saying why (post/redirect/get).
- **FR-009**: Every addition and removal MUST be recorded in the security activity of both the root (or the server, for the terminal) and the affected user.
- **FR-010**: The account page MUST tell operators "You're an operator" with the review link. It MUST show the root a link to the operators page too.
- **FR-011**: `operator-add`, `operator-remove` and `operator-list` MUST run the same code as the page, with the same refusals.
- **FR-012**: Deleting a user MUST remove their operator status.
- **FR-013**: Every visible text MUST come from the site's English and Spanish texts. The page MUST use the shared design system and work from 360px wide.
- **FR-014**: The extension's API contract MUST NOT change. The docs MUST describe `Frunchy:RootOperator`, replacing `Frunchy:Operators`.

### Key Entities

- **Root operator**: the user named in the root setting. Only configuration changes it.
- **Operator**: a user granted review rights by the root, with who granted it and when. It disappears with the user's account.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The root adds or removes an operator in at most 2 actions from the account page, and the change takes effect on the operator's next request.
- **SC-002**: 100% of the operators page's routes answer "page not found" to signed-out visitors, regular users and operators, and change nothing for them.
- **SC-003**: Page and terminal leave identical operator records for the same change.
- **SC-004**: Every change appears in both accounts' security activity.
- **SC-005**: Signed-in page loads make the same number of database queries as before this feature.

## Assumptions

- One root, as the owner asked, and set only in configuration. Losing the root's sign-in is recovered from the terminal or by changing the setting.
- Operators can't add other operators. Only the root can.
- No "operators" are migrated: today only the owner is configured, and they become the root.
- The security events reuse the existing activity log with new kinds: operator added, operator removed.

## Verbatim Constraints

- Setting: `Frunchy:RootOperator` (replaces `Frunchy:Operators`)
- Route: `/review/operators`
- Commands: `operator-add <username>`, `operator-remove <username>`, `operator-list`
- Table: `operators`
