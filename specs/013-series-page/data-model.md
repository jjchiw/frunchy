# Data Model: Public Series Page

There are no schema changes. The page reads `cr_series`, `cr_seasons`, `cr_episodes`,
`cr_season_matches`, `mal_anime`, the MyAnimeList tags, `comments` (counts only) and `watch_events`
(people counts only).

**Computed shape** (`Handlers/Series/Series.fs`):

```text
SeriesPage  = { SeriesId; Title; Slug: string option; Poster: Poster option; People: int option; PeopleWeek: int option;
                Seasons: SeasonBlock list }
SeasonBlock = { Id: string option; Title: string option; Number: int option; Audio: string option; Dub: bool;
                Mal: MalInfo option; ShownBefore: bool; Episodes: EpisodeLine list }
EpisodeLine = { Id; Number: int option; Label: string option; Title: string option; DurationMs: int; Comments: int }
```

**Usernames**: `Username.tryCreateNew` is added; `Username.tryCreate` doesn't change.
