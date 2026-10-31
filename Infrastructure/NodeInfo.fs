/// Feature 028 (FR-016): is the host an operator typed another Frunchy instance? Its NodeInfo says so
/// (`software.name` = `frunchy`, which Megáfono advertises through `Megafono__Software__Name`). This is the one HTTP
/// call Frunchy makes to a host a person typed, so it is guarded: https only on port 443, no redirects, 5 seconds,
/// at most 256 KB per answer, the schema link only on the same host, and a connect callback that refuses loopback,
/// private, link-local, CGNAT, multicast, unspecified and unique-local addresses (unless
/// `Frunchy:Federation:AllowPrivateAddresses`, which Production refuses). Nothing about an answer but the outcome
/// is logged or stored.
module FrunchyBackend.Infrastructure.NodeInfo

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection

[<Literal>]
let ClientName = "nodeinfo"

/// What the check came to
type NodeInfoResult =
    | IsFrunchy
    | NotFrunchy
    /// No answer, a timeout, a non-2xx, an answer over the cap or a refused connection
    | Unreachable

[<Literal>]
let MaxBodyBytes = 262144

let private overallTimeout = TimeSpan.FromSeconds 5.0

let private schemaRelPrefix = "http://nodeinfo.diaspora.software/ns/schema/2."

let private hostPattern =
    Regex(@"\A[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*\z", RegexOptions.CultureInvariant)

// ---------------------------------------------------------------------------
// Which addresses may be connected to
// ---------------------------------------------------------------------------

let private isPublicV4 (b: byte[]) =
    match int b[0], int b[1] with
    | 0, _ -> false // 0.0.0.0/8
    | 10, _ -> false
    | 100, second when second >= 64 && second <= 127 -> false // 100.64.0.0/10, CGNAT
    | 127, _ -> false
    | 169, 254 -> false
    | 172, second when second >= 16 && second <= 31 -> false
    | 192, 168 -> false
    | 192, 0 when b[2] = 0uy -> false // 192.0.0.0/24, IETF protocol assignments
    | 198, second when second = 18 || second = 19 -> false // 198.18.0.0/15, benchmarking
    | first, _ when first >= 224 -> false // multicast and everything above
    | _ -> true

/// Whether the address is one a server may fetch from on a stranger's say-so: false for loopback, unspecified,
/// private, link-local, CGNAT, benchmarking, multicast and IPv6 unique-local addresses, and for the IPv6 forms that
/// carry an IPv4 address (mapped, the old compatible form, 6to4, NAT64) of the IPv4 ones
let isPublicAddress (ip: IPAddress) : bool =
    if IPAddress.IsLoopback ip then
        false
    elif ip.AddressFamily = AddressFamily.InterNetwork then
        isPublicV4 (ip.GetAddressBytes())
    elif ip.AddressFamily = AddressFamily.InterNetworkV6 then
        if ip.IsIPv4MappedToIPv6 then
            isPublicV4 (ip.MapToIPv4().GetAddressBytes())
        else
            let b = ip.GetAddressBytes()

            if ip.Equals IPAddress.IPv6Any || ip.Equals IPAddress.IPv6Loopback then false
            elif b[0] &&& 0xFEuy = 0xFCuy then false // fc00::/7
            elif b[0] = 0xFEuy && (b[1] &&& 0xC0uy) = 0x80uy then false // fe80::/10
            elif b[0] = 0xFFuy then false // ff00::/8
            elif b[0..11] |> Array.forall ((=) 0uy) then isPublicV4 b[12..15] // ::a.b.c.d
            elif b[0] = 0x20uy && b[1] = 0x02uy then isPublicV4 b[2..5] // 2002::/16, 6to4 carries an IPv4 address
            elif b[0..11] = [| 0uy; 0x64uy; 0xFFuy; 0x9Buy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |] then
                isPublicV4 b[12..15] // 64:ff9b::/96, NAT64
            else true
    else
        false

/// The primary handler: no redirects, and a connect step that resolves the host, refuses every address
/// `isPublicAddress` refuses (unless private addresses are allowed) and connects to the first one that is left
let private handler (allowPrivate: bool) : HttpMessageHandler =
    let connect (context: SocketsHttpConnectionContext) (token: CancellationToken) : ValueTask<Stream> =
        ValueTask<Stream>(
            task {
                let! addresses = Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token)

                match addresses |> Array.tryFind (fun a -> allowPrivate || isPublicAddress a) with
                | None -> return raise (HttpRequestException "address refused")
                | Some address ->
                    let socket = new Socket(SocketType.Stream, ProtocolType.Tcp, NoDelay = true)

                    try
                        do! socket.ConnectAsync(IPEndPoint(address, context.DnsEndPoint.Port), token)
                        return new NetworkStream(socket, true) :> Stream
                    with ex ->
                        socket.Dispose()
                        return raise ex
            }
        )

    new SocketsHttpHandler(AllowAutoRedirect = false, ConnectCallback = Func<_, _, _>(connect))

