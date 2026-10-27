module FrunchyBackend.Handlers.Users.Dna

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Domains.Identity
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Views
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

// ---------------------------------------------------------------------------
// Model (specs/004-anime-dna-design-system/data-model.md)
// ---------------------------------------------------------------------------

/// The page appears from this many fully watched episodes (FR-014)
let minimumEpisodes = 15L

/// The "Your seasons" section lists this many airing seasons, the newest first; older ones are only counted
let listedSeasons = 8

/// The top season shows this many tiles before its "Show all" details
let topSeasonTiles = 6

/// A named share of a whole, in percent with 1 decimal. The whole is watched seasons (feature 027): a MyAnimeList
/// entry watched in one airing season is one, so a long show weighs one per season it aired in, not its minutes.
type ShareRow = { Name: string; Share: decimal; Seasons: int64 }

type GenreRow =
    { Name: string
      Share: decimal
      Seasons: int64
      /// (Crunchyroll series id, title) with the most watched seasons in the genre; None on "Others" and on a genre without one
      Top: (string * string) option
      /// The closing "Others" row, which stands for every genre outside the top five
      IsOthers: bool }

type Insignia =
    { SeriesId: string
      Title: string
      Poster: string option
      Format: string option
      Studio: string option
      Season: string option
      Year: int option
      FullyWatched: int64
      MalEpisodes: int option
      Hours: decimal }

/// The long runner with the most minutes, shown when the person watched enough of it (feature 027)
type LongRun =
    { SeriesId: string
      Title: string
      Poster: string option
      FullyWatched: int64
      Hours: decimal
      /// The first and last airing season (lowercase name, year) of the episodes fully watched, when they have air dates
      Span: ((string * int) * (string * int)) option }

/// One show (Crunchyroll series) watched from one airing season
type SeasonShow =
    { SeriesId: string
      Title: string
      Poster: string option
      /// Episodes fully watched that aired in the season
      Episodes: int64
      /// An episode of it was fully watched within `Seasons.WhileAiringDays` of airing
      WhileAiring: bool }

type AiringSeason =
    { Name: string
      Year: int
      /// Most episodes first, then title, then series id
      Shows: SeasonShow list
      /// How many of the shows were watched while airing
      Airing: int }

type SeasonsSection =
    { /// Airing seasons with at least one fully watched episode
      Total: int
      /// The season with the most shows (a tie goes to the newer one)
      Top: AiringSeason
      /// The `listedSeasons` newest, newest first
      Listed: AiringSeason list
      /// Seasons that are counted in `Total` but not listed
      Earlier: int }

type Pace =
    { Episodes: int64
      PerDay: decimal
      /// Weeks of the 13 with a fully watched episode
      ActiveWeeks: int
      /// Fully watched episodes per day, index 0 = the last 24 hours, 89 = 89 days ago
      PerDayCounts: int64 array }

type Figures =
    { Hours: decimal
      Days: decimal
      Episodes: int64
      /// Matched minutes ÷ all minutes, deduplicated (FR-004): the coverage tile's figure, not the cards'
      Coverage: decimal
      /// Watched seasons, with and without a MyAnimeList entry (what the taste cards' notes say "Based on … of")
      SeasonsTotal: int64
      /// Watched seasons with a MyAnimeList entry: the whole of the studio and format shares
      SeasonsMatched: int64
      /// Watched seasons with a known airing or release year: the whole of the decade shares
      SeasonsWithYear: int64
      Studios: ShareRow list
      Decades: ShareRow list
      Genres: GenreRow list
      Formats: ShareRow list
      /// The top series that isn't a long runner
      Insignia: Insignia option
      LongRun: LongRun option
      /// None when no fully watched episode has an air date
      Seasons: SeasonsSection option
      Pace: Pace }

type DnaModel =
    { Username: string
      DisplayName: string
      FullyWatched: int64
      /// None below minimumEpisodes (the page-level "not enough data yet")
      Figures: Figures option
      /// What the page says about who can see it (feature 017)
      Access: Profiles.PageAccess }

// ---------------------------------------------------------------------------
// View (contracts/dna-page.md; design/frunchy_anime_dna_profile_u_username_dna, per design/REVIEW.md)
// ---------------------------------------------------------------------------

let private round1 (value: decimal) =
    Math.Round(value, 1, MidpointRounding.AwayFromZero)

let private percent (part: int64) (whole: int64) =
    if whole = 0L then 0m else round1 (100m * decimal part / decimal whole)

