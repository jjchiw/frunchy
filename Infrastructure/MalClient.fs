/// The MyAnimeList data client (feature 003): the provider (MyAnimeList's official API by default, Jikan
/// as an alternative), pacing and short retries (research R4), response classification
/// (contracts/mal-usage.md), and the two sources the matcher reads through (research R8).
module FrunchyBackend.Infrastructure.MalClient

open System
open System.Net.Http
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Http.Resilience
open Polly
open Polly.RateLimiting
open Polly.Timeout
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Infrastructure.MalModel
open FrunchyBackend.Infrastructure.MalMatching

[<Literal>]
let ClientName = "mal"

/// Where MyAnimeList data comes from. Stored responses remember their provider (`source` columns),
/// so data from either can be re-read by `mal-rebuild`.
type Provider =
    /// MyAnimeList's official API v2 (needs a client id: `Frunchy:Mal:ClientId`)
    | MalApi
    /// Jikan v4, the unofficial scraper
    | Jikan

    member p.Name =
        match p with
        | MalApi -> "mal-api"
        | Jikan -> "jikan"

    static member TryParse(name: string) =
        match name with
        | null
        | ""
        | "mal-api" -> Some MalApi
        | "jikan" -> Some Jikan
        | _ -> None

    static member OfSource(source: string) =
        Provider.TryParse source |> Option.defaultValue Jikan

    member p.DefaultBaseUrl =
        match p with
        | MalApi -> "https://api.myanimelist.net/v2/"
        | Jikan -> "https://api.jikan.moe/v4/"

    /// The search text actually sent. MyAnimeList refuses very long queries, so long titles are cut
    /// at their subtitle, then at a word boundary; the matcher compares full titles anyway.
    member p.Query(title: string) =
        let title = title.Trim()

        match p with
        | Jikan -> title
        | MalApi when title.Length <= 64 -> title
        | MalApi ->
            let cut = (cutSubtitle title).Trim()

            if cut.Length >= 3 && cut.Length <= 64 then
                cut
            else
                let words = title.Substring(0, 64)
                let space = words.LastIndexOf ' '
                (if space > 3 then words.Substring(0, space) else words).Trim()

    member p.SearchPath(query: string) =
        match p with
        | MalApi -> $"anime?q={Uri.EscapeDataString query}&limit=10&fields={MalApiModel.SearchFields}"
        | Jikan -> $"anime?q={Uri.EscapeDataString query}&limit=10&sfw"

    member p.DetailsPath(malId: int) =
        match p with
        | MalApi -> $"anime/{malId}?fields={MalApiModel.DetailFields}"
        | Jikan -> $"anime/{malId}/full"

    member p.ParseSearch(body: string) =
        match p with
        | MalApi -> MalApiModel.parseSearch body
        | Jikan -> JikanModel.parseSearch body

    member p.ParseDetails(body: string) =
        match p with
        | MalApi -> MalApiModel.parseFull body
        | Jikan -> JikanModel.parseFull body

    member p.ParseError(body: string) =
        match p with
        | MalApi -> MalApiModel.parseError body
        | Jikan -> JikanModel.parseError body

type MalSettings =
    { Provider: Provider
      BaseUrl: string
      /// MyAnimeList API client id (a secret: Bella or user secrets, never appsettings)
      ClientId: string option
      RequestsPerSecond: float
      RetryBaseDelay: TimeSpan
      AttemptTimeout: TimeSpan }

    /// Why requests can't be made, if they can't (the official API needs a client id)
    member s.Missing =
        match s.Provider, s.ClientId with
        | MalApi, None -> Some "Frunchy:Mal:ClientId is not set (Bella: Frunchy__Mal__ClientId, or dotnet user-secrets)"
        | _ -> None

