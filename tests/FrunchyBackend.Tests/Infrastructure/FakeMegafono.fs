/// A stand-in for Megáfono's internal API (feature 015, research R9), the way `FakeMal` stands in for MyAnimeList:
/// it replaces the "megafono" client's primary handler, records every request (method, path, query, headers, body)
/// and answers with the contract's defaults unless a test scripts something else. No test needs a real Megáfono.
///
/// Defaults: publish gives 201 with a generated id and `objectUrl`, and the same key again gives the same id with 200
/// (a different body under the same key is 409 `conflict`); PUT of an object is 200, DELETE of an object, DELETE and PUT
/// of an actor are 202; blocks are kept in memory (201 new, 200 repeat, 204 lifted, 404 unknown); anything else is 404.
module FrunchyBackend.Tests.Infrastructure.FakeMegafono

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open FrunchyBackend.Infrastructure

/// The settings every federated test app runs with
let Domain = "frunchy.test"
let Token = "megafono-test-token"
let WebhookSecret = "megafono-test-webhook-secret"

let federationSettings =
    [ Federation.EnabledKey, "true"
      Federation.DomainKey, Domain
      Federation.UrlKey, "http://megafono.test"
      Federation.TokenKey, Token
      Federation.WebhookSecretKey, WebhookSecret ]

/// One request the fake received
type Recorded =
    { Method: string
      Path: string
      Query: string
      /// The `Idempotency-Key` header
      IdempotencyKey: string option
      /// The `Authorization` header
      Authorization: string option
      Body: string }

    /// "POST /internal/actors/abc/objects"
    member r.Key = $"{r.Method} {r.Path}"

    /// The body as JSON (an empty body is an empty object)
    member r.Json = JsonDocument.Parse(if r.Body = "" then "{}" else r.Body).RootElement

type Reply =
    /// A status with the standard `{ error, message }` body for it (2xx: an empty body)
    | Status of int
    /// A status with this exact body
    | Body of int * string
    /// A transport failure
    | Throw of exn
    /// Wait, then answer with the next reply
    | Delay of TimeSpan * Reply

let private errorCodeOf (status: int) =
    match status with
    | 400 -> "invalid_request"
    | 401 -> "unauthorized"
    | 404 -> "not_found"
    | 409 -> "conflict"
    | 410 -> "gone"
    | 502 -> "remote_unreachable"
    | 503 -> "app_unavailable"
    | _ -> "server_error"

let private errorBody (status: int) =
    $$"""{"error":"{{errorCodeOf status}}","message":"fake {{status}}"}"""

/// "POST /internal/actors/*/objects" to a regex ("*" matches one path segment); a key without "*" is exact
let private patternOf (key: string) =
    Regex("^" + Regex.Escape(key).Replace("\\*", "[^/]+") + "$")

