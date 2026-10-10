# Contract: the anime page (`GET /u/{username}/anime/{seriesId}`)

A public, server-rendered HTML page with the same visibility as the dashboard. The username is
matched case-insensitively, as on the dashboard. The series id is matched exactly, case included.
The language comes from `Accept-Language` (English default, Spanish).

Verbatim from the spec:
- Route: `/u/{username}/anime/{seriesId}`
- Crunchyroll link: `https://www.crunchyroll.com/series/{id}/{slug}`

## Responses

| Case | Status | Body |
|---|---|---|
| Known user, series the user watched (at least one episode of known length) | 200 | the anime page |
| Unknown or deleted user | 404 | the site's "User not found" message page |
| `{seriesId}` outside `^[A-Z0-9]{6,20}$` | 404 | the site's "Page not found" message page, without touching the database |
| Known user, series the user hasn't watched (no episode of known length) | 404 | the site's "Page not found" message page |

Notes:
- There's no JSON variant, and nothing is added to `/api/*`.
- A series with no fully watched episode still answers 200, with 0 fully watched and its partial
  episodes listed.

## Sections, in order, and their test hooks

Tests read only `data-*` attributes and decoded text (`readHtml`), never classes. Values in
`data-value` and the other numeric attributes are invariant: dot decimals, no grouping, hours with
1 decimal, counts and percentages as whole numbers.

| Section | Root hook | Inner hooks |
|---|---|---|
| Header | `data-anime` with `data-value` = the series id | `data-anime-title` (on the h1), `data-dashboard-link` (a link to `/u/{username}`), `data-crunchyroll-link` (a link to `https://www.crunchyroll.com/series/{id}/{slug}`, new tab, only when a slug is stored) |
| Rewatch note | `data-rewatch-note` | plain text saying only the latest watch of each episode is kept, so rewatches aren't counted |
| Totals | `data-anime-totals` | `data-stat="episodes"` and `data-stat="hours"`, each with `data-value`; `data-first-watch` and `data-latest-watch` on the containers of the two time elements |
| Seasons | one `data-season` per watched season, in display order | `data-watched`, `data-known`, `data-audio`, `data-dub`, then the MyAnimeList hooks and the episodes below |
| MyAnimeList (inside a season) | `data-mal` with `data-value` = the MyAnimeList id | `data-mal-score` (only when it has a score), `data-mal-link`; an unmatched season carries `data-mal-missing` instead of `data-mal` |
| Episodes (inside a season) | one `data-episode` per watched episode, in order | `data-fully`, `data-percent`, and a `<time data-fr-time>` |

**Seasons**: the value of `data-season` is the Crunchyroll season id, or `other` for the episodes
without a season, which come last. Only seasons with at least one watched episode are listed, in
Crunchyroll's order (season number, then id).
- `data-watched`: the season's fully watched episodes.
- `data-known`: the episodes Frunchy knows for the season (with a known length), from any user's
  history. It can be larger than what the user watched, and those episodes aren't listed.
- `data-audio`: the stored locale code, for example `ja-JP`. It is absent on `other`.
- `data-dub`: present only on dub seasons.

**Episodes**: the value of `data-episode` is the Crunchyroll episode id. They are ordered by
episode number, numberless ones last by title.
- `data-fully`: `true` or `false`.
- `data-percent`: an integer from 0 to 100, and 100 when fully watched.

**Totals**: `episodes` and `hours` equal the dashboard's rules restricted to the series (not
deduplicated), so they match a hand count over the stored watches (SC-002).

**MyAnimeList**: a matched season shows its title, format, year and season, score (when it has one),
episode count (when known), airing status, up to 3 genres, the first studio and a link, with no empty
fields. An unmatched season shows a plain "not on MyAnimeList yet" line. Format, genre and studio
names are MyAnimeList's and aren't translated.

## Links added to other pages

Every series title link carries `data-series-link` with `data-value` set to the series id, and leads
to `/u/{username}/anime/{seriesId}`:
- The dashboard (`/u/{username}`): each recent card's title and the header's newest episode title.
- The DNA page (`/u/{username}/dna`): the insignia anime and each genre's top anime.
- The year page (`/u/{username}/year/{year}`): the most watched series and each genre's top anime.

A genre without a top series shows no "Top:" line and no link. The extension does not link to the
page in this feature.

## Behaviour guarantees

- **No external calls**: the page reads only stored data, so it never calls MyAnimeList. It adds no
  table and no stored data.
- **Stable figures**: they're identical after `user-rebuild` and after re-sending submissions.
- **Stable order**: seasons by number then id, episodes by number then title then id.
- **Text**: every visible string comes from `Views/Texts.fs` in English and Spanish. Audio languages
  are named in the viewer's language.
- **Design**: it uses the shared design system and components, works from 360px wide in both
  languages, and has no rating of the user's own, ranking or recommendation (FR-013, FR-014).
