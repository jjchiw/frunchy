/// A stand-in for Jikan (feature 003, research R9): serves recorded real responses from
/// JsonSamples/jikan/ (manifest.json) or scripted replies, and logs every request.
/// Tests never call the real service.
module FrunchyBackend.Tests.Infrastructure.FakeJikan

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
    /// A status code with Jikan's error body
    | Status of int
    /// A 429 with a Retry-After header (seconds)
    | RetryAfter of int
    | Delay of TimeSpan * Reply
    | Throw of exn

let fixturesDir =
    Path.Combine(AppContext.BaseDirectory, "JsonSamples", "jikan")

let readFixture (name: string) = File.ReadAllText(Path.Combine(fixturesDir, name))

let private errorBody (status: int) =
    $$"""{"status":{{status}},"type":"FakeException","message":"fake {{status}}","error":null}"""

let emptySearch = """{"data":[],"pagination":{"has_next_page":false}}"""

/// Route key of a request: "search:<q>" or "full:<id>"
let keyOf (uri: Uri) =
    let path = uri.AbsolutePath.TrimEnd('/')
    let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

    match segments |> Array.rev |> List.ofArray with
    | "full" :: id :: "anime" :: _ -> $"full:{id}"
    | "anime" :: _ ->
        let query = Web.HttpUtility.ParseQueryString uri.Query
        $"""search:{query["q"]}"""
    | _ -> "other:" + path

type FakeJikanHandler() =
    inherit HttpMessageHandler()

    let requests = ConcurrentQueue<string>()
    let ifNoneMatch = ConcurrentQueue<string * string>()
    let routes = ConcurrentDictionary<string, ConcurrentQueue<Reply>>()
    let lastReply = ConcurrentDictionary<string, Reply>()

    /// Every request received, as "search:<q>" / "full:<id>", in order
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

    /// Load the recorded responses (manifest.json)
    member this.LoadRecorded() =
        use doc = JsonDocument.Parse(readFixture "manifest.json")

        for entry in doc.RootElement.EnumerateObject() do
            let v = entry.Value

            match v.TryGetProperty "file" with
            | true, f -> this.Route(entry.Name, [ File(f.GetString()) ])
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

/// Settings that make pacing and retries instant in tests
let fastSettings =
    [ "Frunchy:Jikan:BaseUrl", "http://jikan.test/v4/"
      "Frunchy:Jikan:RequestsPerSecond", "1000"
      "Frunchy:Jikan:RetryBaseDelay", "00:00:00.001"
      "Frunchy:Jikan:AttemptTimeout", "00:00:02" ]

/// Use `fake` as the "jikan" client's primary handler
let useHandler (fake: FakeJikanHandler) (services: IServiceCollection) =
    services
        .AddHttpClient(JikanClient.ClientName)
        .ConfigurePrimaryHttpMessageHandler(fun () -> fake :> HttpMessageHandler)
        .SetHandlerLifetime(Threading.Timeout.InfiniteTimeSpan)
    |> ignore

/// An IHttpClientFactory wired like the app's (same pipeline), backed by `fake`
let factory (fake: FakeJikanHandler) (extra: (string * string) list) : IHttpClientFactory =
    let config =
        ConfigurationBuilder().AddInMemoryCollection(dict (fastSettings @ extra)).Build() :> IConfiguration

    let services = ServiceCollection()
    JikanClient.register config services |> ignore
    useHandler fake services
    services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>()
