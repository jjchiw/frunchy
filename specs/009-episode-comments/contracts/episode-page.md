# Contract: the episode page and comments

## Routes

| Method | Route | Form fields | Result |
|---|---|---|---|
| GET | `/anime/{seriesId}/episode/{episodeId}` | none | the page (200), or the plain 404 page |
| POST | `/anime/{seriesId}/episode/{episodeId}/comments` | `id`, `content`, `spoiler` (`on` when ticked) | 302 to `/anime/{s}/episode/{e}#comment-{id}`; 422 with the page, the error and the typed text when invalid |
| GET | `/comments/{id}/edit` | none | the edit page (author only) |
| POST | `/comments/{id}/edit` | `content`, `spoiler` | 302 to the comment's episode page `#comment-{id}`; 422 with the edit page when invalid |
| POST | `/comments/{id}/delete` | none | 302 to the comment's episode page |
| POST | `/comments/{id}/hide` | none | 302 to the comment's episode page `#comment-{id}` |
| POST | `/comments/{id}/unhide` | none | same |

- **Ids**: `{seriesId}` and `{episodeId}` match `^[A-Z0-9]{6,20}$`, otherwise 404 without a query.
  `{id}` and the form's `id` match `^[A-Za-z0-9_-]{1,64}$`, otherwise 404 (route) or 422 (form).
- **404** is the site's "Page not found" page, with nothing changed. It covers:
  - an unknown or unresolved episode, or one of another series;
  - an unknown or deleted comment;
  - an edit or delete from anyone but the author, signed out included;
  - a hide or unhide from anyone who isn't an operator, decided before the form is read.
- **Create while signed out**: 302 to `/signin?returnUrl=<the episode page>`; nothing is stored.
- **Double submit**: a create with an `id` that already exists for the same author and episode
  redirects to that comment and stores nothing new. One that exists for someone else re-renders the
  page (422) and stores nothing.
- **Forgery**: every POST carries the antiforgery token (`WebAuth.readValidatedForm`).
- **Rate limits**:
  - create, edit and delete share `comment-write`, 10 a minute per IP (429 page);
  - hide and unhide use `review-write`, which counts operators only.

## Validation errors (422)

| Key | When |
|---|---|
| `empty` | nothing left after trimming |
| `too-long` | more than 2,000 characters (Unicode scalar values) |
| `bad-id` | the form's `id` is missing, malformed or taken by someone else |

## Test hooks

| Element | Hook |
|---|---|
| Page root | `data-episode-page` = episode id, with `data-series` = series id |
| Header pieces | `data-episode-number` (number or empty), `data-episode-title`, `data-minutes`, `data-audio`, `data-dub` (present on dubs) |
| Crunchyroll links | `data-cr-episode-link`, `data-cr-series-link` |
| MyAnimeList block | `data-mal` = MyAnimeList id, `data-mal-link` |
| Thread | `data-thread` = number of visible comments (hidden ones count, deleted ones don't) |
| A comment | `data-comment` = id, `data-author` = username, `data-episode` = the episode it was written on, `data-created` (ISO) |
| Comment states | `data-spoiler` on spoilers (the `<details>`), `data-edited` when edited, `data-hidden` when hidden |
| Spoiler warning | `data-spoiler-summary` |
| Placeholder | `data-hidden-placeholder` ("Hidden by an operator") |
| Controls | `data-comment-edit`, `data-comment-delete`, `data-comment-hide`, `data-comment-unhide` |
| Comment form | `data-comment-form`, its checkbox `data-spoiler-toggle` (checked by default) |
| Form error | `data-comment-error` = key |
| Sign-in prompt (signed out) | `data-signin-prompt`, href `/signin?returnUrl=…` |
| Empty thread | `data-thread-empty` |
| Operator note | `data-moderation-note` (operators only: hiding hides on Frunchy) |
| Anime page episode link | `data-episode-link` = episode id, href `/anime/{s}/episode/{e}` |
| Edit page | `data-comment-edit-page` = id |

## Visibility (research R12)

- **Deleted** comments and those with `audience` `followers` or `direct` are never shown.
- **Hidden** comments show the placeholder to everyone but operators.
- **Spoilers** are closed on load for everyone.
