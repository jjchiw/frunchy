# Research: Anime Page

Prototyped on `frunchydb`, user `jjchiw`, on 2026-10-10. One Piece (`GRMG8ZQZR`): 266 fully
watched episodes, 106.4 h, 7 seasons, 89 ms.

## R1. Route and id validation

- **Decision**: `GET /u/{username}/anime/{seriesId}`. Every stored series id matches `^[A-Z0-9]+$`
  (338 of 338), so the handler accepts 6 to 20 characters from `A-Z0-9`. Anything else is not
  found, without touching the database. Unknown and deleted users get "User not found" with 404. A
  series with no watched episode of known length gets the general "Page not found" with 404.

## R2. Title, poster, Crunchyroll link

- **Title**: `coalesce(max(cr_series.title), min(episode series_title), series_id)`, the same order as
  the DNA and year pages (the stored series title first). `cr_series` is LEFT JOINed, because it can
  be missing (feature 003, T044).
- **Poster**: the `image_url` of the user's most-watched matched season, by minutes; otherwise the
  newest watched episode's thumbnail; otherwise none.
- **Link**: `https://www.crunchyroll.com/series/{id}/{slug}` when a slug is stored (338 of 338
  today), opening in a new tab with `rel="noopener"`. No slug means no link.

## R3. Progress figures

- **Decision**: fully watched episodes and hours use the dashboard's rule (not deduplicated),
  restricted to the series. First and latest watch are `min` and `max` of `watched_at` over those
  episodes, rendered with `Layout.timeElement` (absolute). A note says only the latest watch of each
  episode is kept.

## R4. Seasons and episodes

- **Season order**: `season_number NULLS LAST`, then the season id. Only seasons with a watched
  episode are shown.
- **Known episodes**: `cr_episodes` rows of that season with a known duration. They're shared
  catalog data from any user's history, which the label says ("of N known episodes").
- **Dub mark**: the season's match has `method = 'dub'`. An unmatched dub only shows its language.
  `// simplified`: no language heuristic; revisit if unmatched dubs are common.
- **No season**: watched episodes without a season are grouped last, under "Other episodes".
- **Episode order**: `episode_number NULLS LAST`, then the title, then the id. Each row shows the
  number (`EpisodeLabel`) or `SpecialLabel`, the title, the minutes, then "watched" or
  `WatchedPercent`, then `timeElement "relative"`.

## R5. MyAnimeList per season

- **Shown**: `mal_anime` title, `type` (MyAnimeList's name, untranslated as on the other pages),
  season and year (`DnaSeason`), score (when present, `ScoreLabel` for assistive tech), `episodes`
  (when > 0), status, up to 3 genres (`kind = 'genre'` by position), the first studio, and the
  MyAnimeList link.
- **Status texts**: the stored values are "Finished Airing", "Currently Airing" and "Not yet aired".
  Each maps to a localized text, and anything else is shown as stored.
- **Unmatched**: `NotOnMyAnimeList`.

## R6. Audio language names

- **Decision**: a `Texts` function maps the stored locale codes to names in the viewer's language,
  for example "Japanese" / "Japonés" and "Spanish (Latin America)" / "Español (Latinoamérica)". It
  covers the locales in the catalog (ja-JP, en-US, es-419, es-ES, zh-CN, fr-FR, pt-BR, de-DE, ru-RU,
  it-IT, ar-SA, hi-IN, ko-KR) and falls back to the code.
- **Rationale**: .NET's culture display names depend on the OS's ICU data and the process culture.
  A small explicit map is predictable in both languages.

## R7. Links from other pages

- **Decision**: a new component, `seriesLink username seriesId title`, an `a` to
  `/u/{username}/anime/{seriesId}` with the `fr-link` style and `data-series-link`.
  - **`shareLine`**: its `detail` becomes an `HtmlElement option`, so a genre's top anime can be
    "Top: <link>".
  - **`DnaTopAnime`**: becomes a plain `TopAnimePrefix` text ("Top:" / "Destacado:").
  - **Dashboard**: the recent query adds `e.series_id` as its last column, so the other column
    indices stay. The card title links, and so does the header's newest-episode title.
- **Fix**: the DNA and year pages fall back to `Top = (genreName, genreName)` when a genre has no top
  series. That would make a link to a series named after the genre. The fallback goes. "Others" gets
  an explicit flag, and a genre without a top shows no "Top:" line.
