# Feature Specification: Language switch

**Feature Branch**: `024-language`

**Created**: 2026-10-22

**Status**: Draft

**Input**: Frunchy speaks English and Spanish, but only the browser's Accept-Language picks between them. A person
whose browser says one thing and who wants the other has no way to choose.

## Clarifications

### Session 2026-10-22

- Q: How many languages? → A: Two, English (`en`) and Spanish (`es`), as today.
- Q: Where is the choice kept? → A: A preference cookie `frunchy.lang`, value `en` or `es`, one year, `Path=/`,
  `SameSite=Lax`, `HttpOnly`, `Secure` when the request is https. It is **not** stored on the account (a later
  feature can add that); signed in or out behaves the same.
- Q: How is it set? → A: A plain link, `GET /language/{en|es}?returnUrl=<local path and query>`. It sets the cookie
  and redirects 303 to `returnUrl`, checked with `WebAuth.localReturnUrl` (fallback `/`; `https://evil.example` and
  `//evil.example` go to `/`). No rate limit and no antiforgery: it only sets a preference cookie. An unknown
  language is the plain 404 and sets no cookie.
- Q: Who decides the language of a request? → A: A valid `frunchy.lang` first, then `Accept-Language` as today, then
  English. Any other cookie value is ignored. `<html lang>` follows.

## Design

The frunchy_designer session's direction. It is binding.

- **Footer only**; nothing in the top navigation. The footer row is `flex flex-wrap items-center gap-3`: the mark,
  the two text lines, then the switch, pushed right with `sm:ml-auto`. At 360px it wraps onto its own line under the
  two lines, with `-ml-3.5 sm:ml-0` so its text lines up with them (the home page's "How to sync" trick).
- **One link naming the other language**, in that language's own name: an English page shows "Español", a
  Spanish page shows "English". Never flags. It is not a two-option control, and has no selected state or
  `aria-current`.
- A small ghost button (`btnSm Ghost`) with a leading Material Symbols `language` (globe) icon, then the name.
  Hover and focus are the ghost button's own plus the global focus ring.
- Identical signed in and out. No notice after switching (the reload is the confirmation), no cookie banner,
  nothing on `/account`.

## Requirements *(mandatory)*

- **FR-001**: `GET /language/{code}` for `en` and `es` sets `frunchy.lang` with the attributes above and answers
  303 to a local `returnUrl` (else `/`). Any other code answers the plain 404 and sets nothing.
- **FR-002**: Request localization reads `frunchy.lang` (only the exact values `en`/`es`) before
  `Accept-Language`; an invalid value is ignored.
- **FR-003**: Every page's footer has `<nav aria-label="{Language|Idioma}" data-language-switch>` with one link:
  `href="/language/{target}?returnUrl={this page's path and query, URL-encoded}"`, `hreflang`, `lang` and
  `data-language` all the TARGET language, text the target language's own name.
- **FR-004**: All copy is in `Views/Texts.fs` (`LanguageSwitchLabel` in the page's language; `LanguageNameEn` and
  `LanguageNameEs` identical in both text sets). `Language` is added to `Theme.Icon` (the icon subset grows by one).
- **FR-005**: No change to the account, to data or to the API. No shared design token changes.

## Success Criteria *(mandatory)*

- **SC-001**: From any page, one click shows the same page in the other language, and it stays that way on later
  pages and visits (one year), whatever the browser's Accept-Language says.
- **SC-002**: Without the cookie, behaviour is exactly as before.
