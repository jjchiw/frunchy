/// The Jikan HTTP client: pacing and short retries (research R4), response classification
/// (contracts/jikan-usage.md), and the two sources the matcher reads through (research R8).
module FrunchyBackend.Infrastructure.JikanClient

open System
open System.Net
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
open FrunchyBackend.Infrastructure.JikanModel
open FrunchyBackend.Infrastructure.MalMatching

[<Literal>]
let ClientName = "jikan"

type JikanSettings =
    { BaseUrl: string
      RequestsPerSecond: float
      RetryBaseDelay: TimeSpan
      AttemptTimeout: TimeSpan }

let readSettings (config: IConfiguration) =
    let section = config.GetSection "Frunchy:Jikan"

    let timeSpan (key: string) (fallback: TimeSpan) =
        match section[key] with
        | null
        | "" -> fallback
        | v -> TimeSpan.Parse v

    { BaseUrl =
        match section["BaseUrl"] with
        | null
        | "" -> "https://api.jikan.moe/v4/"
        | v -> if v.EndsWith "/" then v else v + "/"
      RequestsPerSecond = section.GetValue<float>("RequestsPerSecond", 1.0)
      RetryBaseDelay = timeSpan "RetryBaseDelay" (TimeSpan.FromSeconds 2.0)
      AttemptTimeout = timeSpan "AttemptTimeout" (TimeSpan.FromSeconds 15.0) }

/// Named client + resilience pipeline, outermost first: total timeout → retry → rate limiter →
/// attempt timeout. The limiter sits inside the retry, so retries are paced too (FR-010).
let register (config: IConfiguration) (services: IServiceCollection) =
    let settings = readSettings config
    let version = typeof<JikanSettings>.Assembly.GetName().Version

    services
        .AddHttpClient(
            ClientName,
            // An explicit Action: a bare lambda resolves to the typed-client overload (TClient = unit)
            Action<HttpClient>(fun client ->
                client.BaseAddress <- Uri settings.BaseUrl
                client.Timeout <- Threading.Timeout.InfiniteTimeSpan // the pipeline owns timeouts
                client.DefaultRequestHeaders.UserAgent.ParseAdd $"Frunchy-Backend/{version}")
        )
        .AddResilienceHandler(
            "jikan-pipeline",
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

/// How Frunchy reads a Jikan answer (contracts/jikan-usage.md)
type JikanResponse =
    | Ok of body: string * etag: string option
    | Unchanged
    | NotFound
    | Definite of string
    | Temporary of string

let private describe (status: int) (body: string) =
    match parseError body with
    | Some e ->
        let text = $"""{status} {defaultArg e.Type ""}: {defaultArg e.Message ""}"""
        if text.Length > 300 then text.Substring(0, 300) else text
    | None -> $"HTTP {status}"

/// One GET through the paced, retried pipeline
let get (factory: IHttpClientFactory) (path: string) (etag: string option) : Task<JikanResponse> =
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
                | 408
                | 429 -> Temporary(describe status body)
                | s when s >= 500 -> Temporary(describe status body)
                | s -> Definite(describe s body)
        with
        | :? HttpRequestException as ex -> return Temporary $"connection: {ex.Message}"
        | :? TimeoutRejectedException -> return Temporary "timeout"
        | :? RateLimiterRejectedException -> return Temporary "rate limiter queue full"
        | :? TaskCanceledException -> return Temporary "timeout"
    }

let searchPath (query: string) =
    $"anime?q={Uri.EscapeDataString query}&limit=10&sfw"

let fullPath (malId: int) = $"anime/{malId}/full"

/// Searches younger than this are reused instead of asked again
let searchReuse = TimeSpan.FromDays 30.0

/// The matcher's view of Jikan.
/// Live: stored data first (held anime, recent searches), otherwise one request, stored before use.
/// Stored: stored data only, "Unavailable not-held" for the rest (mal-rebuild, research R8).
type Source =
    | Live of factory: IHttpClientFactory * db: DbConfig * clock: TimeProvider
    | Stored of db: DbConfig

    member source.Lookup: Lookup =
        let db =
            match source with
            | Live(_, db, _) -> db
            | Stored db -> db

        let parsedSearch raw =
            match parseSearch raw with
            | Result.Ok results -> Got results
            | Result.Error e -> Unavailable $"unreadable search response: {e}"

        let parsedFull raw =
            match parseFull raw with
            | Result.Ok(a, _) -> Got a
            | Result.Error e -> Unavailable $"unreadable anime response: {e}"

        { Search =
            fun query ->
                task {
                    let! stored = MalCatalog.trySearch db query

                    match source, stored with
                    | Stored _, Some(raw, _) -> return parsedSearch raw
                    | Stored _, None -> return Unavailable "not-held"
                    | Live(_, _, clock), Some(raw, at) when clock.GetUtcNow() - at < searchReuse -> return parsedSearch raw
                    | Live(factory, _, clock), _ ->
                        match! get factory (searchPath query) None with
                        | Ok(body, _) ->
                            match parseSearch body with
                            | Result.Ok results ->
                                do! MalCatalog.saveSearch db query body (clock.GetUtcNow())
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
                    | _, Some(raw, _, _) -> return parsedFull raw
                    | Stored _, None -> return Unavailable "not-held"
                    | Live(factory, _, clock), None ->
                        match! get factory (fullPath malId) None with
                        | Ok(body, etag) ->
                            match parseFull body with
                            | Result.Ok(a, hash) ->
                                do! MalCatalog.saveAnime db a body hash etag (clock.GetUtcNow())
                                return Got a
                            | Result.Error e -> return Unavailable $"unreadable anime response: {e}"
                        | NotFound -> return Gone
                        | Unchanged -> return Unavailable "unexpected 304"
                        | Definite e
                        | Temporary e -> return Unavailable e
                } }