/// The "nodeinfo" client: 5 s, a 256 KB buffer cap and a User-Agent naming Frunchy, with the guarded handler.
/// Registered only when federation is on.
let register (values: Federation.FederationValues) (services: IServiceCollection) =
    services
        .AddHttpClient(
            ClientName,
            // An explicit Action: a bare lambda resolves to the typed-client overload (TClient = unit)
            Action<HttpClient>(fun client ->
                client.Timeout <- overallTimeout
                client.MaxResponseContentBufferSize <- int64 MaxBodyBytes
                client.DefaultRequestHeaders.UserAgent.ParseAdd "Frunchy-NodeInfo/1.0 (+https://github.com/jjchiw/frunchy)")
        )
        .ConfigurePrimaryHttpMessageHandler(fun () -> handler values.AllowPrivateAddresses)
    |> ignore

    services

// ---------------------------------------------------------------------------
// The check
// ---------------------------------------------------------------------------

/// The body of a 2xx answer, or None when it isn't one or is over the cap. Reads at most the cap plus one byte.
let private fetch (client: HttpClient) (url: string) (token: CancellationToken) : Task<string option> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, url)
        request.Headers.Accept.ParseAdd "application/json"
        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)

        if not response.IsSuccessStatusCode then
            return None
        else
            use! stream = response.Content.ReadAsStreamAsync token
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable tooBig = false
            let mutable reading = true

            while reading do
                let! read = stream.ReadAsync(chunk.AsMemory(), token)

                if read = 0 then
                    reading <- false
                else
                    buffer.Write(chunk, 0, read)

                    if buffer.Length > int64 MaxBodyBytes then
                        tooBig <- true
                        reading <- false

            return (if tooBig then None else Some(Text.Encoding.UTF8.GetString(buffer.ToArray())))
    }

let private prop (element: JsonElement) (name: string) : JsonElement option =
    if element.ValueKind = JsonValueKind.Object then
        match element.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    else
        None

let private stringProp (element: JsonElement) (name: string) : string option =
    match prop element name with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// The 2.x schema link of the discovery document, when its `href` is https on `host` (no port, no user info)
let private schemaLink (host: string) (body: string) : string option =
    try
        use doc = JsonDocument.Parse body

        match prop doc.RootElement "links" with
        | Some links when links.ValueKind = JsonValueKind.Array ->
            links.EnumerateArray()
            |> Seq.tryFind (fun link ->
                match stringProp link "rel" with
                | Some rel -> rel.StartsWith(schemaRelPrefix, StringComparison.Ordinal)
                | None -> false)
            |> Option.bind (fun link -> stringProp link "href")
            |> Option.filter (fun href ->
                href.Length <= 2048
                && (match Uri.TryCreate(href, UriKind.Absolute) with
                    | true, uri ->
                        uri.Scheme = Uri.UriSchemeHttps
                        && uri.IsDefaultPort
                        && uri.UserInfo = ""
                        && String.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
                    | _ -> false))
        | _ -> None
    with :? JsonException ->
        None

let private softwareName (body: string) : string option =
    try
        use doc = JsonDocument.Parse body

        prop doc.RootElement "software" |> Option.bind (fun software -> stringProp software "name")
    with :? JsonException ->
        None

/// Ask `host` (a plain host name, no port or path) whether it is a Frunchy instance
let check (client: HttpClient) (host: string) : Task<NodeInfoResult> =
    task {
        let host = if isNull host then "" else host.Trim().ToLowerInvariant()

        if host = "" || not (hostPattern.IsMatch host) then
            return NotFrunchy
        else
            use deadline = new CancellationTokenSource(overallTimeout)

            try
                match! fetch client $"https://{host}/.well-known/nodeinfo" deadline.Token with
                | None -> return Unreachable
                | Some discovery ->
                    match schemaLink host discovery with
                    | None -> return NotFrunchy
                    | Some href ->
                        match! fetch client href deadline.Token with
                        | None -> return Unreachable
                        | Some document ->
                            match softwareName document with
                            | Some name when String.Equals(name, "frunchy", StringComparison.OrdinalIgnoreCase) -> return IsFrunchy
                            | _ -> return NotFrunchy
            with
            | :? HttpRequestException
            | :? TaskCanceledException
            | :? OperationCanceledException
            | :? IOException -> return Unreachable
    }
