/// Feature 015 (research R1): the calls Frunchy makes to Megáfono's internal API. The named `HttpClient`
/// "megafono" has the base URL, the bearer token and a 10 s timeout, and no resilience pipeline: the outbox
/// retries. Every function returns a `MegafonoResult` (status plus body, or the parsed `{ error, message }`);
/// the token travels only in the client's default header and is never read, logged or returned here.
module FrunchyBackend.Infrastructure.MegafonoClient

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection

[<Literal>]
let ClientName = "megafono"

/// What a call to Megáfono answered. `Status` is 0 when nothing answered (network failure or timeout); then
/// `ErrorCode` is `unreachable` or `timeout`. Otherwise `ErrorCode`/`ErrorMessage` come from `{ error, message }`.
type MegafonoResult =
    { Status: int
      Body: string
      ErrorCode: string option
      ErrorMessage: string option }

    member r.IsSuccess = r.Status >= 200 && r.Status < 300

    /// "503 app_unavailable" or "unreachable": a line fit for `last_error` (never the token)
    member r.Describe =
        match r.Status, r.ErrorCode with
        | 0, Some code -> code
        | status, Some code -> $"{status} {code}"
        | status, None -> string status

/// What `publish` sends: the Note and its audience
type NoteToPublish =
    { Audience: string
      Content: string
      Url: string
      InReplyTo: string option
      Summary: string option
      Sensitive: bool }

/// What `updateObject` sends (the audience can't change)
type NoteUpdate =
    { Content: string
      Url: string
      Summary: string option
      Sensitive: bool }

/// A block target: an actor (URL or `@user@host`) or a whole domain
type BlockTarget =
    | Actor of string
    | Domain of string

/// A block as Megáfono lists it: exactly one of the two is set
type BlockItem =
    { Actor: string option
      Domain: string option
      CreatedAt: string option }

let private errorOf (body: string) =
    try
        use doc = JsonDocument.Parse body

        match doc.RootElement.ValueKind with
        | JsonValueKind.Object ->
            let text (name: string) =
                match doc.RootElement.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            text "error", text "message"
        | _ -> None, None
    with _ ->
        None, None

let private orNull (value: string option) =
    match value with
    | Some v -> v
    | None -> null

let private escape (segment: string) = Uri.EscapeDataString segment

