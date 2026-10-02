# Contract: Public dashboard and pages (HTML)

**Consumers**: anyone with a browser, and integration tests that assert on rendered HTML.

## Routes

| Route | Page | Notes |
|---|---|---|
| `GET /` | Home | localized; explains Frunchy and how to sync with the extension. Lists no users. |
| `GET /u/{username}` | Public dashboard | case-insensitive username; read-only; no login |
| unknown `/u/{username}` | "User not found" | HTTP **404**, localized, contains no other user's data |
| `GET /dashboard`, `GET /profile` | removed | placeholder pages with invented data (research R16); return 404 |

## Language

- The response language comes from the `Accept-Language` request header: Spanish if `es` is the
  best supported match, otherwise English. Unsupported languages (e.g. `fr`) get English.
- `<html lang="en">` or `<html lang="es">` matches the response language.
- All interface text comes from `Views/Texts.fs`. Anime and episode titles are shown as
  Crunchyroll sent them.

## Dashboard content (`/u/{username}`)

Sections, top to bottom (visual style per `design/dashboard_frunchy`, simplified):

1. **Header**: the display name, a note that the profile is public (FR-011c), and the last sync
   time as a `<time>` element (see below). When an import is pending or processing, a "sync in
   progress" notice (FR-014).
2. **Stats**: four figures with stable test hooks:
   - `data-stat="series"`: integer;
   - `data-stat="episodes"`: integer;
   - `data-stat="hours"`: one decimal, culture-formatted (`39.3` in English, `39,3` in Spanish);
   - `data-stat="days"`: one decimal.
3. **Recent episodes**: up to 6 items, newest first, each `data-recent-item` with:
   - thumbnail `<img>` (Crunchyroll URL, `alt` = series title, `loading="lazy"`);
   - series title;
   - episode label: "Episode 13" / "Episodio 13", or "Special" / "Especial" when there's no number;
   - duration in minutes ("24 min");
   - an "In progress" / "En curso" badge when the episode wasn't fully watched;
   - the watch time as a `<time>` element.
4. **Empty state** (no resolved watch events): replaces sections 2–3. It explains how to sync
   from the extension and shows no numbers (FR-015, SC-008).

The dashboard never shows sync keys, import ids, issues or unresolved entries (FR-011b).

## Time elements

```html
<time datetime="2026-09-30T22:46:02Z" data-fr-time="relative">30 Sep 2026, 22:46 UTC</time>
```

- `datetime` is the exact UTC instant. The text content is a fallback formatted in the page
  language with an explicit `UTC`, so it's unambiguous without JavaScript (FR-013a).
- An inline script in the layout rewrites each `[data-fr-time]` using `Intl.RelativeTimeFormat` and
  `Intl.DateTimeFormat` with `document.documentElement.lang` and the viewer's time zone, e.g.
  "2 hours ago · 30 Sep 2026, 23:46" or "hace 2 horas · 30 sept 2026, 23:46".

## Not part of this contract

Period filters, weekly activity, ranks, trends, genres/DNA and the full-history page are out of
scope (spec Assumptions).
