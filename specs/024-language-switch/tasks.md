# Tasks: Language switch

**Input**: [language-switch.spec.md](language-switch.spec.md)

**Tests**: `data-*` hooks and attributes only. The suite must stay green.

- [x] T001 Request localization: `CookieRequestCultureProvider` reads the plain `en`/`es` value of `frunchy.lang`
  (anything else is ignored) and is registered before `AcceptLanguageHeaderRequestCultureProvider`
  · Infrastructure/LanguagePreference.fs, Program.fs
- [x] T002 `GET /language/{code}`: sets the cookie, 303 to `WebAuth.localReturnUrl` (fallback `/`), the plain 404 for
  an unknown language; no rate limit, no antiforgery. Registered in `Program.endpoints`
  · Handlers/Language.fs, FrunchyBackend.fsproj, Program.fs
- [x] T003 The footer link to the other language (the designer's direction): `Theme.Language` icon, texts
  `LanguageSwitchLabel`, `LanguageNameEn`, `LanguageNameEs` (en and es) · Views/Layout.fs, Views/Texts.fs,
  Views/Theme.fs
- [x] T004 Tests: the route and cookie attributes; local-only returnUrl; unknown language; cookie against
  Accept-Language both ways; invalid cookie ignored; the footer link on several pages in both languages
  · tests/FrunchyBackend.Tests/LanguageSwitchTests.fs
- [x] T005 CHANGELOG (Added) and CLAUDE.md localization line