let private header (ctx: HttpContext) (t: Texts) (m: DnaModel) =
    let address = $"{ctx.Request.Scheme}://{ctx.Request.Host}/u/{m.Username}/dna"

    Fragment() {
        profileTop t ProfileTab.Dna m.Username m.DisplayName (Some(m.Access, address))

        div(class' = "space-y-2") {
            h1(class' = "text-headline-lg text-on-surface text-balance").data("dna-user", m.DisplayName) { t.DnaTitle }

            p(class' = "max-w-prose text-body-lg text-on-surface-variant") { t.DnaIntro }
        }
    }

let private totals (t: Texts) (f: Figures) =
    div(class' = "grid grid-cols-1 gap-4 sm:grid-cols-3").data("dna-totals", "") {
        div(class' = "fr-card flex flex-col gap-2 p-5 md:p-6") {
            p(class' = "fr-overline") { t.DnaWatchTime }

            div(class' = "flex items-baseline gap-1.5") {
                span(class' = "text-stat-counter text-primary").data("stat", "hours").data("value", inv f.Hours) {
                    f.Hours.ToString("N1", t.Culture)
                }

                span(class' = "text-headline-md text-on-surface-variant").attr("aria-hidden", "true") { "h" }
            }

            p(class' = "text-label-md font-normal text-on-surface-variant").data("stat", "days").data("value", inv f.Days) {
                t.DnaDaysOfLife f.Days
            }
        }

        metricTile t.StatEpisodes "episodes" (string f.Episodes) (f.Episodes.ToString("N0", t.Culture)) None
        metricTile t.DnaCoverageTile "coverage" (inv f.Coverage) (pctText t f.Coverage) (Some t.DnaCoverageCaption)
    }

let private insignia (t: Texts) (username: string) (i: Insignia) =
    (figureSection "dna-insignia" t.DnaInsigniaTitle None "lg:col-span-7")
        .data("insignia-series", i.SeriesId)
        .data("series", i.SeriesId)
        .data("episodes", string i.FullyWatched) {
        sectionHeading t.DnaInsigniaTitle

        div(class' = "flex flex-col gap-5 sm:flex-row") {
            match i.Poster with
            | Some url ->
                poster (Some url) "aspect-[2/3] w-32 shrink-0 rounded-md sm:w-40"
            | None -> Fragment()

            div(class' = "flex min-w-0 flex-col gap-3") {
                p(class' = "text-headline-lg text-on-surface text-balance") { seriesLink username i.SeriesId i.Title }

                div(class' = "flex flex-wrap gap-2") {
                    match i.Format with
                    | Some f -> pill PrimaryTone f
                    | None -> Fragment()

                    match i.Studio with
                    | Some s -> pill TertiaryTone s
                    | None -> Fragment()

                    match i.Season, i.Year with
                    | Some s, Some y -> pill Muted (t.DnaSeason s y)
                    | None, Some y -> pill Muted (string y)
                    | _ -> Fragment()
                }

                p(class' = "text-body-lg text-on-surface") { t.DnaInsigniaEpisodes i.FullyWatched i.MalEpisodes }
                p(class' = "text-body-md text-on-surface-variant") { t.DnaInsigniaHours i.Hours }
            }
        }
    }

/// A season's label as the page shows it ("Fall 2026" / "Otoño 2026")
let private seasonLabel (t: Texts) (name: string, year: int) = t.DnaSeason name year

/// One wide line, your marathon (the flagship above it is a tall card). With no flagship it takes the flagship's
/// slot (7 columns, the pace card keeps its 5); otherwise it spans the grid.
let private longRun (t: Texts) (username: string) (hasFlagship: bool) (l: LongRun) =
    let extra = if hasFlagship then "lg:col-span-12" else "lg:col-span-7"

    (figureSectionWith "dna-long-run" l.SeriesId t.DnaLongRunTitle None extra) {
        sectionHeading t.DnaLongRunTitle

        div(class' = "flex items-center gap-4") {
            poster l.Poster "aspect-[2/3] w-16 shrink-0 rounded-md sm:w-20"

            div(class' = "flex min-w-0 flex-col gap-1") {
                p(class' = "text-headline-md text-on-surface text-balance [overflow-wrap:anywhere]") {
                    seriesLink username l.SeriesId l.Title
                }

                p(class' = "flex flex-wrap items-baseline gap-x-2 text-body-md text-on-surface-variant") {
                    span().data("long-run-episodes", string l.FullyWatched) { t.DnaEpisodesPlain l.FullyWatched }
                    span().attr("aria-hidden", "true") { "·" }
                    span() { t.DnaInsigniaHours l.Hours }

                    match l.Span with
                    | Some(first, last) ->
                        span().attr("aria-hidden", "true") { "·" }

                        span().data("long-run-span", $"{Seasons.key (fst first) (snd first)}/{Seasons.key (fst last) (snd last)}") {
                            t.DnaLongRunSpan (seasonLabel t first) (seasonLabel t last)
                        }
                    | None -> Fragment()
                }
            }
        }
    }

let private paceChart (t: Texts) (pc: Pace) =
    let peak = max 1L (Array.max pc.PerDayCounts)

    div(class' = "flex h-20 items-end gap-px")
        .attr("role", "img")
        .attr("aria-label", t.DnaPaceChart pc.Episodes) {
        // oldest first, today on the right
        for i in 89 .. -1 .. 0 do
            let n = pc.PerDayCounts[i]
            let height = if n = 0L then 4m else max 8m (100m * decimal n / decimal peak)

            div(class' = (if n = 0L then "flex-1 rounded-sm bg-surface-container-highest" else "flex-1 rounded-sm bg-primary"))
                .attr("style", $"""height: {height.ToString("0.#", CultureInfo.InvariantCulture)}%%""")
    }

let private pace (t: Texts) (pc: Pace) =
    let el = figureSection "dna-pace" t.DnaPaceTitle None "lg:col-span-5"

    if pc.Episodes < 10L then
        el.data("not-enough", "") {
            sectionHeading t.DnaPaceTitle
            p(class' = "text-body-md text-on-surface-variant") { t.DnaPaceNotEnough }
        }
    else
        let steady = pc.ActiveWeeks >= 9

        el
            .data("value", inv pc.PerDay)
            .data("label", (if steady then "steady" else "bursts"))
            .data("episodes", string pc.Episodes)
            .data("weeks", string pc.ActiveWeeks) {
            sectionHeading t.DnaPaceTitle

            div(class' = "flex flex-wrap items-baseline gap-x-2 gap-y-1") {
                span(class' = "text-stat-counter text-on-surface") { pc.PerDay.ToString("N1", t.Culture) }
                span(class' = "text-body-md text-on-surface-variant") { t.DnaPaceUnit }
                pill (if steady then PrimaryTone else TertiaryTone) (if steady then t.DnaSteady else t.DnaBursts)
            }

            paceChart t pc
            p(class' = "text-label-md font-normal text-on-surface-variant") { t.DnaPaceDetail pc.Episodes pc.ActiveWeeks }
        }

let private studios (t: Texts) (f: Figures) =
    let coverage = percent f.SeasonsMatched f.SeasonsTotal
    let el = figureSection "dna-studio" t.DnaStudioTitle (Some coverage) "lg:col-span-6"

    match f.Studios with
    | [] ->
        el {
            sectionHeading t.DnaStudioTitle
            notEnough t
        }
    | first :: rest ->
        el {
            sectionHeading t.DnaStudioTitle

            div(class' = "space-y-1")
                .data("rank", "1")
                .data("name", first.Name)
                .data("share", inv first.Share)
                .data("studio-seasons", string first.Seasons) {
                p(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface text-balance") { first.Name }
                p(class' = "text-body-md text-primary") { $"{pctText t first.Share} · {t.DnaSeasonsCount first.Seasons}" }
            }

            for second in rest |> List.truncate 1 do
                div(class' = "flex items-baseline justify-between gap-4 border-t border-outline-variant/50 pt-4")
                    .data("rank", "2")
                    .data("name", second.Name)
                    .data("share", inv second.Share)
                    .data("studio-seasons", string second.Seasons) {
                    div() {
                        p(class' = "fr-overline") { t.DnaRunnerUp }
                        p(class' = "text-headline-md text-on-surface") { second.Name }
                    }

                    span(class' = "text-body-md text-on-surface-variant") {
                        $"{pctText t second.Share} · {t.DnaSeasonsCount second.Seasons}"
                    }
                }

            seasonCoverageNote (t.DnaCoverage f.SeasonsMatched f.SeasonsTotal) f.SeasonsMatched f.SeasonsTotal
        }

let private decades (t: Texts) (f: Figures) =
    let coverage = percent f.SeasonsWithYear f.SeasonsTotal
    let el = figureSection "dna-decade" t.DnaDecadeTitle (Some coverage) "lg:col-span-6"

    match f.Decades with
    | [] ->
        el {
            sectionHeading t.DnaDecadeTitle
            notEnough t
        }
    | top :: _ ->
        let name (d: ShareRow) = t.DnaDecade(int d.Name)

        el.data("name", top.Name).data("share", inv top.Share) {
            sectionHeading t.DnaDecadeTitle

            div(class' = "space-y-1") {
                p(class' = "text-headline-xl-mobile md:text-headline-xl text-on-surface") { name top }
                p(class' = "text-body-md text-primary") { pctText t top.Share }
            }

            div(class' = "space-y-3") {
                for d in f.Decades do
                    div().data("decade", d.Name).data("share", inv d.Share).data("decade-seasons", string d.Seasons) {
                        shareLine (name d) (Some(span() { t.DnaSeasonsCount d.Seasons })) d.Share t
                    }
            }

            seasonCoverageNote (t.DnaDecadeCoverage f.SeasonsWithYear f.SeasonsTotal) f.SeasonsWithYear f.SeasonsTotal
        }

let private genres (t: Texts) (username: string) (f: Figures) =
    let coverage = percent f.SeasonsMatched f.SeasonsTotal
    let el = figureSection "dna-genres" t.DnaGenresTitle (Some coverage) "lg:col-span-7"

    if f.Genres.IsEmpty then
        el {
            sectionHeading t.DnaGenresTitle
            notEnough t
        }
    else
        el {
            div(class' = "space-y-1") {
                sectionHeading t.DnaGenresTitle
                p(class' = "text-body-md text-on-surface-variant") { t.DnaGenresNote }
            }

            div(class' = "space-y-4") {
                for g in f.Genres do
                    let row =
                        div()
                            .data("genre", (if g.IsOthers then "others" else g.Name))
                            .data("share", inv g.Share)
                            .data("genre-seasons", string g.Seasons)

                    if g.IsOthers then
                        row { shareLine t.DnaOthers None g.Share t }
                    else
                        match g.Top with
                        | Some(seriesId, title) ->
                            row.data("top-series", seriesId) { shareLine g.Name (Some(span() { t.TopAnimePrefix; " "; seriesLink username seriesId title })) g.Share t }
                        | None -> row { shareLine g.Name None g.Share t }
            }

            seasonCoverageNote (t.DnaCoverage f.SeasonsMatched f.SeasonsTotal) f.SeasonsMatched f.SeasonsTotal
        }

let private formats (t: Texts) (f: Figures) =
    let coverage = percent f.SeasonsMatched f.SeasonsTotal
    let el = figureSection "dna-formats" t.DnaFormatsTitle (Some coverage) "lg:col-span-5"

    if f.Formats.IsEmpty then
        el {
            sectionHeading t.DnaFormatsTitle
            notEnough t
        }
    else
        el {
            sectionHeading t.DnaFormatsTitle

            div(class' = "space-y-4") {
                for x in f.Formats do
                    div().data("format", x.Name).data("share", inv x.Share).data("format-seasons", string x.Seasons) {
                        shareLine x.Name (Some(span() { t.DnaSeasonsCount x.Seasons })) x.Share t
                    }
            }

            seasonCoverageNote (t.DnaCoverage f.SeasonsMatched f.SeasonsTotal) f.SeasonsMatched f.SeasonsTotal
        }

/// A show's poster box in a tile: the quiet empty slot when there is no poster (nothing inside it)
let private tilePoster (url: string option) =
    // the micro border is always there, so the slot looks the same whether there is no poster or the image failed to load
    posterOn "bg-surface-container-lowest [border:var(--border-micro)]" url "aspect-[2/3] w-full rounded-md"

let private seasonShow (username: string) (t: Texts) (show: SeasonShow) =
    let row =
        li(class' = "flex min-w-0 flex-col gap-1.5")
            .data("season-show", show.SeriesId)
            .data("show-episodes", string show.Episodes)

    (if show.WhileAiring then row.data("while-airing", "") else row) {
        tilePoster show.Poster

        p(class' = "line-clamp-2 text-label-md [overflow-wrap:anywhere]") { seriesLink username show.SeriesId show.Title }

        p(class' = "flex items-center gap-1 text-label-md font-normal text-on-surface-variant") {
            if show.WhileAiring then
                iconWith "text-primary" Theme.Sensors
                span(class' = "sr-only") { t.DnaWhileAiring }

            span() { t.DnaShowEpisodes show.Episodes }
        }
    }

let private tileGrid (username: string) (t: Texts) (shows: SeasonShow list) =
    ul(class' = "grid gap-3 grid-cols-[repeat(auto-fill,minmax(6.5rem,1fr))]") {
        for show in shows do
            seasonShow username t show
    }

/// What is open by default shows at most `topSeasonTiles` tiles: the first ones, then the rest behind a nested
/// "Show all N shows" <details> (every show stays in the page)
let private cappedTiles (username: string) (t: Texts) (shows: SeasonShow list) =
    let first, rest = shows |> List.splitAt (min topSeasonTiles shows.Length)

    Fragment() {
        tileGrid username t first

        if not rest.IsEmpty then
            details() {
                summary(class' = $"{btnSm Ghost} -ml-3.5 cursor-pointer list-none [&::-webkit-details-marker]:hidden") {
                    t.DnaShowAll shows.Length
                }

                div(class' = "pt-3") { tileGrid username t rest }
            }
    }

/// The newest listed season starts open, the others closed: a native <details>, no script
let private airingSeason (username: string) (t: Texts) (isOpen: bool) (s: AiringSeason) =
    let row =
        details(class' = "border-t border-outline-variant/50")
            .data("season", Seasons.key s.Name s.Year)
            .data("season-shows", string s.Shows.Length)

    (if isOpen then row.attr("open", "") else row) {
        summary(class' = "flex cursor-pointer list-none flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-3 [&::-webkit-details-marker]:hidden") {
            h3(class' = "text-headline-md text-on-surface") { t.DnaSeason s.Name s.Year }

            span(class' = "flex items-center gap-1 text-label-md font-normal text-on-surface-variant") {
                t.DnaSeasonSummary s.Shows.Length s.Airing
                iconWith "fr-expand" Theme.ExpandMore
            }
        }

        // the open row is capped like the top block; a row the reader opens shows everything
        div(class' = "space-y-3 pb-4") {
            if isOpen then cappedTiles username t s.Shows else tileGrid username t s.Shows
        }
    }

/// The busiest season in full view: the first tiles, the rest behind "Show all"
let private topSeason (username: string) (t: Texts) (s: AiringSeason) =
    div(class' = "space-y-3").data("top-season", Seasons.key s.Name s.Year) {
        div(class' = "space-y-1") {
            h3(class' = "text-headline-lg text-primary") { t.DnaSeason s.Name s.Year }
            p(class' = "text-body-md text-on-surface-variant") { t.DnaTopSeasonLine s.Shows.Length s.Airing }
        }

        cappedTiles username t s.Shows
    }

let private seasonsSection (t: Texts) (username: string) (s: SeasonsSection) =
    (figureSectionWith "dna-seasons" (string s.Total) t.DnaSeasonsTitle None "lg:col-span-12") {
        div(class' = "space-y-1") {
            sectionHeading t.DnaSeasonsTitle
            p(class' = "text-body-md text-on-surface-variant") { t.DnaSeasonsIntro }
        }

        topSeason username t s.Top

        div() {
            for i, season in List.indexed s.Listed do
                airingSeason username t (i = 0) season
        }

        if s.Earlier > 0 then
            p(class' = "text-label-md font-normal text-on-surface-variant").data("earlier-seasons", string s.Earlier) {
                t.DnaEarlierSeasons s.Earlier
            }
    }

let private empty (t: Texts) (m: DnaModel) =
    div().data("dna-empty", "").data("value", string m.FullyWatched) {
        emptyState
            Theme.Fingerprint
            t.DnaEmptyTitle
            t.DnaEmptyBody
            (Some(
                div(class' = "flex flex-col items-center gap-4") {
                    p(class' = "text-label-md text-primary") { t.DnaEmptyCount m.FullyWatched }

                    a(href = "/#how-to-sync", class' = btn Primary) {
                        t.DnaEmptyAction
                        icon Theme.ArrowForward
                    }
                }
            ))
    }

let view (ctx: HttpContext) (t: Texts) (m: DnaModel) =
    div(class' = "space-y-8").data("dna", "") {
        header ctx t m

        match m.Figures with
        | None -> empty t m
        | Some f ->
            totals t f

            div(class' = "grid grid-cols-1 gap-4 lg:grid-cols-12") {
                match f.Insignia with
                | Some i -> insignia t m.Username i
                | None -> Fragment()

                // without a flagship the long-run strip takes its slot, so the pace card still sits beside it
                match f.Insignia, f.LongRun with
                | None, Some l -> longRun t m.Username false l
                | _ -> Fragment()

                pace t f.Pace

                match f.Insignia, f.LongRun with
                | Some _, Some l -> longRun t m.Username true l
                | _ -> Fragment()

                studios t f
                decades t f
                genres t m.Username f
                formats t f

                match f.Seasons with
                | Some s -> seasonsSection t m.Username s
                | None -> Fragment()
            }
    }
    |> Layout.page ctx t $"{t.DnaTitle} · {m.DisplayName}"

// ---------------------------------------------------------------------------
// Data access (research R4–R10). Minutes per episode: the whole duration when fully watched, else up
// to the playhead; episodes without a duration don't count. MyAnimeList figures use the deduplicated
// minutes: a dub and its original count once per episode number (keyed on the original season).
// Since feature 027 the taste figures (studio, decade, genre, format) count watched seasons instead of minutes, so a
// long show weighs one per airing season it was watched from (contracts/hooks.md in specs/027-seasons).
// ---------------------------------------------------------------------------

/// The "Your seasons" section from one row per (airing season number, show): the season with the most shows is the
/// top one (a tie goes to the newer), the `listedSeasons` newest are listed newest first, the rest are only counted.
/// None when there are no rows, that is, no fully watched episode has an air date.
let foldSeasons (rows: (int * SeasonShow) list) : SeasonsSection option =
    let seasons =
        rows
        |> List.groupBy fst
        |> List.map (fun (number, shows) ->
            let name, year = Seasons.ofOrder number

            let ordered = shows |> List.map snd |> List.sortBy (fun s -> -s.Episodes, s.Title, s.SeriesId)

            number,
            { Name = name
              Year = year
              Shows = ordered
              Airing = ordered |> List.filter _.WhileAiring |> List.length })
        |> List.sortByDescending fst

    match seasons with
    | [] -> None
    | _ ->
        let top = seasons |> List.maxBy (fun (number, s) -> s.Shows.Length, number) |> snd

        Some
            { Total = seasons.Length
              Top = top
              Listed = seasons |> List.truncate listedSeasons |> List.map snd
              Earlier = max 0 (seasons.Length - listedSeasons) }

/// `gate` runs right after the user lookup, before any other query: anything but `Show` is an `Error` and the handler
/// answers the 404 (or, for a Members profile and a signed-out viewer, the sign-in redirect; features 017 and 021)
let private tryLoad
    (db: DbConfig)
    (gate: Guid -> Task<Profiles.GateResult>)
    (username: string)
    (now: DateTimeOffset)
    : Task<Result<DnaModel, Profiles.GateResult>> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use userCmd = new NpgsqlCommand("SELECT id, display_name FROM users WHERE username = @username", connection)
        userCmd.Parameters.AddWithValue("username", username) |> ignore

        let! user =
            task {
                use! r = userCmd.ExecuteReaderAsync()
                return if r.Read() then Some(r.GetGuid 0, r.GetString 1) else None
            }

        // The visibility check, right after the lookup and before any history query
        let! gated =
            match user with
            | Some(userId, displayName) ->
                task {
                    match! gate userId with
                    | Profiles.Show access -> return Ok(userId, displayName, access)
                    | refused -> return Error refused
                }
            | None -> Task.FromResult(Error Profiles.NotFound)

        match gated with
        | Error refused -> return Error refused
        | Ok(userId, displayName, access) ->
            // One statement, one row shape: (kind, name, ms, n, x1, x2, x3, t1, t2, t3, t4, t5). The taste rows
            // ('studio', 'decade', 'genre', 'format') carry watched seasons in `ms`; 'seasons' carries the totals
            // they are based on; 'longrun' and 'season' feed the two sections of feature 027.
            use cmd =
                new NpgsqlCommand(
                    """
                    WITH w AS (
                        SELECT e.id, e.series_id, e.series_title, e.thumbnail_url, e.episode_number, e.season_id,
                               e.aired_at, w.fully_watched, w.watched_at,
                               CASE WHEN w.fully_watched THEN e.duration_ms::bigint
                                    ELSE least(w.playhead_s::bigint * 1000, e.duration_ms) END AS ms,
                               m.mal_id, coalesce(m.shared_from, e.season_id) AS original_season
                        FROM watch_events w
                        JOIN cr_episodes e ON e.id = w.cr_episode_id
                        LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                        WHERE w.user_id = @user_id AND e.duration_ms IS NOT NULL
                    ),
                    keyed AS (
                        SELECT w.*, CASE WHEN w.episode_number IS NULL OR w.original_season IS NULL THEN w.id
                                         ELSE w.original_season || '#' || w.episode_number END AS dedup_key
                        FROM w
                    ),
                    -- A dub and its original are one episode: it aired when its earliest version did, and it was
                    -- fully watched when any version was
                    timed AS (
                        SELECT k.*, min(k.aired_at) OVER (PARTITION BY k.dedup_key) AS group_aired,
                               bool_or(k.fully_watched) OVER (PARTITION BY k.dedup_key) AS group_full
                        FROM keyed k
                    ),
                    -- season_no = year * 4 + quarter (0 = winter), the UTC quarter of the group's air date
                    dna AS (
                        SELECT DISTINCT ON (t.dedup_key) t.*,
                               (extract(year FROM t.group_aired AT TIME ZONE 'UTC')::int * 4
                                + (extract(month FROM t.group_aired AT TIME ZONE 'UTC')::int - 1) / 3) AS season_no,
                               coalesce(bool_or(t.fully_watched AND t.watched_at >= t.group_aired
                                                AND t.watched_at <= t.group_aired + make_interval(days => @while_airing_days))
                                        OVER (PARTITION BY t.dedup_key), false) AS while_airing
                        FROM timed t
                        ORDER BY t.dedup_key, t.ms DESC, t.id
                    ),
                    titles AS (
                        SELECT d.series_id, coalesce(max(s.title), min(d.series_title), d.series_id) AS title
                        FROM dna d LEFT JOIN cr_series s ON s.id = d.series_id
                        GROUP BY d.series_id
                    ),
                    series_ms AS (SELECT d.series_id, sum(d.ms)::bigint AS ms FROM dna d GROUP BY d.series_id),
                    -- A long runner: a matched season of the series has more episodes than the limit on MyAnimeList,
                    -- or any known episode of it is numbered above it
                    long_runners AS (
                        SELECT e.series_id FROM cr_episodes e
                        WHERE e.episode_number > @long_runner_episodes AND e.series_id IN (SELECT series_id FROM w)
                        UNION
                        SELECT sea.series_id FROM cr_seasons sea
                        JOIN cr_season_matches m ON m.season_id = sea.id AND m.status = 'matched'
                        JOIN mal_anime a ON a.mal_id = m.mal_id
                        WHERE a.episodes > @long_runner_episodes AND sea.series_id IN (SELECT series_id FROM w)
                    ),
                    -- A watched season: a MyAnimeList entry in an airing season (NULL only when none of its watched episodes has a date) in
                    -- which the person fully watched an episode; unmatched ones count only in the totals
                    ws AS (
                        SELECT DISTINCT d.mal_id, d.season_no FROM dna d
                        WHERE d.group_full AND d.mal_id IS NOT NULL
                          AND (d.season_no IS NOT NULL
                               OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id = d.mal_id AND x.season_no IS NOT NULL))
                    ),
                    ws_unmatched AS (
                        SELECT DISTINCT coalesce(d.original_season, d.series_id) AS cr_season, d.season_no
                        FROM dna d
                        WHERE d.group_full AND d.mal_id IS NULL
                          AND (d.season_no IS NOT NULL
                               OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id IS NULL AND x.season_no IS NOT NULL
                                              AND coalesce(x.original_season, x.series_id) = coalesce(d.original_season, d.series_id)))
                    ),
                    series_seasons AS (
                        SELECT DISTINCT d.series_id, d.mal_id, d.season_no FROM dna d
                        WHERE d.group_full AND d.mal_id IS NOT NULL
                          AND (d.season_no IS NOT NULL
                               OR NOT EXISTS (SELECT 1 FROM dna x WHERE x.group_full AND x.mal_id = d.mal_id AND x.season_no IS NOT NULL))
                    ),
                    genre_count AS (
                        SELECT tg.name, count(*)::bigint AS seasons
                        FROM ws
                        JOIN mal_anime_tags at ON at.mal_id = ws.mal_id AND at.kind = 'genre'
                        JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                        GROUP BY tg.name
                    ),
                    genre_series AS (
                        SELECT tg.name, ss.series_id, count(*)::bigint AS seasons
                        FROM series_seasons ss
                        JOIN mal_anime_tags at ON at.mal_id = ss.mal_id AND at.kind = 'genre'
                        JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                        GROUP BY tg.name, ss.series_id
                    ),
                    genre_top AS (
                        SELECT DISTINCT ON (g.name) g.name, g.series_id, ti.title
                        FROM genre_series g
                        JOIN titles ti ON ti.series_id = g.series_id
                        JOIN series_ms sm ON sm.series_id = g.series_id
                        ORDER BY g.name, g.seasons DESC, sm.ms DESC, ti.title, g.series_id
                    ),
                    insignia AS (
                        SELECT d.series_id, ti.title, sum(d.ms)::bigint AS ms, count(*) FILTER (WHERE d.fully_watched) AS n
                        FROM dna d JOIN titles ti ON ti.series_id = d.series_id
                        WHERE d.series_id NOT IN (SELECT series_id FROM long_runners)
                        GROUP BY d.series_id, ti.title
                        ORDER BY sum(d.ms) DESC, ti.title, d.series_id
                        LIMIT 1
                    ),
                    insignia_season AS (
                        SELECT d.mal_id FROM dna d JOIN insignia i ON i.series_id = d.series_id
                        WHERE d.mal_id IS NOT NULL
                        GROUP BY d.mal_id ORDER BY sum(d.ms) DESC, d.mal_id LIMIT 1
                    ),
                    longrun AS (
                        SELECT d.series_id, ti.title, sum(d.ms)::bigint AS ms, count(*) FILTER (WHERE d.group_full) AS n,
                               min(d.season_no) FILTER (WHERE d.group_full) AS first_season,
                               max(d.season_no) FILTER (WHERE d.group_full) AS last_season
                        FROM dna d JOIN titles ti ON ti.series_id = d.series_id
                        WHERE d.series_id IN (SELECT series_id FROM long_runners)
                        GROUP BY d.series_id, ti.title
                        ORDER BY sum(d.ms) DESC, ti.title, d.series_id
                        LIMIT 1
                    ),
                    -- One picture per series: its main MyAnimeList entry's, else its latest watched thumbnail
                    best_mal AS (
                        SELECT DISTINCT ON (x.series_id) x.series_id, x.mal_id
                        FROM (SELECT d.series_id, d.mal_id, sum(d.ms) AS ms FROM dna d
                              WHERE d.mal_id IS NOT NULL GROUP BY d.series_id, d.mal_id) x
                        ORDER BY x.series_id, x.ms DESC, x.mal_id
                    ),
                    latest_thumb AS (
                        SELECT DISTINCT ON (d.series_id) d.series_id, d.thumbnail_url
                        FROM dna d WHERE d.thumbnail_url IS NOT NULL
                        ORDER BY d.series_id, d.watched_at DESC, d.id
                    ),
                    series_art AS (
                        SELECT s.series_id, coalesce(a.image_url, lt.thumbnail_url) AS art
                        FROM (SELECT DISTINCT series_id FROM dna) s
                        LEFT JOIN best_mal b ON b.series_id = s.series_id
                        LEFT JOIN mal_anime a ON a.mal_id = b.mal_id
                        LEFT JOIN latest_thumb lt ON lt.series_id = s.series_id
                    )
                    SELECT 'totals'::text, NULL::text, (SELECT coalesce(sum(ms), 0) FROM w)::bigint,
                           (SELECT count(*) FROM w WHERE fully_watched)::bigint,
                           coalesce(sum(d.ms), 0)::bigint,
                           coalesce(sum(d.ms) FILTER (WHERE d.mal_id IS NOT NULL), 0)::bigint,
                           NULL::bigint,
                           NULL::text, NULL::text, NULL::text, NULL::text, NULL::text
                    FROM dna d
                    UNION ALL
                    SELECT 'seasons', NULL, ((SELECT count(*) FROM ws) + (SELECT count(*) FROM ws_unmatched))::bigint,
                           (SELECT count(*) FROM ws)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    UNION ALL
                    (SELECT 'studio', tg.name, count(*)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                     FROM ws
                     JOIN mal_anime_tags at ON at.mal_id = ws.mal_id AND at.kind = 'studio'
                     JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                     GROUP BY tg.name ORDER BY count(*) DESC, tg.name LIMIT 2)
                    UNION ALL
                    -- a decade is the airing year of the watched season, else the entry's MyAnimeList year
                    SELECT 'decade', (coalesce(ws.season_no / 4, a.year, extract(year FROM a.aired_from)::int) / 10 * 10)::text,
                           count(*)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    FROM ws JOIN mal_anime a ON a.mal_id = ws.mal_id
                    WHERE coalesce(ws.season_no / 4, a.year, extract(year FROM a.aired_from)::int) IS NOT NULL
                    GROUP BY 2
                    UNION ALL
                    SELECT 'genre', g.name, g.seasons, NULL, NULL, NULL, NULL, gt.series_id, gt.title, NULL, NULL, NULL
                    FROM genre_count g JOIN genre_top gt ON gt.name = g.name
                    UNION ALL
                    SELECT 'format', a.type, count(*)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    FROM ws JOIN mal_anime a ON a.mal_id = ws.mal_id
                    WHERE a.type IS NOT NULL
                    GROUP BY a.type
                    UNION ALL
                    SELECT 'insignia', i.title, i.ms, i.n, a.episodes::bigint, a.year::bigint, NULL, i.series_id,
                           coalesce(a.image_url,
                                    (SELECT d.thumbnail_url FROM dna d
                                     WHERE d.series_id = i.series_id AND d.thumbnail_url IS NOT NULL
                                     ORDER BY d.watched_at DESC, d.id LIMIT 1)),
                           a.type,
                           (SELECT tg.name FROM mal_anime_tags at JOIN mal_tags tg ON tg.kind = at.kind AND tg.tag_id = at.tag_id
                            WHERE at.mal_id = a.mal_id AND at.kind = 'studio' ORDER BY at.position LIMIT 1),
                           a.season
                    FROM insignia i
                    LEFT JOIN insignia_season s ON true
                    LEFT JOIN mal_anime a ON a.mal_id = s.mal_id
                    UNION ALL
                    SELECT 'longrun', l.title, l.ms, l.n, l.first_season::bigint, l.last_season::bigint, NULL, l.series_id, art.art,
                           NULL, NULL, NULL
                    FROM longrun l LEFT JOIN series_art art ON art.series_id = l.series_id
                    UNION ALL
                    SELECT 'season', ti.title, d.season_no::bigint, count(*)::bigint,
                           (CASE WHEN bool_or(d.while_airing) THEN 1 ELSE 0 END)::bigint, NULL, NULL,
                           d.series_id, art.art, NULL, NULL, NULL
                    FROM dna d
                    JOIN titles ti ON ti.series_id = d.series_id
                    LEFT JOIN series_art art ON art.series_id = d.series_id
                    WHERE d.group_full AND d.season_no IS NOT NULL
                    GROUP BY d.season_no, d.series_id, ti.title, art.art
                    UNION ALL
                    SELECT 'day', NULL, NULL, count(*)::bigint,
                           floor(extract(epoch FROM (@now - d.watched_at)) / 86400)::bigint, NULL, NULL,
                           NULL, NULL, NULL, NULL, NULL
                    FROM dna d
                    WHERE d.fully_watched AND d.watched_at > @now - interval '90 days' AND d.watched_at <= @now
                    GROUP BY 5
                    """,
                    connection
                )

            cmd.Parameters.AddWithValue("user_id", userId) |> ignore
            cmd.Parameters.AddWithValue("now", now) |> ignore
            cmd.Parameters.AddWithValue("long_runner_episodes", Seasons.LongRunnerEpisodes) |> ignore
            cmd.Parameters.AddWithValue("while_airing_days", Seasons.WhileAiringDays) |> ignore

            let! rows =
                task {
                    use! r = cmd.ExecuteReaderAsync()
                    let rows = ResizeArray()

                    let text (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
                    let int64' (i: int) = if r.IsDBNull i then None else Some(r.GetInt64 i)

                    while r.Read() do
                        rows.Add(
                            r.GetString 0,
                            text 1,
                            int64' 2,
                            int64' 3,
                            (int64' 4, int64' 5, int64' 6),
                            (text 7, text 8, text 9, text 10, text 11)
                        )

                    return List.ofSeq rows
                }

            let ofKind kind =
                rows |> List.filter (fun (k, _, _, _, _, _) -> k = kind)

            let allMs, fullyWatched, dedupMs, matchedMs =
                match ofKind "totals" with
                | (_, _, Some all, Some n, (Some dedup, Some matched, _), _) :: _ -> all, n, dedup, matched
                | _ -> 0L, 0L, 0L, 0L

            let seasonsTotal, seasonsMatched =
                match ofKind "seasons" with
                | (_, _, Some total, Some matched, _, _) :: _ -> total, matched
                | _ -> 0L, 0L

            // Shares over a whole of watched seasons, ranked by seasons, then name
            let shares kind whole =
                ofKind kind
                |> List.choose (fun (_, name, seasons, _, _, _) ->
                    match name, seasons with
                    | Some n, Some seasons -> Some(n, seasons)
                    | _ -> None)
                |> List.sortBy (fun (n, seasons) -> -seasons, n)
                |> List.map (fun (n, seasons) -> { Name = n; Share = percent seasons whole; Seasons = seasons })

            let decades =
                shares "decade" (ofKind "decade" |> List.sumBy (fun (_, _, seasons, _, _, _) -> defaultArg seasons 0L))

            // Genres: shares over the sum of genre seasons, top 5 + "Others" (research R6)
            let genreRows =
                ofKind "genre"
                |> List.choose (fun (_, name, seasons, _, _, (top, title, _, _, _)) ->
                    match name, seasons with
                    | Some n, Some seasons -> Some(n, seasons, Option.map2 (fun a b -> a, b) top title)
                    | _ -> None)
                |> List.sortBy (fun (n, seasons, _) -> -seasons, n)

            let genreTotal = genreRows |> List.sumBy (fun (_, seasons, _) -> seasons)
            let top5 = genreRows |> List.truncate 5
            let others = genreTotal - (top5 |> List.sumBy (fun (_, seasons, _) -> seasons))

            let genres =
                [ for n, seasons, top in top5 ->
                      { Name = n
                        Share = percent seasons genreTotal
                        Seasons = seasons
                        Top = top
                        IsOthers = false }
                  if others > 0L then
                      yield
                          { Name = "others"
                            Share = percent others genreTotal
                            Seasons = others
                            Top = None
                            IsOthers = true } ]

            let insignia =
                match ofKind "insignia" with
                | (_, Some title, Some ms, Some n, (episodes, year, _), (Some seriesId, poster, format, studio, season)) :: _ ->
                    Some
                        { SeriesId = seriesId
                          Title = title
                          Poster = poster
                          Format = format
                          Studio = studio
                          Season = season
                          Year = year |> Option.map int
                          FullyWatched = n
                          MalEpisodes = episodes |> Option.map int |> Option.filter (fun e -> e > 0)
                          Hours = round1 (decimal ms / 3_600_000m) }
                | _ -> None

            let longRun =
                match ofKind "longrun" with
                | (_, Some title, Some ms, Some n, (first, last, _), (Some seriesId, poster, _, _, _)) :: _ when n >= int64 Seasons.LongRunMinimum ->
                    Some
                        { SeriesId = seriesId
                          Title = title
                          Poster = poster
                          FullyWatched = n
                          Hours = round1 (decimal ms / 3_600_000m)
                          Span =
                            Option.map2 (fun a b -> Seasons.ofOrder (int a), Seasons.ofOrder (int b)) first last }
                | _ -> None

            let seasonRows =
                [ for _, title, seasonNo, episodes, (whileAiring, _, _), (seriesId, poster, _, _, _) in ofKind "season" do
                      match title, seasonNo, episodes, seriesId with
                      | Some title, Some seasonNo, Some episodes, Some seriesId ->
                          yield
                              int seasonNo,
                              { SeriesId = seriesId
                                Title = title
                                Poster = poster
                                Episodes = episodes
                                WhileAiring = (whileAiring = Some 1L) }
                      | _ -> () ]

            let perDay = Array.zeroCreate<int64> 90

            for _, _, _, n, (index, _, _), _ in ofKind "day" do
                match n, index with
                | Some n, Some i when i >= 0L && i < 90L -> perDay[int i] <- perDay[int i] + n
                | _ -> ()

            let paceEpisodes = Array.sum perDay
            let activeWeeks = perDay |> Array.chunkBySize 7 |> Array.filter (Array.exists (fun n -> n > 0L)) |> Array.length
            let hours = decimal allMs / 3_600_000m

            return
                Ok
                    { Username = username
                      DisplayName = displayName
                      FullyWatched = fullyWatched
                      Access = access
                      Figures =
                        if fullyWatched < minimumEpisodes then
                            None
                        else
                            Some
                                { Hours = round1 hours
                                  Days = round1 (hours / 24m)
                                  Episodes = fullyWatched
                                  Coverage = percent matchedMs dedupMs
                                  SeasonsTotal = seasonsTotal
                                  SeasonsMatched = seasonsMatched
                                  SeasonsWithYear = decades |> List.sumBy _.Seasons
                                  Studios = shares "studio" seasonsMatched
                                  Decades = decades
                                  Genres = genres
                                  Formats = shares "format" seasonsMatched
                                  Insignia = insignia
                                  LongRun = longRun
                                  Seasons = foldSeasons seasonRows
                                  Pace =
                                    { Episodes = paceEpisodes
                                      PerDay = round1 (decimal paceEpisodes / 90m)
                                      ActiveWeeks = activeWeeks
                                      PerDayCounts = perDay } } }
    }

// ---------------------------------------------------------------------------
// Handler
// ---------------------------------------------------------------------------

let handler (rawUsername: string) : EndpointHandler =
    fun ctx ->
        task {
            let t = Texts.current ()
            let now = ctx.GetService<TimeProvider>().GetUtcNow()

            let! model =
                match Username.tryCreate rawUsername with
                | Some username ->
                    let db = ctx.GetService<DbConfig>()
                    // A hidden profile loads as no user at all: the unknown-user 404 below
                    tryLoad db (Profiles.gate ctx db) username now
                | None -> Task.FromResult(Error Profiles.NotFound)

            match model with
            | Ok m -> return! ctx.WriteHtmlView(view ctx t m)
            // A Members profile and a signed-out viewer: sign in, then back here (feature 021)
            | Error Profiles.SignInFirst -> return! WebAuth.redirectToSignInHere ctx
            | Error _ ->
                ctx.SetStatusCode 404
                return! ctx.WriteHtmlView(Layout.messagePage ctx t t.UserNotFoundTitle t.UserNotFoundBody)
        }

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/u/{%s}/dna" handler ]
