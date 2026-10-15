/// The rules every comment write shares (feature 009, research R3, R7, R9): creating and editing apply the same
/// normalization, length limit, id alphabet and spoiler warning, so no slice calls another's private code.
module FrunchyBackend.Infrastructure.CommentRules

open System.Text
open System.Text.RegularExpressions
open FrunchyBackend.Views.Texts

/// Crunchyroll's id alphabet, checked before the database is touched
let crunchyrollIdPattern = Regex(@"\A[A-Z0-9]{6,20}\z", RegexOptions.CultureInvariant)

/// The comment id's alphabet (the `comments.id` CHECK)
let commentIdPattern = Regex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)

let maxLength = 2000

/// CRLF and CR folded to LF, control characters other than LF and TAB removed, broken surrogates replaced, trimmed
let normalize (raw: string) : string =
    let folded = raw.Replace("\r\n", "\n").Replace("\r", "\n")
    let sb = StringBuilder()

    for rune in folded.EnumerateRunes() do
        if not (Rune.IsControl rune) || rune.Value = 10 || rune.Value = 9 then
            sb.Append(rune.ToString()) |> ignore

    sb.ToString().Trim()

/// Unicode scalar values, not UTF-16 units
let runeCount (s: string) =
    let mutable n = 0

    for _ in s.EnumerateRunes() do
        n <- n + 1

    n

/// The normalized content, or the error key (`empty`, `too-long`) shown on the form
let validateContent (typed: string) : Result<string, string> =
    let content = normalize typed
    let length = runeCount content

    if length = 0 then Error "empty"
    elif length > maxLength then Error "too-long"
    else Ok content

/// The warning stored with a spoiler, in the author's language: season display number (else season number),
/// episode number, the special's label
let spoilerSummary (t: Texts) (seasonDisplay: string option) (seasonNumber: int option) (number: int option) (label: string option) =
    let season = seasonDisplay |> Option.orElse (seasonNumber |> Option.map string)
    t.SpoilerSummary season number label
