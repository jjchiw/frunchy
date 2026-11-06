/// A stand-in for other servers' NodeInfo (feature 028), the way `FakeMal` stands in for MyAnimeList: it replaces
/// the "nodeinfo" client's primary handler, routes by host and records every request. No test touches a real
/// network. An unknown host is down (a transport failure).
module FrunchyBackend.Tests.Infrastructure.FakeNodeInfo

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Http
open Microsoft.Extensions.Options
open FrunchyBackend.Infrastructure

/// What a host answers
type Behavior =
    /// A Frunchy instance: discovery on the same host, `software.name` = `frunchy`
    | Frunchy
    /// Another server (`mastodon`, `gotosocial`...)
    | Software of string
    /// A transport failure
    | Down
    /// A non-2xx answer
    | Status of int
    /// The discovery link points at another host
    | OffHost of string
    /// The discovery document has no 2.x schema link
    | NoSchemaLink
    /// An answer over the 256 KB cap
    | Oversize
    /// A redirect to another host's NodeInfo (never followed)
    | Redirect of string
    /// An answer that is not JSON
    | Garbage

type FakeNodeInfoHandler() =
    inherit HttpMessageHandler()

    let requests = ConcurrentQueue<string>()
    let hosts = ConcurrentDictionary<string, Behavior>()

    /// Every URL asked, in order
    member _.Requests = List.ofSeq requests

    member _.Clear() = requests.Clear()

    /// What `host` answers from now on
    member _.Set(host: string, behavior: Behavior) = hosts[host.ToLowerInvariant()] <- behavior

    override _.SendAsync(request, cancellationToken) =
        task {
            let uri = request.RequestUri
            requests.Enqueue(uri.ToString())

            let respond (status: int) (body: string) =
                let response = new HttpResponseMessage(enum<HttpStatusCode> status)
                response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                response.RequestMessage <- request
                response

            let discovery (host: string) =
                $$"""{"links":[{"rel":"http://nodeinfo.diaspora.software/ns/schema/2.0","href":"https://{{host}}/nodeinfo/2.0"}]}"""

            let document (name: string) =
                $$"""{"version":"2.0","software":{"name":"{{name}}","version":"0.3.0"},"protocols":["activitypub"]}"""

            match hosts.TryGetValue(uri.Host.ToLowerInvariant()) with
            | false, _ -> return raise (HttpRequestException "fake: no such host")
            | true, behavior ->
                let isDiscovery = uri.AbsolutePath = "/.well-known/nodeinfo"

                match behavior with
                | Down -> return raise (HttpRequestException "fake: down")
                | Status s -> return respond s "{}"
                | Garbage -> return respond 200 "<html>not json</html>"
                | Redirect target ->
                    let response = respond 302 ""
                    response.Headers.Location <- Uri $"https://{target}{uri.AbsolutePath}"
                    return response
                | Oversize -> return respond 200 (String('x', 300000))
                | OffHost other -> return respond 200 (if isDiscovery then discovery other else document "frunchy")
                | NoSchemaLink -> return respond 200 (if isDiscovery then """{"links":[]}""" else document "frunchy")
                | Frunchy -> return respond 200 (if isDiscovery then discovery uri.Host else document "Frunchy")
                | Software name -> return respond 200 (if isDiscovery then discovery uri.Host else document name)
        }

/// Use `fake` as the "nodeinfo" client's primary handler. The app registers its own guarded handler for the client
/// after the tests' customization runs, so the fake goes in as a post-configuration, which runs last and wins.
let useHandler (fake: FakeNodeInfoHandler) (services: IServiceCollection) =
    services.AddHttpClient(NodeInfo.ClientName) |> ignore

    services.PostConfigure<HttpClientFactoryOptions>(
        NodeInfo.ClientName,
        Action<HttpClientFactoryOptions>(fun options ->
            options.HttpMessageHandlerBuilderActions.Add(fun builder -> builder.PrimaryHandler <- fake)
            options.HandlerLifetime <- Threading.Timeout.InfiniteTimeSpan)
    )
    |> ignore
