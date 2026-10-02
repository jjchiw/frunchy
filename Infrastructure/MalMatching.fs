/// Matching Crunchyroll seasons to MyAnimeList entries (feature 003, research R2).
/// Deterministic title rules, no fuzzy scores. Anything uncertain becomes Ambiguous or NoMatch.
/// No I/O of its own: fetching goes through the `Lookup` functions it's given, so the worker
/// (live, stores responses) and `mal-rebuild` (stored responses only) share the same decisions.
module FrunchyBackend.Infrastructure.MalMatching

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open FrunchyBackend.Infrastructure.MalModel

// ---------------------------------------------------------------------------------------------
// Titles
// ---------------------------------------------------------------------------------------------

let private stripAccents (s: string) =
    let decomposed = s.Normalize NormalizationForm.FormD
    let sb = StringBuilder(decomposed.Length)

    for c in decomposed do
        if CharUnicodeInfo.GetUnicodeCategory c <> UnicodeCategory.NonSpacingMark then
            sb.Append c |> ignore

    sb.ToString().Normalize NormalizationForm.FormC

let private dubMarker =
    Regex(@"\((?:[^()]*\s)?(?:dub|dubbed|audio)\)", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

let private yearMarker = Regex(@"\((\d{4})\)")
let private tvMarker = Regex(@"\(tv\)", RegexOptions.IgnoreCase)

/// True when the title says it's a dub, e.g. "Season 01 (English Dub)"
let isDubTitle (title: string) = dubMarker.IsMatch title

/// The title without its dub marker
let stripDub (title: string) = dubMarker.Replace(title, " ").Trim()

/// A "(2020)"-style year in the title, used to tell remakes apart
let yearHint (title: string) =
    let m = yearMarker.Match title
    if m.Success then Some(int m.Groups[1].Value) else None

let private droppedWords = set [ "movie"; "film" ]

/// Comparison form: lowercase, no accents, no dub/"(TV)"/year markers, apostrophes removed,
/// other punctuation as spaces, no leading "the", no "movie"/"film" words, single spaces
let normalize (title: string) =
    if String.IsNullOrWhiteSpace title then
        ""
    else
        let s = title |> stripDub
        let s = tvMarker.Replace(s, " ")
        let s = yearMarker.Replace(s, " ")
        let s = (stripAccents s).ToLowerInvariant()
        let s = s.Replace("'", "").Replace("’", "").Replace("`", "")
        let s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ").Trim()
        let words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        let words = words |> List.filter (fun w -> not (droppedWords.Contains w))

        let words =
            match words with
            | "the" :: rest when not rest.IsEmpty -> rest
            | w -> w

        String.Join(" ", words)

/// A normalized title split into its base text and its season/part markers
type Marked =
    { Base: string
      Season: int option
      Part: int option }

let private romans =
    dict [ "ii", 2; "iii", 3; "iv", 4; "v", 5; "vi", 6; "vii", 7; "viii", 8; "ix", 9; "x", 10 ]

/// Roman numerals that are a season marker anywhere after the first word ("Mushoku Tensei II: …").
/// "v" and "x" only count at the end ("Hunter x Hunter" is not season 10).
let private midRomans = set [ "ii"; "iii"; "iv"; "vi"; "vii"; "viii"; "ix" ]

let private ordinal = Regex(@"^(\d+)(st|nd|rd|th)$")

let private number (w: string) =
    match Int32.TryParse w with
    | true, n -> Some n
    | _ -> None

/// Season markers: "season 2", "2nd season", trailing roman "ii"; part markers: "part 2", "cour 2".
/// "2nd Season" and "Season 2" are the same; a trailing arabic number is NOT a marker
/// ("Jujutsu Kaisen 0", "Eyeshield 21", "Mob Psycho 100").
let mark (normalized: string) : Marked =
    let words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
    let mutable season = None
    let mutable part = None

    let rec scan (acc: string list) (ws: string list) =
        match ws with
        | "season" :: n :: rest when (number n).IsSome ->
            season <- number n
            scan acc rest
        | o :: "season" :: rest when ordinal.IsMatch o ->
            season <- Some(int (ordinal.Match o).Groups[1].Value)
            scan acc rest
        | ("part" | "cour") :: n :: rest when (number n).IsSome ->
            part <- number n
            scan acc rest
        | ("part" | "cour") :: o :: rest when ordinal.IsMatch o ->
            part <- Some(int (ordinal.Match o).Groups[1].Value)
            scan acc rest
        | [ r ] when romans.ContainsKey r && not acc.IsEmpty && season.IsNone ->
            season <- Some romans[r]
            List.rev acc
        | r :: rest when midRomans.Contains r && not acc.IsEmpty && season.IsNone ->
            season <- Some romans[r]
            scan acc rest
        | w :: rest -> scan (w :: acc) rest
        | [] -> List.rev acc

    let rest = scan [] words
    { Base = String.Join(" ", rest); Season = season; Part = part }

let private markOf (title: string) = mark (normalize title)

/// The title before its first subtitle separator (":", " - ", "–", "—", " -X-" style)
let cutSubtitle (title: string) =
    let idx =
        [ title.IndexOf ':'
          title.IndexOf " - "
          title.IndexOf '–'
          title.IndexOf '—'
          title.IndexOf " -" ]
        |> List.filter (fun i -> i > 0)

    match idx with
    | [] -> title
    | xs -> title.Substring(0, List.min xs)

/// Same base text and the same season number (no marker = season 1)
let private sameTitle (a: Marked) (b: Marked) =
    a.Base <> "" && a.Base = b.Base && defaultArg a.Season 1 = defaultArg b.Season 1

let private tierWith (cutCrSide: bool) (crTitle: string) (candidateTitles: string list) : int option =
    let cr = markOf crTitle
    let candidates = candidateTitles |> List.map markOf

    if candidates |> List.exists (sameTitle cr) then
        Some 1
    else
        let candidatesCut = candidateTitles |> List.map (cutSubtitle >> markOf)

        if candidatesCut |> List.exists (sameTitle cr) then
            Some 2
        elif cutCrSide && candidates |> List.exists (sameTitle (markOf (cutSubtitle crTitle))) then
            Some 2
        else
            None

/// Tier 1: equal normalized titles. Tier 2: equal after cutting one side's subtitle.
let titleTier (crTitle: string) (candidateTitles: string list) : int option = tierWith true crTitle candidateTitles

/// A Crunchyroll-side text to compare. A combined "series + season" text is never cut on the
/// Crunchyroll side (cutting "MASHLE: MAGIC AND MUSCLES …" would leave just the series' name).
type CrText =
    | Own of string
    | Combined of string

/// The best tier across several Crunchyroll-side texts
let bestTier (crTexts: CrText list) (candidateTitles: string list) =
    crTexts
    |> List.choose (function
        | Own t -> tierWith true t candidateTitles
        | Combined t -> tierWith false t candidateTitles)
    |> List.sortBy id
    |> List.tryHead

/// The candidates with the best tier any of them reaches (tier 1 beats tier 2)
let private bestHits (texts: CrText list) (candidates: MalAnime list) =
    let tiered = candidates |> List.choose (fun a -> bestTier texts a.AllTitles |> Option.map (fun t -> t, a))

    match tiered |> List.map fst |> List.sortBy id |> List.tryHead with
    | None -> []
    | Some best -> tiered |> List.filter (fun (t, _) -> t = best) |> List.map snd

// ---------------------------------------------------------------------------------------------
// Formats
// ---------------------------------------------------------------------------------------------

let private tvTypes = set [ "TV"; "ONA"; "OVA"; "Special"; "TV Special" ]
let private sequelTypes = set [ "TV"; "ONA" ]
let private movieTypes = set [ "Movie" ]
let private movieLikeTypes = set [ "Movie"; "Special"; "TV Special"; "OVA" ]
let private ovaTypes = set [ "OVA"; "ONA"; "Special"; "TV Special" ]

/// Adult-only entries are never candidates (FR-005): rating "Rx - Hentai", or the Hentai genre
let isAdult (a: MalAnime) =
    (a.Rating |> Option.exists (fun r -> r.StartsWith "Rx")) || (a.ExplicitGenres |> List.exists (fun g -> g.Id = 12))

let private hasType (types: Set<string>) (a: MalAnime) =
    match a.Type with
    | Some t -> types.Contains t
    | None -> false

// ---------------------------------------------------------------------------------------------
// Inputs and outputs
// ---------------------------------------------------------------------------------------------

type SeasonInfo =
    { Id: string
      Title: string option
      Number: int option
      AudioLocale: string option
      EpisodeCount: int
      /// Longest known episode duration in the season, if any
      MaxDurationMs: int option }

type SeriesInfo =
    { SeriesId: string
      Title: string
      Seasons: SeasonInfo list }

type Candidate =
    { MalId: int
      Title: string
      Type: string option
      Year: int option }

let candidateOf (a: MalAnime) =
    { MalId = a.MalId
      Title = a.TitleEnglish |> Option.defaultValue a.Title
      Type = a.Type
      Year = a.Year |> Option.orElse (a.AiredFrom |> Option.map _.Year) }

type Method =
    | BySearch
    | ByLink
    | ByDub

    member m.Db =
        match m with
        | BySearch -> "search"
        | ByLink -> "link"
        | ByDub -> "dub"

type Decision =
    | Matched of malId: int * method: Method * sharedFrom: string option
    | Ambiguous of reason: string * candidates: Candidate list
    | NoMatch of reason: string * candidates: Candidate list

/// Per season: a decision, or "can't decide now" (a temporary failure, or data not held)
type Outcome =
    | Decided of Decision
    | Deferred of reason: string

/// What a lookup can answer
type Fetch<'T> =
    | Got of 'T
    | Gone
    | Unavailable of reason: string

type Lookup =
    { Search: string -> Task<Fetch<MalAnime list>>
      Full: int -> Task<Fetch<MalAnime>> }

// ---------------------------------------------------------------------------------------------
// Dubs
// ---------------------------------------------------------------------------------------------

let private seasonText (s: SeasonInfo) = defaultArg s.Title ""

/// Original-language seasons, and for every dub the original it follows (research R2.4).
/// A dub is a season whose title says so, or a season sharing an original's title and number
/// with a different audio; within such a group the ja-JP one (else the lowest id) is the original.
let splitDubs (seasons: SeasonInfo list) : SeasonInfo list * (SeasonInfo * SeasonInfo) list =
    let key (s: SeasonInfo) = normalize (stripDub (seasonText s)), s.Number

    let marked, unmarked = seasons |> List.partition (fun s -> isDubTitle (seasonText s))

    let groups =
        unmarked
        |> List.groupBy key
        |> List.map (fun (_, group) ->
            let original =
                group
                |> List.sortBy (fun s -> (if s.AudioLocale = Some "ja-JP" then 0 else 1), s.Id)
                |> List.head

            original, group |> List.filter (fun s -> s.Id <> original.Id))

    let originals = groups |> List.map fst
    let groupDubs = groups |> List.collect (fun (o, ds) -> ds |> List.map (fun d -> d, o))

    let markedDubs, orphans =
        marked
        |> List.fold
            (fun (dubs, orphans) d ->
                let title = normalize (stripDub (seasonText d))

                let byTitle = originals |> List.tryFind (fun o -> normalize (seasonText o) = title)
                let byNumber = originals |> List.tryFind (fun o -> o.Number = d.Number && d.Number.IsSome)

                match byTitle |> Option.orElse byNumber with
                | Some o -> (d, o) :: dubs, orphans
                | None -> dubs, d :: orphans)
            ([], [])

    originals @ List.rev orphans, groupDubs @ List.rev markedDubs

// ---------------------------------------------------------------------------------------------
// Resolving a series
// ---------------------------------------------------------------------------------------------

type private SeasonKind =
    | Tv
    | Movie
    | Ova

/// Text of the season title with the series title in front removed ("JUJUTSU KAISEN Season 2" → "season 2")
let relativeTitle (seriesTitle: string) (seasonTitle: string) =
    let series = normalize seriesTitle
    let season = normalize seasonTitle

    if series <> "" && season.StartsWith(series + " ") then season.Substring(series.Length + 1)
    elif season = series then ""
    else season

let private ovaWords = set [ "ova"; "oad"; "ovas"; "oads" ]

let private classify (season: SeasonInfo) (relative: string) =
    let words = relative.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    let normalizedTitle = (stripAccents (seasonText season)).ToLowerInvariant()

    if Regex.IsMatch(normalizedTitle, @"\b(movie|film)\b") then
        Movie
    elif season.EpisodeCount = 1 && (season.MaxDurationMs |> Option.exists (fun d -> d >= 45 * 60 * 1000)) then
        Movie
    elif words |> Array.exists ovaWords.Contains then
        Ova
    else
        Tv

let private withoutOvaWords (relative: string) =
    relative.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter (fun w -> not (ovaWords.Contains w))
    |> fun ws -> String.Join(" ", ws)

let private maxLinked = 20

/// Resolve every given season of a series. `pending` are the season ids to decide; all the series'
/// seasons are passed so dubs can find their originals.
let resolveSeries (lookup: Lookup) (series: SeriesInfo) (pending: Set<string>) : Task<Map<string, Outcome>> =
    task {
        // Memoized lookups for this series
        let fulls = Dictionary<int, Fetch<MalAnime>>()
        let searches = Dictionary<string, Fetch<MalAnime list>>()
        let mutable fetchedLinked = 0

        let full id =
            task {
                match fulls.TryGetValue id with
                | true, f -> return f
                | _ ->
                    let! f = lookup.Full id
                    fulls[id] <- f
                    return f
            }

        let search q =
            task {
                match searches.TryGetValue q with
                | true, f -> return f
                | _ ->
                    let! f = lookup.Search q

                    let f =
                        match f with
                        | Got results -> Got(results |> List.filter (isAdult >> not))
                        | other -> other

                    searches[q] <- f
                    return f
            }

        /// Fetch linked entries (capped); a temporary failure stops and reports
        let fetchAll (ids: int list) =
            task {
                let results = ResizeArray<MalAnime>()
                let mutable failure = None

                for id in ids |> List.distinct do
                    if failure.IsNone && (fulls.ContainsKey id || fetchedLinked < maxLinked) then
                        if not (fulls.ContainsKey id) then
                            fetchedLinked <- fetchedLinked + 1

                        match! full id with
                        | Got a -> results.Add a
                        | Gone -> ()
                        | Unavailable reason -> failure <- Some reason

                return List.ofSeq results, failure
            }

        // ---- the anchor: MyAnimeList's first season of the franchise -------------------------
        let anchorTask: Lazy<Task<Result<MalAnime, Outcome>>> =
            lazy
                (task {
                    match! search series.Title with
                    | Unavailable reason -> return Error(Deferred reason)
                    | Gone -> return Error(Decided(NoMatch("no-search-results", [])))
                    | Got results ->
                        let candidates =
                            results
                            |> List.filter (hasType tvTypes)
                            |> List.filter (fun a -> a.AllTitles |> List.exists (fun t -> (markOf t).Part |> Option.forall (fun p -> p <= 1)))

                        let qualifying tier =
                            candidates |> List.filter (fun a -> titleTier series.Title a.AllTitles = Some tier)

                        let tierHits =
                            match qualifying 1 with
                            | [] -> qualifying 2
                            | hits -> hits

                        let tierHits =
                            match yearHint series.Title with
                            | Some y ->
                                match tierHits |> List.filter (fun a -> a.Year = Some y || (a.AiredFrom |> Option.map _.Year) = Some y) with
                                | [ one ] -> [ one ]
                                | _ -> tierHits
                            | None -> tierHits

                        let shown = results |> List.truncate 5 |> List.map candidateOf

                        match tierHits with
                        | [] -> return Error(Decided(NoMatch("no-title-match", shown)))
                        | [ one ] ->
                            match! full one.MalId with
                            | Got a -> return Ok a
                            | Gone -> return Error(Decided(NoMatch("not-found", shown)))
                            | Unavailable reason -> return Error(Deferred reason)
                        | several ->
                            // The earliest one is the first season when the others are in its sequel chain
                            let earliest =
                                several
                                |> List.sortBy (fun a -> a.AiredFrom |> Option.defaultValue DateOnly.MaxValue, a.MalId)
                                |> List.head

                            match! full earliest.MalId with
                            | Unavailable reason -> return Error(Deferred reason)
                            | Gone -> return Error(Decided(Ambiguous("several-first-seasons", several |> List.truncate 5 |> List.map candidateOf)))
                            | Got first ->
                                let others = several |> List.filter (fun a -> a.MalId <> first.MalId) |> List.map _.MalId |> Set.ofList
                                let seen = HashSet<int>([ first.MalId ])
                                let mutable frontier = [ first ]
                                let mutable failure = None
                                let mutable steps = 0

                                while not frontier.IsEmpty && failure.IsNone && steps < 10 do
                                    steps <- steps + 1

                                    let next =
                                        frontier
                                        |> List.collect (fun a ->
                                            a.Relations
                                            |> List.filter (fun r -> r.Relation = "Sequel")
                                            |> List.collect (fun r -> r.Entries |> List.map _.Id))
                                        |> List.filter (fun id -> seen.Add id)

                                    let! fetched, err = fetchAll next
                                    failure <- err
                                    frontier <- fetched

                                match failure with
                                | Some reason -> return Error(Deferred reason)
                                | None when others |> Set.forall seen.Contains -> return Ok first
                                | None -> return Error(Decided(Ambiguous("several-first-seasons", several |> List.truncate 5 |> List.map candidateOf)))
                 })

        let anchor () = anchorTask.Force()

        /// The sequel chain from the anchor (TV/ONA only), in order
        let sequelChain (first: MalAnime) =
            task {
                let chain = ResizeArray<MalAnime>()
                let seen = HashSet<int>([ first.MalId ])
                let mutable current = Some first
                let mutable failure = None

                while current.IsSome && failure.IsNone && chain.Count < 12 do
                    let sequels =
                        current.Value.Relations
                        |> List.filter (fun r -> r.Relation = "Sequel")
                        |> List.collect (fun r -> r.Entries |> List.map _.Id)
                        |> List.filter seen.Add

                    let! fetched, err = fetchAll sequels
                    failure <- err

                    match fetched |> List.filter (hasType sequelTypes) |> List.sortBy (fun a -> a.AiredFrom |> Option.defaultValue DateOnly.MaxValue) with
                    | next :: _ ->
                        chain.Add next
                        current <- Some next
                    | [] -> current <- None

                return List.ofSeq chain, failure
            }

        /// Anchor plus everything reachable through Sequel/Prequel (any TV-like type)
        let franchise (first: MalAnime) =
            task {
                let pool = ResizeArray<MalAnime>([ first ])
                let seen = HashSet<int>([ first.MalId ])
                let mutable frontier = [ first ]
                let mutable failure = None

                while not frontier.IsEmpty && failure.IsNone do
                    let next =
                        frontier
                        |> List.collect (fun a ->
                            a.Relations
                            |> List.filter (fun r -> r.Relation = "Sequel" || r.Relation = "Prequel")
                            |> List.collect (fun r -> r.Entries |> List.map _.Id))
                        |> List.filter seen.Add

                    let! fetched, err = fetchAll next
                    failure <- err
                    let fetched = fetched |> List.filter (hasType tvTypes)
                    pool.AddRange fetched
                    frontier <- fetched

                return List.ofSeq pool, failure
            }

        let linkedEntries (first: MalAnime) (accept: string -> bool) =
            first.Relations
            |> List.filter (fun r -> accept r.Relation)
            |> List.collect (fun r -> r.Entries)

        /// "Season N" relative to the anchor: an entry marked N, else the Nth season-entry of the chain
        let byNumber (first: MalAnime) (n: int) =
            task {
                if n <= 1 then
                    return Decided(Matched(first.MalId, BySearch, None))
                else
                    let! chain, failure = sequelChain first

                    match failure with
                    | Some reason -> return Deferred reason
                    | None ->
                        let marked =
                            chain
                            |> List.filter (fun a ->
                                a.AllTitles
                                |> List.exists (fun t ->
                                    let m = markOf t
                                    m.Season = Some n && m.Part |> Option.forall (fun p -> p <= 1)))

                        match marked with
                        | [ one ] -> return Decided(Matched(one.MalId, ByLink, None))
                        | _ :: _ :: _ -> return Decided(Ambiguous("several-season-entries", marked |> List.truncate 5 |> List.map candidateOf))
                        | [] ->
                            let seasonsOnly =
                                first
                                :: (chain
                                    |> List.filter (fun a ->
                                        a.AllTitles |> List.forall (fun t -> (markOf t).Part |> Option.forall (fun p -> p <= 1))))

                            if chain.IsEmpty then
                                // MyAnimeList keeps the whole show as one entry (Eyeshield 21, One Piece)
                                return Decided(Matched(first.MalId, ByLink, None))
                            elif seasonsOnly.Length >= n then
                                return Decided(Matched(seasonsOnly[n - 1].MalId, ByLink, None))
                            else
                                return Decided(Ambiguous("season-not-in-chain", (first :: chain) |> List.truncate 5 |> List.map candidateOf))
            }

        /// Fallback: search by the season's own title (the first text's string is the query)
        let searchSeason (texts: CrText list) (accept: MalAnime -> bool) =
            task {
                let query =
                    match List.head texts with
                    | Own t
                    | Combined t -> t

                match! search query with
                | Unavailable reason -> return Deferred reason
                | Gone -> return Decided(NoMatch("no-search-results", []))
                | Got results ->
                    let shown = results |> List.truncate 5 |> List.map candidateOf

                    match bestHits texts (results |> List.filter accept) with
                    | [ one ] ->
                        match! full one.MalId with
                        | Got _ -> return Decided(Matched(one.MalId, BySearch, None))
                        | Gone -> return Decided(NoMatch("not-found", shown))
                        | Unavailable reason -> return Deferred reason
                    | [] -> return Decided(NoMatch("no-title-match", shown))
                    | several -> return Decided(Ambiguous("several-candidates", several |> List.truncate 5 |> List.map candidateOf))
            }

        let decideOriginal (season: SeasonInfo) : Task<Outcome> =
            task {
                let title = stripDub (seasonText season)
                let relative = relativeTitle series.Title title
                let kind = classify season relative

                let relativeCore =
                    match kind with
                    | Ova -> withoutOvaWords relative
                    | _ -> relative

                let marked = mark relativeCore
                let generic = marked.Base = ""

                // Raw titles (normalization happens inside the comparison)
                let texts =
                    [ if relativeCore <> "" && normalize title <> normalize series.Title && not ((normalize title).StartsWith(normalize series.Title + " ")) then
                          yield Combined $"{series.Title} {title}"
                      yield Own title ]
                    |> List.distinct

                let! anchored = anchor ()

                match kind, anchored with
                | _, Error(Deferred reason) -> return Deferred reason

                // ---- TV seasons -------------------------------------------------------------
                | Tv, Ok first when generic -> return! byNumber first (defaultArg marked.Season 1)
                | Tv, Ok first ->
                    let! pool, failure = franchise first

                    match failure with
                    | Some reason -> return Deferred reason
                    | None ->
                        match bestHits texts pool with
                        | [ one ] -> return Decided(Matched(one.MalId, (if one.MalId = first.MalId then BySearch else ByLink), None))
                        | _ :: _ :: _ as several -> return Decided(Ambiguous("several-season-entries", several |> List.truncate 5 |> List.map candidateOf))
                        | [] ->
                            match marked.Season with
                            | Some n -> return! byNumber first n
                            | None when pool.Length = 1 ->
                                // No sequels at all: MyAnimeList has one entry for every arc (One Piece)
                                return Decided(Matched(first.MalId, ByLink, None))
                            | None -> return! searchSeason texts (hasType tvTypes)
                | Tv, Error(Decided _ as anchorDecision) when generic ->
                    match marked.Season with
                    | Some n when n > 1 -> return! searchSeason [ Own $"{series.Title} Season {n}" ] (hasType tvTypes)
                    | _ -> return anchorDecision
                | Tv, Error(Decided _) -> return! searchSeason texts (hasType tvTypes)

                // ---- Movies -----------------------------------------------------------------
                | Movie, anchoredResult ->
                    let isMovieByTitle = Regex.IsMatch((stripAccents title).ToLowerInvariant(), @"\b(movie|film)\b")
                    let accept = hasType (if isMovieByTitle then movieTypes else movieLikeTypes)

                    let! fromLinks =
                        task {
                            match anchoredResult with
                            | Ok first ->
                                let linked = linkedEntries first (fun r -> r <> "Adaptation" && r <> "Character")

                                // Linked names first (no extra requests when one fits), then the linked entries themselves
                                let named =
                                    linked |> List.filter (fun e -> (bestTier texts [ e.Name ]).IsSome) |> List.map _.Id

                                let! fetched, failure = fetchAll named

                                match bestHits texts (fetched |> List.filter accept), failure with
                                | [], None ->
                                    let! all, failure = fetchAll (linked |> List.map _.Id)
                                    return bestHits texts (all |> List.filter accept), failure
                                | hits, failure -> return hits, failure
                            | Error _ -> return [], None
                        }

                    match fromLinks with
                    | _, Some reason -> return Deferred reason
                    | [ one ], None -> return Decided(Matched(one.MalId, ByLink, None))
                    | _ :: _ :: _ as several, None -> return Decided(Ambiguous("several-movies", several |> List.truncate 5 |> List.map candidateOf))
                    | [], None -> return! searchSeason texts accept

                // ---- OVAs -------------------------------------------------------------------
                | Ova, Ok first ->
                    let ids =
                        linkedEntries first (fun r ->
                            r = "Side Story" || r = "Other" || r = "Summary" || r = "Alternative Version" || r = "Spin-Off")
                        |> List.map _.Id

                    let! fetched, failure = fetchAll ids

                    match failure with
                    | Some reason -> return Deferred reason
                    | None ->
                        let ovas = fetched |> List.filter (hasType ovaTypes)

                        let titled = if generic then [] else bestHits texts ovas

                        match titled, ovas with
                        | [ one ], _ -> return Decided(Matched(one.MalId, ByLink, None))
                        | [], [ one ] -> return Decided(Matched(one.MalId, ByLink, None))
                        | [], [] -> return Decided(NoMatch("no-linked-ova", []))
                        | several, _ when not several.IsEmpty -> return Decided(Ambiguous("several-ovas", several |> List.truncate 5 |> List.map candidateOf))
                        | _, several -> return Decided(Ambiguous("several-ovas", several |> List.truncate 5 |> List.map candidateOf))
                | Ova, Error(Decided _) -> return! searchSeason texts (hasType ovaTypes)
            }

        let originals, dubs = splitDubs series.Seasons
        let results = Dictionary<string, Outcome>()

        let needed =
            originals
            |> List.filter (fun o ->
                pending.Contains o.Id || dubs |> List.exists (fun (d, orig) -> orig.Id = o.Id && pending.Contains d.Id))

        for season in needed do
            let! outcome = decideOriginal season
            results[season.Id] <- outcome

        for dub, original in dubs do
            if pending.Contains dub.Id then
                results[dub.Id] <-
                    match results.TryGetValue original.Id with
                    | true, Decided(Matched(id, _, _)) -> Decided(Matched(id, ByDub, Some original.Id))
                    | true, other -> other
                    | _ -> Deferred "original-not-decided"

        return
            results
            |> Seq.filter (fun kv -> pending.Contains kv.Key)
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq
    }