let readSettings (config: IConfiguration) =
    let section = config.GetSection "Frunchy:Mal"

    let timeSpan (key: string) (fallback: TimeSpan) =
        match section[key] with
        | null
        | "" -> fallback
        | v -> TimeSpan.Parse v

    let provider =
        match Provider.TryParse section["Provider"] with
        | Some p -> p
        | None -> invalidOp $"""Frunchy:Mal:Provider must be "mal-api" or "jikan", not "{section["Provider"]}" """

    { Provider = provider
      BaseUrl =
        match section["BaseUrl"] with
        | null
        | "" -> provider.DefaultBaseUrl
        | v -> if v.EndsWith "/" then v else v + "/"
      ClientId =
        match section["ClientId"] with
        | null -> None
        | v when String.IsNullOrWhiteSpace v -> None
        | v -> Some(v.Trim())
      RequestsPerSecond = section.GetValue<float>("RequestsPerSecond", 1.0)
      RetryBaseDelay = timeSpan "RetryBaseDelay" (TimeSpan.FromSeconds 2.0)
      AttemptTimeout = timeSpan "AttemptTimeout" (TimeSpan.FromSeconds 15.0) }

/// Named client + resilience pipeline, outermost first: total timeout → retry → rate limiter →
/// attempt timeout. The limiter sits inside the retry, so retries are paced too (FR-010).
let register (config: IConfiguration) (services: IServiceCollection) =
    let settings = readSettings config
    let version = typeof<MalSettings>.Assembly.GetName().Version

    services
        .AddHttpClient(
            ClientName,
            // An explicit Action: a bare lambda resolves to the typed-client overload (TClient = unit)
            Action<HttpClient>(fun client ->
                client.BaseAddress <- Uri settings.BaseUrl
                client.Timeout <- Threading.Timeout.InfiniteTimeSpan // the pipeline owns timeouts
                client.DefaultRequestHeaders.UserAgent.ParseAdd $"Frunchy-Backend/{version}"

                settings.ClientId
                |> Option.iter (fun id -> client.DefaultRequestHeaders.Add("X-MAL-CLIENT-ID", id)))
        )
        .AddResilienceHandler(
            "mal-pipeline",
            fun (builder: ResiliencePipelineBuilder<HttpResponseMessage>) ->
                let retry =
                    HttpRetryStrategyOptions(
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = settings.RetryBaseDelay,
                        ShouldRetryAfterHeader = true
                    )

                let period = TimeSpan.FromSeconds(1.0 / max 0.001 settings.RequestsPerSecond)

                let limiter =
                    new TokenBucketRateLimiter(
                        TokenBucketRateLimiterOptions(
                            TokenLimit = 1,
                            TokensPerPeriod = 1,
                            ReplenishmentPeriod = period,
                            QueueLimit = 100,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        )
                    )

                builder
                    .AddTimeout(TimeSpan.FromSeconds 60.0)
                    .AddRetry(retry)
                    .AddRateLimiter(limiter)
                    .AddTimeout(settings.AttemptTimeout)
                |> ignore
        )
    |> ignore

    services

/// How Frunchy reads a provider's answer (contracts/mal-usage.md)
type MalResponse =
    | Ok of body: string * etag: string option
    | Unchanged
    | NotFound
    | Definite of string
    | Temporary of string

let private describe (provider: Provider) (status: int) (body: string) =
    match provider.ParseError body with
    | Some e ->
        let text = $"""{status} {defaultArg e.Type ""}: {defaultArg e.Message ""}""".TrimEnd(' ', ':')
        if text.Length > 300 then text.Substring(0, 300) else text
    | None -> $"HTTP {status}"

