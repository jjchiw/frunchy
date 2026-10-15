module FrunchyBackend.Tests.EpisodePageTests

open System
open System.Net
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Views
open FrunchyBackend.Tests.AuthHelpers
open FrunchyBackend.Tests.EpisodeHelpers
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

// Feature 009, User Story 1: the episode page (contracts/episode-page.md). The 2026 sample with the feature 003
// MyAnimeList fixtures; every expectation is a query over the catalog, independent of the slice's SQL.

/// Every header hook, both Crunchyroll links and the MyAnimeList block of a page against the catalog's facts
let private assertHeader (app: TestApp) (language: string) (ep: EpisodeRef) =
    task {
        let! status, html = episodePage app language ep.Series ep.Episode
        Assert.Equal(HttpStatusCode.OK, status)
        let! facts = episodeFacts app.Db ep

        Assert.Equal(ep.Episode, hookValue "episode-page" html)
        Assert.Equal(ep.Series, hookValue "series" html)
        Assert.Equal(facts.Number, hookValue "episode-number" html)
        Assert.Equal(facts.Title, hookValue "episode-title" html)
        Assert.Equal(string (int (Math.Round(float facts.DurationMs / 60000.0))), hookValue "minutes" html)

        if facts.Audio = "" then
            Assert.False(hasHook "audio" html)
        else
            Assert.Equal(facts.Audio, hookValue "audio" html)

        Assert.Equal(facts.Dub, hasHook "dub" html)

        Assert.Equal(Some $"https://www.crunchyroll.com/watch/{ep.Episode}", hrefOfHook "cr-episode-link" html)

        if facts.Slug = "" then
            Assert.False(hasHook "cr-series-link" html)
        else
            Assert.Equal(Some $"https://www.crunchyroll.com/series/{ep.Series}/{facts.Slug}", hrefOfHook "cr-series-link" html)

        for link in Regex.Matches(html, "<a\\b[^>]*data-cr-[a-z]+-link[^>]*>") do
            Assert.Contains("target=\"_blank\"", link.Value)
            Assert.Contains("rel=\"noopener\"", link.Value)

        if facts.MalId = "" then
            Assert.False(hasHook "mal" html)
            Assert.False(hasHook "mal-link" html)
        else
            Assert.Equal(facts.MalId, hookValue "mal" html)
            Assert.Equal(Some facts.MalUrl, hrefOfHook "mal-link" html)

        // the language of the page
        let t = if language = "es" then Texts.es else Texts.en
        Assert.Contains($"<html lang=\"{t.Lang}\"", html)
        Assert.Contains(t.EpisodeWatchOnCrunchyroll, html)
        Assert.Contains(t.CommentsHeading, html)
        return facts
    }

let private pickAll (db: FrunchyBackend.Infrastructure.Database.DbConfig) =
    task {
        let! matched = pickEpisode db "m.mal_id IS NOT NULL AND coalesce(m.method, '') <> 'dub' AND e.episode_number IS NOT NULL AND e.title IS NOT NULL"
        let! unmatched = pickEpisode db "e.season_id IS NOT NULL AND m.mal_id IS NULL AND e.episode_number IS NOT NULL"
        // the series of GRK5CNJ14 holds a season and its dub
        let! dub = pickEpisode db "m.method = 'dub' AND e.series_id = (SELECT series_id FROM cr_seasons WHERE id = 'GRK5CNJ14')"
        let! special = pickEpisode db "e.episode_number IS NULL"
        return matched, unmatched, dub, special
    }

[<Fact>]
let ``the header of a matched, an unmatched, a dub and a numberless episode equals the catalog, in en and es`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! matched, unmatched, dub, special = pickAll app.Db

        for language in [ "en"; "es" ] do
            let! m = assertHeader app language matched
            Assert.NotEqual<string>("", m.MalId)
            Assert.False m.Dub

            let! u = assertHeader app language unmatched
            Assert.Equal("", u.MalId)

            let! d = assertHeader app language dub
            Assert.True(d.Dub, "GRK5CNJ14 is a dub")
            // a dub shows its original's MyAnimeList entry
            Assert.NotEqual<string>("", d.MalId)

            let! s = assertHeader app language special
            Assert.Equal("", s.Number)
    }

[<Fact>]
let ``the Crunchyroll series link appears only when the slug is known`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! known = pickEpisode app.Db "s.slug IS NOT NULL"
        let! _ = assertHeader app "en" known
        let! html = rawEpisodePage app known
        Assert.Contains("data-cr-series-link", html)

        // forget the slug: the series link goes, the episode link stays
        do! Db.execute app.Db $"UPDATE cr_series SET slug = NULL WHERE id = '{known.Series}'"
        let! _ = assertHeader app "en" known
        let! html = rawEpisodePage app known
        Assert.DoesNotContain("data-cr-series-link", html)
        Assert.Contains("data-cr-episode-link", html)
    }

