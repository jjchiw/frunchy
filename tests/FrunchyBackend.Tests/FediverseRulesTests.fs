/// Feature 015 (T002): the pure fediverse rules, no database and no app
module FrunchyBackend.Tests.FediverseRulesTests

open System
open System.Text
open System.Text.RegularExpressions
open Xunit
open FrunchyBackend.Infrastructure.Fediverse

// ---------------------------------------------------------------------------
// Megafono-Signature
// ---------------------------------------------------------------------------

let private secret = "the-test-secret"
let private now = DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)
let private body = Encoding.UTF8.GetBytes """{"id":"evt_1","type":"reply.received"}"""

let private signedAt (offsetSeconds: int64) =
    signBody secret (now.AddSeconds(float offsetSeconds)) body

[<Fact>]
let ``a valid signature verifies`` () =
    Assert.True(verifySignature secret now (signedAt 0L) body)

[<Fact>]
let ``the header has the contract's shape`` () =
    Assert.Matches("^t=[0-9]+,v1=[0-9a-f]{64}$", signedAt 0L)

[<Fact>]
let ``a wrong secret fails`` () =
    Assert.False(verifySignature "another-secret" now (signedAt 0L) body)

[<Fact>]
let ``a tampered body fails`` () =
    let tampered = Encoding.UTF8.GetBytes """{"id":"evt_2","type":"reply.received"}"""
    Assert.False(verifySignature secret now (signedAt 0L) tampered)

[<Fact>]
let ``a signature 301 seconds old fails and 300 passes`` () =
    Assert.False(verifySignature secret now (signedAt -301L) body)
    Assert.True(verifySignature secret now (signedAt -300L) body)

[<Fact>]
let ``a signature skewed into the future fails past 5 minutes`` () =
    Assert.False(verifySignature secret now (signedAt 301L) body)
    Assert.True(verifySignature secret now (signedAt 299L) body)

[<Fact>]
let ``a signature with missing parts fails`` () =
    let valid = signedAt 0L
    let t = valid.Split(',')[0]
    let v1 = valid.Split(',')[1]

    for header in [ ""; null; t; v1; "garbage"; "t=abc,v1=00"; "t=,v1="; $"{t},v1=" ] do
        Assert.False(verifySignature secret now header body, $"accepted: {header}")

[<Fact>]
let ``an empty secret never verifies`` () =
    let header = signBody "" now body
    Assert.False(verifySignature "" now header body)

[<Fact>]
let ``the hex digest is compared without regard to case`` () =
    let valid = signedAt 0L
    let upper = Regex.Replace(valid, "v1=([0-9a-f]+)", fun m -> "v1=" + m.Groups[1].Value.ToUpperInvariant())
    Assert.True(verifySignature secret now upper body)

// ---------------------------------------------------------------------------
// Series handles
// ---------------------------------------------------------------------------

let private never (_: string) = false

[<Fact>]
let ``a slug becomes the handle as it is`` () =
    Assert.Equal("frieren-beyond-journeys-end", seriesHandleOf (Some "frieren-beyond-journeys-end") "GG5H5XQ7D" never)

[<Fact>]
let ``a long slug is cut at a dash within 30 characters`` () =
    let handle = seriesHandleOf (Some "the-eminence-in-shadow-the-very-long-title") "GG5H5XQ7D" never
    Assert.Equal("the-eminence-in-shadow-the", handle)
    Assert.True(handle.Length <= 30)
    Assert.True(isValidHandle handle)

[<Fact>]
let ``a long word with no dash is cut hard at 30`` () =
    let handle = seriesHandleOf (Some(String('a', 80))) "GG5H5XQ7D" never
    Assert.Equal(String('a', 30), handle)

[<Fact>]
let ``a slug cut exactly before a dash keeps all 30 characters`` () =
    let word30 = String('b', 30)
    Assert.Equal(word30, seriesHandleOf (Some $"{word30}-rest") "GG5H5XQ7D" never)