/// One call: any answer (even 4xx and 5xx) comes back as a result; only a transport failure has `Status = 0`
let private send (client: HttpClient) (method': HttpMethod) (path: string) (body: obj option) (key: string option) : Task<MegafonoResult> =
    task {
        try
            use request = new HttpRequestMessage(method', Uri(path, UriKind.Relative))
            key |> Option.iter (fun k -> request.Headers.Add("Idempotency-Key", k))

            body
            |> Option.iter (fun b -> request.Content <- new StringContent(JsonSerializer.Serialize b, Encoding.UTF8, "application/json"))

            use! response = client.SendAsync request
            let! text = response.Content.ReadAsStringAsync()
            let status = int response.StatusCode

            if status >= 200 && status < 300 then
                return { Status = status; Body = text; ErrorCode = None; ErrorMessage = None }
            else
                let code, message = errorOf text
                return { Status = status; Body = text; ErrorCode = code; ErrorMessage = message }
        with
        | :? TaskCanceledException ->
            return { Status = 0; Body = ""; ErrorCode = Some "timeout"; ErrorMessage = Some "Megafono did not answer in time" }
        | :? HttpRequestException as ex ->
            return { Status = 0; Body = ""; ErrorCode = Some "unreachable"; ErrorMessage = Some(ex.GetType().Name) }
    }

/// Publish a Note from an actor (`POST /internal/actors/{id}/objects`). The same key always gives the same object:
/// 201 the first time, 200 on a repeat. Read the answer with `publishedOf`.
let publish (client: HttpClient) (actorId: string) (key: string) (note: NoteToPublish) =
    let body =
        {| audience = note.Audience
           ``object`` =
            {| ``type`` = "Note"
               content = note.Content
               url = note.Url
               inReplyTo = orNull note.InReplyTo
               summary = orNull note.Summary
               sensitive = note.Sensitive |} |}

    send client HttpMethod.Post $"/internal/actors/{escape actorId}/objects" (Some(box body)) (Some key)

/// The object id and URL of a successful publish answer
let publishedOf (result: MegafonoResult) : (string * string) option =
    if not result.IsSuccess then
        None
    else
        try
            use doc = JsonDocument.Parse result.Body

            match doc.RootElement.TryGetProperty "id", doc.RootElement.TryGetProperty "objectUrl" with
            | (true, id), (true, url) when id.ValueKind = JsonValueKind.String && url.ValueKind = JsonValueKind.String ->
                Some(id.GetString(), url.GetString())
            | _ -> None
        with _ ->
            None

/// `PUT /internal/objects/{objectId}`: 200; 404 and 410 are permanent
let updateObject (client: HttpClient) (objectId: string) (note: NoteUpdate) =
    let body =
        {| ``type`` = "Note"
           content = note.Content
           url = note.Url
           summary = orNull note.Summary
           sensitive = note.Sensitive |}

    send client HttpMethod.Put $"/internal/objects/{escape objectId}" (Some(box body)) None

/// `DELETE /internal/objects/{objectId}`: 202
let deleteObject (client: HttpClient) (objectId: string) =
    send client HttpMethod.Delete $"/internal/objects/{escape objectId}" None None

/// `DELETE /internal/actors/{id}`: 202, or 404 when Megáfono never knew it
let deleteActor (client: HttpClient) (actorId: string) =
    send client HttpMethod.Delete $"/internal/actors/{escape actorId}" None None

/// `PUT /internal/actors/{id}`: Megáfono looks the profile up again (202; 404 and 410 when nothing is left)
let refreshProfile (client: HttpClient) (actorId: string) =
    send client HttpMethod.Put $"/internal/actors/{escape actorId}" None None

let private targetBody (target: BlockTarget) : obj =
    match target with
    | Actor a -> box {| actor = a |}
    | Domain d -> box {| domain = d |}

let private targetQuery (target: BlockTarget) =
    match target with
    | Actor a -> $"actor={escape a}"
    | Domain d -> $"domain={escape d}"

/// The blocks of a list answer (`{ "items": [ ... ] }`); empty when the body isn't one
let blocksOf (result: MegafonoResult) : BlockItem list =
    if not result.IsSuccess then
        []
    else
        try
            use doc = JsonDocument.Parse result.Body

            match doc.RootElement.TryGetProperty "items" with
            | true, items when items.ValueKind = JsonValueKind.Array ->
                [ for item in items.EnumerateArray() do
                      let text (name: string) =
                          match item.TryGetProperty name with
                          | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                          | _ -> None

                      { Actor = text "actor"; Domain = text "domain"; CreatedAt = text "createdAt" } ]
            | _ -> []
        with _ ->
            []

/// User blocks (synchronous, from the account page): `GET/POST/DELETE /internal/actors/{id}/blocks`
let listBlocks (client: HttpClient) (userId: string) =
    send client HttpMethod.Get $"/internal/actors/{escape userId}/blocks" None None

let addBlock (client: HttpClient) (userId: string) (target: BlockTarget) =
    send client HttpMethod.Post $"/internal/actors/{escape userId}/blocks" (Some(targetBody target)) None

let removeBlock (client: HttpClient) (userId: string) (target: BlockTarget) =
    send client HttpMethod.Delete $"/internal/actors/{escape userId}/blocks?{targetQuery target}" None None

/// Instance blocks (operators): `GET/POST/DELETE /internal/blocks`
let listInstanceBlocks (client: HttpClient) =
    send client HttpMethod.Get "/internal/blocks" None None

let addInstanceBlock (client: HttpClient) (target: BlockTarget) =
    send client HttpMethod.Post "/internal/blocks" (Some(targetBody target)) None

let removeInstanceBlock (client: HttpClient) (target: BlockTarget) =
    send client HttpMethod.Delete $"/internal/blocks?{targetQuery target}" None None

/// The named client, with the base URL, the bearer token and a 10 s timeout. Registered only when federation is on.
let register (values: Federation.FederationValues) (services: IServiceCollection) =
    services.AddHttpClient(
        ClientName,
        // An explicit Action: a bare lambda resolves to the typed-client overload (TClient = unit)
        Action<HttpClient>(fun client ->
            client.BaseAddress <- values.MegafonoUrl
            client.Timeout <- TimeSpan.FromSeconds 10.0
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", values.Token))
    )
    |> ignore

    services
