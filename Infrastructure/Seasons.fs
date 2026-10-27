/// Airing seasons (feature 027): the UTC quarter of an instant, named the way MyAnimeList and the charts name them.
/// Winter is January to March, then Spring, Summer and Fall. Pure, no I/O; the DNA page and the charts both use it.
module FrunchyBackend.Infrastructure.Seasons

open System

/// A Crunchyroll series is a long runner when its MyAnimeList entry has more than this many episodes, or any known
/// episode of it is numbered above it (One Piece, Detective Conan, Naruto Shippuden, Bleach)
[<Literal>]
let LongRunnerEpisodes = 100

/// The long-run card shows from this many fully watched episodes of the long runner
[<Literal>]
let LongRunMinimum = 24

/// An episode fully watched within this many days of airing was watched while airing
[<Literal>]
let WhileAiringDays = 7

let private names = [| "winter"; "spring"; "summer"; "fall" |]

/// The season name of a month, 1 to 12
let nameOfMonth (month: int) = names[(month - 1) / 3]

/// The season of an instant, by UTC quarter: (lowercase name, year, the quarter's first instant at 00:00 UTC)
let ofInstant (instant: DateTimeOffset) : string * int * DateTimeOffset =
    let utc = instant.UtcDateTime
    let quarter = (utc.Month - 1) / 3

    names[quarter], utc.Year, DateTimeOffset(DateTime(utc.Year, quarter * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc))

/// The key a season goes by in the page's hooks: `fall-2026`
let key (name: string) (year: int) = $"{name}-{year}"

/// Seasons compare by this: a later season has a larger number. Unknown names sort before everything of their year.
let order (name: string) (year: int) =
    year * 4 + max 0 (Array.IndexOf(names, name))

/// The season of an `order` number, the inverse of it: (lowercase name, year)
let ofOrder (number: int) : string * int = names[number % 4], number / 4