[<Fact>]
let ``punctuation, capitals and runs become single dashes with none at the ends`` () =
    Assert.Equal("one-piece-film-red", seriesHandleOf (Some "  One Piece: Film -- Red!! ") "GG5H5XQ7D" never)
    Assert.Equal("a-b", seriesHandleOf (Some "!!!a***b???") "GG5H5XQ7D" never)
    Assert.Equal("kaguya_sama-love-is-war", seriesHandleOf (Some "Kaguya_sama: Love Is War") "GG5H5XQ7D" never)

[<Fact>]
let ``an empty or unusable slug falls back to the series id`` () =
    Assert.Equal("series-gg5h5xq7d", seriesHandleOf None "GG5H5XQ7D" never)
    Assert.Equal("series-gg5h5xq7d", seriesHandleOf (Some "") "GG5H5XQ7D" never)
    Assert.Equal("series-gg5h5xq7d", seriesHandleOf (Some "   ") "GG5H5XQ7D" never)
    Assert.Equal("series-gg5h5xq7d", seriesHandleOf (Some "日本語") "GG5H5XQ7D" never)

[<Fact>]
let ``the fallback from a long series id stays within 30 characters`` () =
    let handle = seriesHandleOf None "GG5H5XQ7DGG5H5XQ7DGG5H5" never
    Assert.True(handle.Length <= 30)
    Assert.True(isValidHandle handle)

[<Fact>]
let ``a clash adds a numeric suffix`` () =
    let taken = set [ "frieren" ]
    Assert.Equal("frieren-2", seriesHandleOf (Some "frieren") "GG5H5XQ7D" taken.Contains)
    let taken = set [ "frieren"; "frieren-2"; "frieren-3" ]
    Assert.Equal("frieren-4", seriesHandleOf (Some "frieren") "GG5H5XQ7D" taken.Contains)

[<Fact>]
let ``a suffix shortens a handle that is already 30 characters long`` () =
    let word30 = String('c', 30)
    let taken = set [ word30 ]
    let handle = seriesHandleOf (Some word30) "GG5H5XQ7D" taken.Contains
    Assert.Equal(String('c', 28) + "-2", handle)
    Assert.Equal(30, handle.Length)

[<Fact>]
let ``suffixes of two digits still fit and stay valid`` () =
    let slug = "the-eminence-in-shadow-the-very-long"
    let candidates = handleCandidates (Some slug) "GG5H5XQ7D" 120
    Assert.True(candidates |> List.forall (fun h -> h.Length <= 30 && isValidHandle h))
    Assert.Equal(candidates.Length, (List.distinct candidates).Length)
    Assert.EndsWith("-99", candidates |> List.find (fun h -> h.EndsWith "-99"))
    Assert.EndsWith("-100", candidates |> List.find (fun h -> h.EndsWith "-100"))

[<Fact>]
let ``every handle made is valid under Megafono's rule`` () =
    for slug in [ "a"; "ab"; "x-y"; "_"; "-_-"; "9"; "re-zero-starting-life-in-another-world-season-2" ] do
        let handle = seriesHandleOf (Some slug) "GG5H5XQ7D" never
        Assert.True(isValidHandle handle, $"'{slug}' gave '{handle}'")

[<Fact>]
let ``the handle rule refuses a trailing dash, dots and long names`` () =
    Assert.True(isValidHandle "alice")
    Assert.True(isValidHandle "a")
    Assert.False(isValidHandle "alice-")
    Assert.False(isValidHandle "-alice")
    Assert.False(isValidHandle "a.b")
    Assert.False(isValidHandle(String('a', 31)))
    Assert.False(isValidHandle "Alice")
    Assert.False(isValidHandle "")

// ---------------------------------------------------------------------------
// htmlToText
// ---------------------------------------------------------------------------

[<Fact>]
let ``paragraphs become line breaks`` () =
    Assert.Equal("one\ntwo", htmlToText "<p>one</p><p>two</p>")