type FakeMegafonoHandler() =
    inherit HttpMessageHandler()

    let requests = ConcurrentQueue<Recorded>()
    let scripts = List<Regex * ConcurrentQueue<Reply> * (Reply option) ref>()
    let failNext = ref 0
    let gate = obj ()
    let mutable counter = 0

    // Idempotency: (actor id, key) -> (body, answer)
    let published = Dictionary<string, string * string>()
    // Blocks per scope ("" = the instance, else the user id), newest first, as JSON object strings
    let blocks = Dictionary<string, List<string * string>>()

    let next () =
        lock gate (fun () ->
            counter <- counter + 1
            counter)

    member _.Requests = List.ofSeq requests

    /// The requests whose "METHOD path" matches (exact, or with "*" for one segment)
    member _.RequestsTo(key: string) =
        let pattern = patternOf key
        requests |> Seq.filter (fun r -> pattern.IsMatch r.Key) |> List.ofSeq

    member this.Count(key: string) = (this.RequestsTo key).Length

    member _.Clear() =
        requests.Clear()

    /// Replies for a "METHOD path" key (with "*" for one segment), in order; the last one repeats
    member _.Route(key: string, replies: Reply list) =
        lock gate (fun () ->
            scripts.RemoveAll(fun (p, _, _) -> p.ToString() = (patternOf key).ToString()) |> ignore
            scripts.Add((patternOf key, ConcurrentQueue(replies), ref None)))

    /// The next `count` calls (to anything) answer 503, then the fake goes back to normal
    member _.FailNext(count: int) =
        lock gate (fun () -> failNext.Value <- count)

    /// The blocks the fake holds for a user id ("" for the instance), as (actor-or-domain, kind)
    member _.BlocksOf(scope: string) =
        lock gate (fun () ->
            match blocks.TryGetValue scope with
            | true, items -> items |> Seq.map fst |> List.ofSeq
            | _ -> [])

    /// Put a block in the fake as if Megáfono already held it
    member _.SeedBlock(scope: string, target: string, isDomain: bool) =
        lock gate (fun () ->
            let items =
                match blocks.TryGetValue scope with
                | true, items -> items
                | _ ->
                    let created = List<string * string>()
                    blocks[scope] <- created
                    created

            let json =
                if isDomain then
                    $$"""{"actor":null,"domain":"{{target}}","createdAt":"2026-10-15T09:30:00Z"}"""
                else
                    $$"""{"actor":"{{target}}","domain":null,"createdAt":"2026-10-15T09:30:00Z"}"""

            items.Insert(0, (target, json)))

    member private _.Scripted(key: string) : Reply option =
        lock gate (fun () ->
            scripts
            |> Seq.tryFind (fun (p, _, _) -> p.IsMatch key)
            |> Option.bind (fun (_, queue, last) ->
                match queue.TryDequeue() with
                | true, reply ->
                    last.Value <- Some reply
                    Some reply
                | _ -> last.Value))

    member private this.Default(request: Recorded) : Reply =
        let segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        let queryOf (name: string) = Web.HttpUtility.ParseQueryString(request.Query)[name]

        let publishAnswer (id: string) =
            Body(
                (if request.Method = "POST" then 201 else 200),
                $$"""{"id":"{{id}}","objectUrl":"https://{{Domain}}/ap/objects/{{id}}","deliveriesUrl":"/internal/objects/{{id}}/deliveries"}"""
            )

        // The block calls, user (segments: internal actors {id} blocks) and instance (internal blocks)
        let blockCall (scope: string) =
            lock gate (fun () ->
                let items =
                    match blocks.TryGetValue scope with
                    | true, items -> items
                    | _ ->
                        let created = List<string * string>()
                        blocks[scope] <- created
                        created

                match request.Method with
                | "GET" -> Body(200, "{\"items\":[" + String.Join(",", items |> Seq.map snd) + "]}")
                | "POST" ->
                    let json = request.Json

                    let target, isDomain =
                        match json.TryGetProperty "domain", json.TryGetProperty "actor" with
                        | (true, d), _ when d.ValueKind = JsonValueKind.String -> d.GetString(), true
                        | _, (true, a) when a.ValueKind = JsonValueKind.String -> a.GetString(), false
                        | _ -> "", false

                    if target = "" then
                        Status 400
                    elif items |> Seq.exists (fun (t, _) -> t = target) then
                        Body(200, items |> Seq.find (fun (t, _) -> t = target) |> snd)
                    else
                        let blockJson =
                            if isDomain then
                                $$"""{"actor":null,"domain":"{{target}}","createdAt":"2026-10-15T09:30:00Z"}"""
                            else
                                $$"""{"actor":"{{target}}","domain":null,"createdAt":"2026-10-15T09:30:00Z"}"""

                        items.Insert(0, (target, blockJson))
                        Body(201, blockJson)
                | "DELETE" ->
                    let target =
                        match queryOf "domain", queryOf "actor" with
                        | null, null -> ""
                        | null, a -> a
                        | d, _ -> d

                    if target = "" then Status 400
                    elif items.RemoveAll(fun (t, _) -> t = target) > 0 then Status 204
                    else Status 404
                | _ -> Status 404)

        match request.Method, List.ofArray segments with
        | "POST", [ "internal"; "actors"; actor; "objects" ] ->
            let key = defaultArg request.IdempotencyKey ""

            lock gate (fun () ->
                let slot = $"{actor}|{key}"

                match published.TryGetValue slot with
                | true, (body, id) when key <> "" ->
                    if body = request.Body then publishAnswer id |> (function Body(_, b) -> Body(200, b) | other -> other)
                    else Status 409
                | _ ->
                    let id = $"obj-{next ()}"
                    if key <> "" then published[slot] <- (request.Body, id)
                    publishAnswer id)
        | "PUT", [ "internal"; "objects"; id ] -> publishAnswer id
        | "DELETE", [ "internal"; "objects"; _ ] -> Status 202
        | "DELETE", [ "internal"; "actors"; _ ] -> Status 202
        | "PUT", [ "internal"; "actors"; _ ] -> Status 202
        | _, [ "internal"; "actors"; actor; "blocks" ] -> blockCall actor
        | _, [ "internal"; "blocks" ] -> blockCall ""
        | _ -> Status 404

    override this.SendAsync(request, cancellationToken) =
        task {
            let! body =
                match request.Content with
                | null -> Task.FromResult ""
                | content -> content.ReadAsStringAsync(cancellationToken)

            let header (name: string) =
                match request.Headers.TryGetValues name with
                | true, values -> Some(String.Join(",", values))
                | _ -> None

            let recorded =
                { Method = request.Method.Method
                  Path = request.RequestUri.AbsolutePath
                  Query = request.RequestUri.Query
                  IdempotencyKey = header "Idempotency-Key"
                  Authorization = header "Authorization"
                  Body = body }

            requests.Enqueue recorded

            let respond (status: int) (text: string) =
                let response = new HttpResponseMessage(enum<HttpStatusCode> status)
                response.Content <- new StringContent(text, Encoding.UTF8, "application/json")
                response.RequestMessage <- request
                response

            let rec reply (r: Reply) : Task<HttpResponseMessage> =
                task {
                    match r with
                    | Status s when s >= 200 && s < 300 -> return respond s ""
                    | Status s -> return respond s (errorBody s)
                    | Body(s, text) -> return respond s text
                    | Throw ex -> return raise ex
                    | Delay(d, next) ->
                        do! Task.Delay(d, cancellationToken)
                        return! reply next
                }

            let failing =
                lock gate (fun () ->
                    if failNext.Value > 0 then
                        failNext.Value <- failNext.Value - 1
                        true
                    else
                        false)

            if failing then
                return! reply (Status 503)
            else
                match this.Scripted recorded.Key with
                | Some scripted -> return! reply scripted
                | None -> return! reply (this.Default recorded)
        }

/// Use `fake` as the "megafono" client's primary handler
let useHandler (fake: FakeMegafonoHandler) (services: IServiceCollection) =
    services
        .AddHttpClient(MegafonoClient.ClientName)
        .ConfigurePrimaryHttpMessageHandler(fun () -> fake :> HttpMessageHandler)
        .SetHandlerLifetime(Threading.Timeout.InfiniteTimeSpan)
    |> ignore