/// One GET through the paced, retried pipeline.
/// MyAnimeList answers 403 both for a bad client id and for its DoS protection, so 403 is temporary
/// (backed off, visible in `last_error`) rather than final.
let get (provider: Provider) (factory: IHttpClientFactory) (path: string) (etag: string option) : Task<MalResponse> =
    task {
        try
            let client = factory.CreateClient ClientName
            use request = new HttpRequestMessage(HttpMethod.Get, path)

            etag
            |> Option.iter (fun e -> request.Headers.TryAddWithoutValidation("If-None-Match", e) |> ignore)

            use! response = client.SendAsync request
            let! body = response.Content.ReadAsStringAsync()
            let status = int response.StatusCode

            let responseEtag =
                match response.Headers.ETag with
                | null -> None
                | e -> Some e.Tag

            return
                match status with
                | 200 -> Ok(body, responseEtag)
                | 304 -> Unchanged
                | 404 -> NotFound
                | 403 when provider = MalApi -> Temporary($"{describe provider status body} (bad client id, or MyAnimeList's DoS protection)")
                | 408
                | 429 -> Temporary(describe provider status body)
                | s when s >= 500 -> Temporary(describe provider status body)
                | s -> Definite(describe provider s body)
        with
        | :? HttpRequestException as ex -> return Temporary $"connection: {ex.Message}"
        | :? TimeoutRejectedException -> return Temporary "timeout"
        | :? RateLimiterRejectedException -> return Temporary "rate limiter queue full"
        | :? TaskCanceledException -> return Temporary "timeout"
    }

/// Searches younger than this are reused instead of asked again
let searchReuse = TimeSpan.FromDays 30.0

/// The matcher's view of MyAnimeList.
/// Live: stored data first (held anime, recent searches from the same provider), otherwise one
/// request, stored before use. Stored: stored data only, "Unavailable not-held" for the rest
/// (mal-rebuild, research R8). Stored bodies are read with the parser of the provider they came from.
type Source =
    | Live of factory: IHttpClientFactory * db: DbConfig * clock: TimeProvider * provider: Provider
    | Stored of db: DbConfig

    member source.Lookup: Lookup =
        let db =
            match source with
            | Live(_, db, _, _) -> db
            | Stored db -> db

        let parsedSearch (from: string) raw =
            match (Provider.OfSource from).ParseSearch raw with
            | Result.Ok results -> Got results
            | Result.Error e -> Unavailable $"unreadable search response: {e}"

        let parsedFull (from: string) raw =
            match (Provider.OfSource from).ParseDetails raw with
            | Result.Ok(a, _) -> Got a
            | Result.Error e -> Unavailable $"unreadable anime response: {e}"

        { Search =
            fun title ->
                task {
                    let query =
                        match source with
                        | Live(_, _, _, provider) -> provider.Query title
                        | Stored _ -> title

                    let! stored = MalCatalog.trySearch db query

                    match source, stored with
                    | Stored _, Some(raw, _, from) -> return parsedSearch from raw
                    | Stored _, None -> return Unavailable "not-held"
                    | Live(_, _, clock, provider), Some(raw, at, from) when from = provider.Name && clock.GetUtcNow() - at < searchReuse ->
                        return parsedSearch from raw
                    | Live(_, _, _, MalApi), _ when query.Length < 3 -> return Got [] // MyAnimeList needs 3 characters
                    | Live(factory, _, clock, provider), _ ->
                        match! get provider factory (provider.SearchPath query) None with
                        | Ok(body, _) ->
                            match provider.ParseSearch body with
                            | Result.Ok results ->
                                do! MalCatalog.saveSearch db query body provider.Name (clock.GetUtcNow())
                                return Got results
                            | Result.Error e -> return Unavailable $"unreadable search response: {e}"
                        | NotFound -> return Gone
                        | Unchanged -> return Unavailable "unexpected 304 on search"
                        | Definite e
                        | Temporary e -> return Unavailable e
                }
          Full =
            fun malId ->
                task {
                    let! stored = MalCatalog.tryAnime db malId

                    match source, stored with
                    | _, Some(raw, _, _, from) -> return parsedFull from raw
                    | Stored _, None -> return Unavailable "not-held"
                    | Live(factory, _, clock, provider), None ->
                        match! get provider factory (provider.DetailsPath malId) None with
                        | Ok(body, etag) ->
                            match provider.ParseDetails body with
                            | Result.Ok(a, hash) ->
                                do! MalCatalog.saveAnime db a body hash etag provider.Name (clock.GetUtcNow())
                                return Got a
                            | Result.Error e -> return Unavailable $"unreadable anime response: {e}"
                        | NotFound -> return Gone
                        | Unchanged -> return Unavailable "unexpected 304"
                        | Definite e
                        | Temporary e -> return Unavailable e
                } }
