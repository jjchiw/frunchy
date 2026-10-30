/// Feature 028 (T007): the Note's HTML is a pure function, tested without Postgres.
module FrunchyBackend.Tests.WatchUpdateHtmlTests

open Xunit
open FrunchyBackend.Workers.FediverseOutbox

let private series (id: string) (title: string) (count: int) (episodes: (int * string) list) : WatchedSeries =
    { SeriesId = id; Title = title; EpisodeCount = count; Episodes = episodes }

let private link (href: string) (text: string) = $"<a href=\"{href}\">{text}</a>"
let private seriesLink (id: string) (title: string) = link $"https://frunchy.test/anime/{id}" title
let private episodeLink (seriesId: string) (episodeId: string) (number: int) =
    link $"https://frunchy.test/anime/{seriesId}/episode/{episodeId}" (string number)

[<Fact>]
let ``one series lists its episode numbers, ascending, each linking to its episode page`` () =
    let html = watchUpdateHtml "frunchy.test" [ series "FRIEREN1" "Frieren" 3 [ 7, "EP7"; 5, "EP5"; 6, "EP6" ] ]

    Assert.Equal(
        "<p>Watched 3 episodes of "
        + seriesLink "FRIEREN1" "Frieren"
        + " ("
        + episodeLink "FRIEREN1" "EP5" 5
        + ", "
        + episodeLink "FRIEREN1" "EP6" 6
        + ", "
        + episodeLink "FRIEREN1" "EP7" 7
        + ").</p>",
        html
    )

[<Fact>]
let ``a single episode is singular`` () =
    let html = watchUpdateHtml "frunchy.test" [ series "APOTHEC1" "The Apothecary Diaries" 1 [ 12, "E12" ] ]
    Assert.StartsWith("<p>Watched 1 episode of ", html)
    Assert.DoesNotContain("episodes", html)

[<Fact>]
let ``several series are joined with commas and and, the one with most episodes first`` () =
    let html =
        watchUpdateHtml
            "frunchy.test"
            [ series "SERB" "Bravo" 1 [ 1, "B1" ]
              series "SERA" "Alpha" 1 [ 1, "A1" ]
              series "SERC" "Charlie" 4 [ 1, "C1"; 2, "C2"; 3, "C3"; 4, "C4" ] ]

    let charlie = html.IndexOf "Charlie"
    let alpha = html.IndexOf "Alpha"
    let bravo = html.IndexOf "Bravo"
    Assert.True(charlie >= 0 && charlie < alpha && alpha < bravo, html)
    Assert.Contains("), 1 episode of " + seriesLink "SERA" "Alpha", html)
    Assert.Contains(" and 1 episode of " + seriesLink "SERB" "Bravo", html)

[<Fact>]
let ``two series are joined with and only`` () =
    let html =
        watchUpdateHtml "frunchy.test" [ series "SERA" "Alpha" 2 [ 1, "A1"; 2, "A2" ]; series "SERB" "Bravo" 1 [ 1, "B1" ] ]

    Assert.DoesNotContain(", 1 episode of", html)
    Assert.Contains(") and 1 episode of " + seriesLink "SERB" "Bravo", html)

[<Fact>]
let ``more than five series name five, then and N more series`` () =
    let many =
        [ for n in 1..8 -> series $"SER{n}" $"Show {n}" (10 - n) [ 1, $"E{n}" ] ]

    let html = watchUpdateHtml "frunchy.test" many

    // the five with the most episodes are named, the other three are counted
    for n in 1..5 do
        Assert.Contains(seriesLink $"SER{n}" $"Show {n}", html)

    for n in 6..8 do
        Assert.DoesNotContain($"Show {n}", html)

    Assert.EndsWith(" and 3 more series.</p>", html)

[<Fact>]
let ``more than six episode numbers list six, then an ellipsis`` () =
    let episodes = [ for n in 1..9 -> n, $"EP{n}" ]
    let html = watchUpdateHtml "frunchy.test" [ series "LONGRUN1" "Long" 9 episodes ]

    Assert.StartsWith("<p>Watched 9 episodes of ", html)

    for n in 1..6 do
        Assert.Contains(episodeLink "LONGRUN1" $"EP{n}" n, html)

    for n in 7..9 do
        Assert.DoesNotContain($"EP{n}", html)

    Assert.EndsWith(", …).</p>", html)

[<Fact>]
let ``an episode without a number is counted but not listed`` () =
    let html = watchUpdateHtml "frunchy.test" [ series "SPECIAL1" "Specials" 3 [ 2, "E2" ] ]
    Assert.Contains("3 episodes of ", html)
    Assert.Contains(" (" + episodeLink "SPECIAL1" "E2" 2 + ").", html)

    let none = watchUpdateHtml "frunchy.test" [ series "SPECIAL1" "Specials" 2 [] ]
    Assert.Equal("<p>Watched 2 episodes of " + seriesLink "SPECIAL1" "Specials" + ".</p>", none)

[<Fact>]
let ``titles are escaped`` () =
    let html = watchUpdateHtml "frunchy.test" [ series "EVIL0001" "<script>alert(1)</script> & Co" 1 [ 1, "E1" ] ]
    Assert.DoesNotContain("<script>", html)
    Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; Co", html)

[<Fact>]
let ``every link is absolute under the domain and no episode title is used`` () =
    let html = watchUpdateHtml "frunchy.test" [ series "SERA" "Alpha" 2 [ 1, "A1"; 2, "A2" ] ]
    let hrefs = System.Text.RegularExpressions.Regex.Matches(html, "href=\"([^\"]*)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq
    Assert.Equal<string list>([ "https://frunchy.test/anime/SERA"; "https://frunchy.test/anime/SERA/episode/A1"; "https://frunchy.test/anime/SERA/episode/A2" ], hrefs)
    Assert.StartsWith("<p>Watched 2 episodes of ", html)
