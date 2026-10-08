# Contract: Dashboard additions (`GET /u/{username}`)

Base: the feature 001 dashboard. Nothing existing changes: the four figures, the recently-watched
list and its order, the timestamps, the empty states and the navbar stay as they are (FR-018,
SC-009). All data comes from tables Frunchy holds. The page never calls the service (FR-011).

## Recently-watched card (`data-recent-item`)

When the episode's season is `matched`:

| Element | Hook | Content |
|---|---|---|
| Score | `data-mal-score` | `★ 9.26`, MyAnimeList's score with 2 decimals; **omitted** when the anime has no score |
| Genres | `data-mal-genres` | up to 3 genre names in MyAnimeList's order, joined with ` / ` (e.g. `Adventure / Drama / Fantasy`) |
| Link | `data-mal-link` | the MyAnimeList page (`mal_anime.url`), `target=_blank rel=noopener`, with the localized label "MyAnimeList" |

When the season isn't matched (no row, `pending`, `failed`, `ambiguous` or `no_match`), none of
these elements exist, and the card is byte-for-byte what feature 001 renders for the same data.

## "Top genres" section (`data-top-genres`)

Shown only when at least one of the user's fully watched episodes is in a matched season (FR-018a),
after the figures and before the recently-watched list.

- **Heading**: localized "Top genres" / "Géneros principales".
- **Up to 5 rows**, `data-genre="{name}"`, ordered by count desc, then name asc. Each row has:
  - the genre name;
  - `data-genre-episodes` = the count, rendered as e.g. "561 episodes" / "561 episodios";
  - `data-genre-share` = round(100 × count / matchedEpisodes), rendered as `42%`;
  - a bar whose width is that share.
- **Coverage line** `data-genres-coverage`: localized "Based on {p}% of fully watched episodes. An
  episode counts in each of its genres." / "Basado en el {p}% de los episodios vistos completos. Un
  episodio cuenta en cada uno de sus géneros." Here p = round(100 × matchedEpisodes /
  allFullyWatchedEpisodes).

**Definitions** (the "Episodes watched" rule, clarify Q3):
- `allFullyWatchedEpisodes`: the user's `watch_events` with `fully_watched = true` whose episode has
  a known `duration_ms`. This is exactly the existing "Episodes watched" figure.
- `matchedEpisodes`: those of them whose `cr_episodes.season_id` has a `matched` row.
- `count(genre)`: those of the matched episodes whose anime has that genre (`kind = genre` only; not
  themes, demographics or explicit genres).

## Texts (`Views/Texts.fs`, en and es)

`TopGenresTitle`, `TopGenresEpisodes n`, `TopGenresCoverage p`, `MyAnimeListLink`, `ScoreLabel`
(the `aria-label` for the score, e.g. "MyAnimeList score" / "Puntuación en MyAnimeList").
