/// A stand-in for the MyAnimeList data provider (feature 003, research R9): serves recorded real
/// responses from JsonSamples/ (manifest.json) or scripted replies, and logs every request.
/// Understands both providers' paths (MyAnimeList API: anime?q=…, anime/{id}; Jikan: anime/{id}/full).
/// Tests never call a real service.
module FrunchyBackend.Tests.Infrastructure.FakeMal

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open FrunchyBackend.Infrastructure

type Reply =
    /// A recorded body (path relative to JsonSamples/jikan/)
    | File of string
    | Body of string
    /// A status code with an error body
    | Status of int
    /// A 429 with a Retry-After header (seconds)
    | RetryAfter of int
    | Delay of TimeSpan * Reply
    | Throw of exn

/// Recorded responses per provider: JsonSamples/mal/ (MyAnimeList API) and JsonSamples/jikan/
let fixturesDirOf (provider: string) =
    Path.Combine(AppContext.BaseDirectory, "JsonSamples", provider)

let fixturesDir = fixturesDirOf "jikan"

let readFixture (name: string) = File.ReadAllText(Path.Combine(fixturesDir, name))

/// MyAnimeList's error shape ({ error, message }); `status` kept in the message for assertions
let private errorBody (status: int) =
    $$"""{"error":"fake_{{status}}","message":"fake {{status}}"}"""

let emptySearch = """{"data":[],"paging":{}}"""

/// Route key of a request: "search:<q>" or "details:<id>"
let keyOf (uri: Uri) =
    let path = uri.AbsolutePath.TrimEnd('/')
    let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

    match segments |> Array.rev |> List.ofArray with
    | "full" :: id :: "anime" :: _ -> $"details:{id}"
    | id :: "anime" :: _ when fst (Int32.TryParse id) -> $"details:{id}"
    | "anime" :: _ ->
        let query = Web.HttpUtility.ParseQueryString uri.Query
        $"""search:{query["q"]}"""
    | _ -> "other:" + path

type FakeMalHandler() =
    inherit HttpMessageHandler()

    let requests = ConcurrentQueue<string>()
    let ifNoneMatch = ConcurrentQueue<string * string>()
    let routes = ConcurrentDictionary<string, ConcurrentQueue<Reply>>()
    let lastReply = ConcurrentDictionary<string, Reply>()

    /// Every request received, as "search:<q>" / "details:<id>", in order
    member _.Requests = List.ofSeq requests

    /// (key, If-None-Match) of every conditional request
    member _.Conditional = List.ofSeq ifNoneMatch

    member _.Count(key: string) =
        requests |> Seq.filter ((=) key) |> Seq.length

    member _.Clear() = requests.Clear()

    /// Replies for a key, in order; the last one repeats
    member _.Route(key: string, replies: Reply list) =
        routes[key] <- ConcurrentQueue(replies)
        lastReply.TryRemove key |> ignore

    /// Load the recorded responses of a provider folder ("mal" or "jikan", with its manifest.json)
    member this.LoadRecorded(provider: string) =
        let dir = fixturesDirOf provider
        use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))

        for entry in doc.RootElement.EnumerateObject() do
            let v = entry.Value

            match v.TryGetProperty "file" with
            | true, f -> this.Route(entry.Name, [ Body(File.ReadAllText(Path.Combine(dir, f.GetString()))) ])
            | _ ->
                let status = v.GetProperty("status").GetInt32()

                let body =
                    match v.TryGetProperty "body" with
                    | true, b -> b.GetString()
                    | _ -> errorBody status

                if status = 200 then this.Route(entry.Name, [ Body body ])
                elif status = 404 then this.Route(entry.Name, [ Status 404 ])
                // A temporary failure while recording isn't replayed: tests decide failures themselves
                else ()

        this

    member private _.Next(key: string) : Reply option =
        match routes.TryGetValue key with
        | true, queue ->
            match queue.TryDequeue() with
            | true, reply ->
                lastReply[key] <- reply
                Some reply
            | _ ->
                match lastReply.TryGetValue key with
                | true, reply -> Some reply
                | _ -> None
        | _ -> None

    override this.SendAsync(request, cancellationToken) =
        task {
            let key = keyOf request.RequestUri
            requests.Enqueue key

            match request.Headers.TryGetValues "If-None-Match" with
            | true, values -> ifNoneMatch.Enqueue(key, String.Join(",", values))
            | _ -> ()

            let respond (status: int) (body: string) =
                let response = new HttpResponseMessage(enum<HttpStatusCode> status)
                response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                response.RequestMessage <- request
                response

            let rec reply (r: Reply) : Task<HttpResponseMessage> =
                task {
                    match r with
                    | File name -> return respond 200 (readFixture name)
                    | Body body -> return respond 200 body
                    | Status s -> return respond s (errorBody s)
                    | RetryAfter seconds ->
                        let response = respond 429 (errorBody 429)
                        response.Headers.RetryAfter <- Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(float seconds))
                        return response
                    | Delay(d, next) ->
                        do! Task.Delay(d, cancellationToken)
                        return! reply next
                    | Throw ex -> return raise ex
                }

            match this.Next key with
            | Some r -> return! reply r
            | None when key.StartsWith "search:" -> return respond 200 emptySearch
            | None -> return respond 404 (errorBody 404)
        }

/// Settings that make pacing and retries instant in tests (the default provider, a fake client id)
let fastSettings =
    [ "Frunchy:Mal:Provider", "mal-api"
      "Frunchy:Mal:ClientId", "test-client-id"
      "Frunchy:Mal:BaseUrl", "http://mal.test/v2/"
      "Frunchy:Mal:RequestsPerSecond", "1000"
      "Frunchy:Mal:RetryBaseDelay", "00:00:00.001"
      "Frunchy:Mal:AttemptTimeout", "00:00:02" ]

/// Use `fake` as the "jikan" client's primary handler
let useHandler (fake: FakeMalHandler) (services: IServiceCollection) =
    services
        .AddHttpClient(MalClient.ClientName)
        .ConfigurePrimaryHttpMessageHandler(fun () -> fake :> HttpMessageHandler)
        .SetHandlerLifetime(Threading.Timeout.InfiniteTimeSpan)
    |> ignore

/// An IHttpClientFactory wired like the app's (same pipeline), backed by `fake`
let factory (fake: FakeMalHandler) (extra: (string * string) list) : IHttpClientFactory =
    let config =
        ConfigurationBuilder().AddInMemoryCollection(dict (fastSettings @ extra)).Build() :> IConfiguration

    let services = ServiceCollection()
    MalClient.register config services |> ignore
    useHandler fake services
    services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>()