[<Fact>]
let ``an empty thread is announced`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL"
        let! _, html = episodePage app "en" ep.Series ep.Episode
        Assert.Equal("0", hookValue "thread" html)
        Assert.True(hasHook "thread-empty" html)
        Assert.Empty(comments html)
    }

[<Fact>]
let ``malformed ids, an unknown episode, an unresolved stub and another series' episode are not found`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! ep = pickEpisode app.Db "e.episode_number IS NOT NULL"
        do! Db.execute app.Db "INSERT INTO cr_episodes (id, series_id) VALUES ('STUBEP0001', 'STUBSER001')"
        let! other = pickEpisode app.Db $"e.series_id <> '{ep.Series}'"

        let notFound (series: string) (episode: string) =
            task {
                let! status, html = episodePage app "en" series episode
                Assert.Equal(HttpStatusCode.NotFound, status)
                Assert.Contains(Texts.en.NotFoundTitle, html)
                Assert.False(hasHook "episode-page" html)
            }

        // lowercase, short, long and out-of-alphabet ids: refused before any query
        for bad in [ ep.Episode.ToLowerInvariant(); "ABCDE"; String('A', 21); "GR-K5CNJ14"; "GRK5CNJ14%20" ] do
            do! notFound ep.Series bad
            do! notFound bad ep.Episode

        // well-formed but unknown, an unresolved stub, and an episode of another series
        do! notFound ep.Series "ZZZZZZZZZ"
        do! notFound "STUBSER001" "STUBEP0001"
        do! notFound ep.Series "STUBEP0001"
        do! notFound ep.Series other.Episode
        do! notFound other.Series ep.Episode

        // and the page itself is there for the right pair
        let! status, _ = episodePage app "en" ep.Series ep.Episode
        Assert.Equal(HttpStatusCode.OK, status)
    }

[<Fact>]
let ``anyone sees the same header, signed out or signed in`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! matched, _, dub, _ = pickAll app.Db
        use browser = signedUp app "sub-1" "watcher" |> fun t -> t.Result

        let headerOf (html: string) =
            [ for m in Regex.Matches(html, "data-(episode-page|series|episode-number|episode-title|minutes|audio|dub|cr-episode-link|cr-series-link|mal|mal-link|mal-score)(=\"[^\"]*\")?") -> m.Value ]
            @ [ for m in Regex.Matches(html, "href=\"https?://[^\"]*\"") -> m.Value ]

        for ep in [ matched; dub ] do
            let! _, anonymous = episodePage app "en" ep.Series ep.Episode
            let! signedIn = browser.Get(episodeUrl ep.Series ep.Episode)
            Assert.Equal(HttpStatusCode.OK, signedIn.Status)
            Assert.NotEmpty(headerOf anonymous)
            Assert.Equal<string list>(headerOf anonymous, headerOf signedIn.Html)
    }

[<Fact>]
let ``every episode of the anime page links to an episode page that opens`` () =
    task {
        let! app, _ = sampleApp ()
        use app = app
        let! series = Db.scalar<string> app.Db "SELECT series_id FROM cr_seasons WHERE id = 'G6MGC3778'" []
        let! response = FrunchyBackend.Tests.Fixtures.getWithLanguage app.Client "en" $"/u/animenerd/anime/{series}"
        let! html = FrunchyBackend.Tests.Fixtures.readHtml response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)

        let rows = [ for m in Regex.Matches(html, "data-episode=\"([^\"]+)\"") -> m.Groups[1].Value ]
        Assert.NotEmpty rows

        // one link per row, in the same order, to that episode's page
        let links =
            [ for m in Regex.Matches(html, "<a\\b[^>]*\\bdata-episode-link=\"([^\"]+)\"[^>]*>") ->
                  m.Groups[1].Value, Regex.Match(m.Value, "href=\"([^\"]*)\"").Groups[1].Value ]

        Assert.Equal<string list>(rows, links |> List.map fst)

        for id, href in links do
            Assert.Equal(episodeUrl series id, href)

        // and the pages open
        for id, href in links |> List.truncate 12 do
            let! page = FrunchyBackend.Tests.Fixtures.getWithLanguage app.Client "en" href
            Assert.True(page.StatusCode = HttpStatusCode.OK, $"{href} answered {int page.StatusCode}")
            let! body = FrunchyBackend.Tests.Fixtures.readHtml page
            Assert.Equal(id, hookValue "episode-page" body)
    }