[<Fact>]
let ``br in any form becomes a line break`` () =
    Assert.Equal("a\nb\nc\nd", htmlToText "a<br>b<br/>c<BR />d")

[<Fact>]
let ``list items end a line`` () =
    Assert.Equal("one\ntwo", htmlToText "<ul><li>one</li><li>two</li></ul>")

[<Fact>]
let ``entities are decoded`` () =
    Assert.Equal("Tom & Jerry <3 \"quotes\" and 'apostrophes'", htmlToText "<p>Tom &amp; Jerry &lt;3 &quot;quotes&quot; and &#39;apostrophes&#39;</p>")

[<Fact>]
let ``nested tags and links show only their text`` () =
    Assert.Equal("look at this link now", htmlToText """<p>look at <a href="https://x.test/" rel="nofollow"><span class="h">this</span> <b>link</b></a> now</p>""")

[<Fact>]
let ``a script is shown as text, not run or hidden`` () =
    let text = htmlToText "<p>hi</p><script>alert('x')</script>"
    Assert.DoesNotContain("<script", text)
    Assert.Contains("alert('x')", text)

[<Fact>]
let ``an encoded tag stays text after decoding`` () =
    // The decoded text is later HTML-escaped by the view, so "<b>" stays literal text
    Assert.Equal("<b>not bold</b>", htmlToText "&lt;b&gt;not bold&lt;/b&gt;")

[<Fact>]
let ``three or more newlines collapse to two and the ends are trimmed`` () =
    Assert.Equal("a\n\nb", htmlToText "<p>a</p><br><br><br><p>b</p><br>")
    Assert.Equal("", htmlToText "  <p></p>  ")
    Assert.Equal("", htmlToText null)

// ---------------------------------------------------------------------------
// remoteCommentId
// ---------------------------------------------------------------------------

[<Fact>]
let ``a remote comment id fits the comment id rule and is stable`` () =
    let objectId = "https://mastodon.social/users/bob/statuses/112233445566778899"
    let id = remoteCommentId objectId
    Assert.Matches("^[A-Za-z0-9_-]{1,64}$", id)
    Assert.StartsWith("r-", id)
    Assert.Equal(42, id.Length)
    Assert.Equal(id, remoteCommentId objectId)

[<Fact>]
let ``different objects give different ids, including every base64url character class`` () =
    let ids = [ for i in 1..500 -> remoteCommentId $"https://remote.test/objects/{i}" ]
    Assert.Equal(500, (List.distinct ids).Length)
    Assert.True(ids |> List.forall (fun id -> Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")))

// ---------------------------------------------------------------------------
// commentHtml
// ---------------------------------------------------------------------------

[<Fact>]
let ``a comment's text is escaped`` () =
    Assert.Equal("<p>&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; more</p>", commentHtml "<script>alert(\"x\")</script> & more")

[<Fact>]
let ``single line breaks become br and blank lines start a paragraph`` () =
    Assert.Equal("<p>line one<br>line two</p><p>second paragraph</p>", commentHtml "line one\nline two\n\nsecond paragraph")

[<Fact>]
let ``windows line endings and stray blank lines are normalised`` () =
    Assert.Equal("<p>a<br>b</p><p>c</p>", commentHtml "a\r\nb\r\n\r\n\r\n\r\nc\r\n")

[<Fact>]
let ``round trip through htmlToText keeps the words and line breaks`` () =
    let text = "first line\nsecond line\nthird with <b> & \"quotes\""
    Assert.Equal(text, htmlToText (commentHtml text))
    // a paragraph break becomes one line break: R7 turns </p> into a single newline
    Assert.Equal("one\ntwo", htmlToText (commentHtml "one\n\ntwo"))

// ---------------------------------------------------------------------------
// actorUrl
// ---------------------------------------------------------------------------

[<Fact>]
let ``the actor url is https on the domain`` () =
    Assert.Equal("https://frunchy.test/ap/actors/abc-1", actorUrl "frunchy.test" "abc-1")
