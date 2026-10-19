# Contract: profile visibility

## Rule

A viewer can see user U's profile (every route under `/u/{U}`) when any of these holds:
1. U's effective visibility is `public`: their choice, or no choice and `Frunchy:Profiles:DefaultVisibility=public`;
2. the viewer is signed in as U;
3. the viewer's `frunchy.shares` cookie holds a grant `{u: U.id, l: U.share_link_id}` matching U's current link.

Otherwise the route answers exactly what an unknown username gets: 404 with the "user not found" page.

## Routes

| Route | Who | Answers |
|---|---|---|
| `GET /s/{token}` | anyone (`share` limiter, 30/min/IP) | valid: 303 to `/u/{username}` + grant cookie; otherwise 404 page, no cookie |
| `POST /account/visibility` | owner, antiforgery, `account-write` | `visibility=public\|private` → `/account?msg=visibility-saved#visibility`; other → `?msg=visibility-invalid#visibility` |
| `POST /account/share-link` | owner, antiforgery, `account-write` | renders the show-once page with `data-share-link` (200) |
| `POST /account/share-link/revoke` | owner, antiforgery, `account-write` | `/account?msg=share-link-revoked#visibility` |

## Hooks (tests read only these)

| Hook | Where | Meaning |
|---|---|---|
| `data-visibility="public\|private"` | `/account#visibility` | effective state |
| `data-visibility-source="choice\|default"` | same | where it comes from |
| `data-share-link-state="none\|active"` | same | link exists |
| `data-share-link` | show-once page | the full link, text and copy button |
| `data-profile-private` | owner's own `/u/` pages | private-page note |
| `data-comment-author` | episode page | author name; has `href` only when the viewer can see that profile |

## Setting

| Key | Default | Rule |
|---|---|---|
| `Frunchy:Profiles:DefaultVisibility` | `private` | `private` or `public` (case-insensitive, trimmed); anything else stops startup naming the key |
