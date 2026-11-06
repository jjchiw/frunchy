/// Feature 030 (T007): the recap Note's HTML is a pure function, tested without Postgres.
module FrunchyBackend.Tests.RecapPostHtmlTests

open System
open Xunit
open FrunchyBackend.Infrastructure
open FrunchyBackend.Workers.FediverseOutbox

let private week (episodes: int64) (minutes: int64) (series: int64) (fresh: int64) : Recap.Week =
    { Episodes = episodes
      Minutes = minutes
      Series = series
      New = fresh
      Continued = series - fresh
      Any = true }

let private most (id: string) (title: string) (episodes: int64) : Recap.MostWatched =
    { SeriesId = id; Title = title; Episodes = episodes }

let private post (w: Recap.Week) (days: int) (mostWatched: Recap.MostWatched list) : RecapPost =
    { Week = w; DaysWatched = days; MostWatched = mostWatched }

let private monday = DateOnly(2026, 11, 2)

let private link (href: string) (text: string) = $"<a href=\"{href}\">{text}</a>"
let private seriesLink (id: string) (title: string) = link $"https://frunchy.test/anime/{id}" title

[<Fact>]
let ``the figures paragraph then the most watched paragraph, the spec's example`` () =
    let html =
        recapPostHtml
            "frunchy.test"
            monday
            (post
                (week 14L 320L 4L 2L)
                5
                [ most "FRIEREN1" "Frieren" 6L; most "APOTHEC1" "The Apothecary Diaries" 4L; most "DANDADAN" "Dandadan" 2L ])

    Assert.Equal(
        "<p>My week on Frunchy (Nov 2 – Nov 8, 2026): 14 episodes, 5 h 20 min, 4 series (2 new, 2 continued). Watched on 5 of 7 days.</p>"
        + "<p>Most watched: "
        + seriesLink "FRIEREN1" "Frieren"
        + " (6), "
        + seriesLink "APOTHEC1" "The Apothecary Diaries"
        + " (4) and "
        + seriesLink "DANDADAN" "Dandadan"
        + " (2).</p>",
        html
    )

[<Fact>]
let ``singular forms: one episode, one series, one day, 1 new and 0 continued`` () =
    let html = recapPostHtml "frunchy.test" monday (post (week 1L 24L 1L 1L) 1 [ most "ONE00001" "One" 1L ])

    Assert.Contains("1 episode, 24 min, 1 series (1 new, 0 continued). Watched on 1 of 7 days.</p>", html)
    Assert.DoesNotContain("episodes", html)
    Assert.Contains("<p>Most watched: " + seriesLink "ONE00001" "One" + " (1).</p>", html)

[<Theory>]
[<InlineData(45L, "45 min")>]
[<InlineData(60L, "1 h")>]
[<InlineData(61L, "1 h 1 min")>]
[<InlineData(0L, "0 min")>]
let ``time uses the recap card's words`` (minutes: int64, expected: string) =
    let html = recapPostHtml "frunchy.test" monday (post (week 3L minutes 1L 0L) 2 [])
    Assert.Contains($"3 episodes, {expected}, 1 series", html)

[<Fact>]
let ``no most watched series means one paragraph only`` () =
    let html = recapPostHtml "frunchy.test" monday (post (week 3L 70L 1L 0L) 2 [])
    Assert.DoesNotContain("Most watched", html)
    Assert.Equal(1, html.Split("<p>").Length - 1)

[<Fact>]
let ``two most watched are joined with and, only three are ever shown`` () =
    let two = recapPostHtml "frunchy.test" monday (post (week 5L 100L 2L 0L) 2 [ most "AAA00001" "Alpha" 3L; most "BBB00001" "Bravo" 2L ])
    Assert.Contains(seriesLink "AAA00001" "Alpha" + " (3) and " + seriesLink "BBB00001" "Bravo" + " (2).</p>", two)

    let many =
        recapPostHtml
            "frunchy.test"
            monday
            (post
                (week 9L 100L 4L 0L)
                2
                [ most "AAA00001" "Alpha" 3L; most "BBB00001" "Bravo" 3L; most "CCC00001" "Charlie" 2L; most "DDD00001" "Delta" 1L ])

    Assert.Contains("Charlie", many)
    Assert.DoesNotContain("Delta", many)

[<Fact>]
let ``titles are escaped and every link is absolute under the domain`` () =
    let html =
        recapPostHtml "frunchy.test" monday (post (week 2L 40L 1L 1L) 1 [ most "EVIL0001" "<script>alert(1)</script> & \"Co\"" 2L ])

    Assert.DoesNotContain("<script>", html)
    Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;Co&quot;", html)
    Assert.Contains("href=\"https://frunchy.test/anime/EVIL0001\"", html)
    Assert.DoesNotContain("href=\"/", html)

[<Fact>]
let ``the week's dates cross a month and a year`` () =
    let html = recapPostHtml "frunchy.test" (DateOnly(2026, 12, 28)) (post (week 2L 40L 1L 1L) 1 [])
    Assert.StartsWith("<p>My week on Frunchy (Dec 28 – Jan 3, 2027):", html)
