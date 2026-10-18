/// Feature 015: the pure fediverse rules (series handles, webhook signature, remote content as text, ids),
/// plus the data access that several slices and the outbox worker share (`ensureSeriesHandle`, `enqueue`,
/// `episodeGroupKey`). Research R2, R4, R7. Nothing here talks to Megáfono.
module FrunchyBackend.Infrastructure.Fediverse

open System
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Npgsql

// ---------------------------------------------------------------------------
// Pure rules
// ---------------------------------------------------------------------------

/// Megáfono's handle rule (app contract): 1 to 30 characters, dashes only inside, no dots
let handlePattern = Regex(@"\A[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?\z", RegexOptions.CultureInvariant)

let isValidHandle (handle: string) =
    not (isNull handle) && handlePattern.IsMatch handle

[<Literal>]
let MaxHandleLength = 30

/// Megáfono's actor id rule
let private idPattern = Regex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)

let private nonHandleRuns = Regex(@"[^a-z0-9_-]+", RegexOptions.CultureInvariant)
let private dashRuns = Regex("-{2,}", RegexOptions.CultureInvariant)

/// Cut to at most `max` characters at the last dash at or before that length (a word boundary), without
/// dashes at either end; a single word longer than `max` is cut hard
let private cutAtWord (max: int) (value: string) =
    let cut =
        if value.Length <= max then
            value
        elif value[max] = '-' then
            value.Substring(0, max)
        else
            match value.Substring(0, max).LastIndexOf '-' with
            | -1 -> value.Substring(0, max)
            | i -> value.Substring(0, i)

    cut.Trim '-'

/// The handle a slug (else the series id) gives before any clash is resolved (research R4)
let baseSeriesHandle (slug: string option) (seriesId: string) : string =
    let fromText (text: string) =
        dashRuns.Replace(nonHandleRuns.Replace(text.ToLowerInvariant(), "-"), "-").Trim '-'
        |> cutAtWord MaxHandleLength

    let fromSlug =
        match slug with
        | Some s when not (String.IsNullOrWhiteSpace s) -> fromText s
        | _ -> ""

    if fromSlug <> "" then
        fromSlug
    else
        match fromText $"series-{seriesId}" with
        | "" -> "series"
        | fallback -> fallback

/// The base, then `-2`, `-3`... up to `-count`, each shortened first so the result stays within 30 characters
let handleCandidates (slug: string option) (seriesId: string) (count: int) : string list =
    let baseHandle = baseSeriesHandle slug seriesId

    baseHandle
    :: [ for n in 2..count do
             let suffix = $"-{n}"

             match cutAtWord (MaxHandleLength - suffix.Length) baseHandle with
             | "" -> ()
             | shortened -> shortened + suffix ]

/// The first candidate that isn't taken (`taken` says whether a handle or username is already used)
let seriesHandleOf (slug: string option) (seriesId: string) (taken: string -> bool) : string =
    handleCandidates slug seriesId 200
    |> List.tryFind (taken >> not)
    |> Option.defaultWith (fun () -> failwith "No free series handle")

/// `https://{domain}/ap/actors/{id}`: the actor URL a page advertises so a pasted link works on Mastodon
let actorUrl (domain: string) (id: string) = $"https://{domain}/ap/actors/{id}"

let private macOf (secret: string) (t: string) (body: byte[]) =
    HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, Array.append (Encoding.UTF8.GetBytes $"{t}.") body)
    |> Convert.ToHexStringLower

/// `Megafono-Signature: t=<unix>,v1=<hex hmac-sha256 of "t.body">`: at most 5 minutes old (either side),
/// compared in constant time (research R2)
let verifySignature (secret: string) (now: DateTimeOffset) (header: string) (body: byte[]) : bool =
    if String.IsNullOrEmpty secret || String.IsNullOrEmpty header then
        false
    else
        let parts =
            header.Split ','
            |> Array.map (fun p -> p.Trim().Split('=', 2))
            |> Array.filter (fun p -> p.Length = 2)

        let part name =
            parts |> Array.tryFind (fun p -> p[0] = name) |> Option.map (fun p -> p[1])

        match part "t", part "v1" with
        | Some t, Some v1 ->
            match Int64.TryParse t with
            | true, timestamp when abs (now.ToUnixTimeSeconds() - timestamp) <= 300L ->
                let expected = Encoding.UTF8.GetBytes(macOf secret t body)
                CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(v1.ToLowerInvariant()))
            | _ -> false
        | _ -> false

/// The header Megáfono sends for `body` at `now` (the tests sign their webhook bodies with it)
let signBody (secret: string) (now: DateTimeOffset) (body: byte[]) : string =
    let t = string (now.ToUnixTimeSeconds())
    $"t={t},v1={macOf secret t body}"

let private lineBreakTags = Regex(@"<\s*(br|/p|/li)\b[^>]*>", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
let private anyTag = Regex(@"<[^>]*>", RegexOptions.CultureInvariant)
let private manyNewlines = Regex(@"\n{3,}", RegexOptions.CultureInvariant)

/// Remote HTML to plain text (research R7): `<br>`, `</p>` and `</li>` become line breaks, every tag is
/// stripped, entities are decoded. The result is shown escaped, so no remote markup ever reaches a page.
let htmlToText (html: string) : string =
    if String.IsNullOrEmpty html then
        ""
    else
        let withBreaks = lineBreakTags.Replace(html, "\n")
        let stripped = anyTag.Replace(withBreaks, "")
        let decoded = WebUtility.HtmlDecode stripped
        (manyNewlines.Replace(decoded.Replace("\r\n", "\n").Replace('\r', '\n'), "\n\n")).Trim()

/// A Frunchy comment as the HTML Megáfono publishes: paragraphs (blank-line separated) of escaped text,
/// single line breaks as `<br>`
let commentHtml (text: string) : string =
    let normalized = (if isNull text then "" else text).Replace("\r\n", "\n").Replace('\r', '\n').Trim()

    Regex.Split(normalized, @"\n{2,}")
    |> Array.filter (fun paragraph -> paragraph.Trim() <> "")
    |> Array.map (fun paragraph ->
        let lines = paragraph.Trim('\n').Split '\n' |> Array.map (fun line -> WebUtility.HtmlEncode(line.TrimEnd()))
        "<p>" + String.Join("<br>", lines) + "</p>")
    |> String.concat ""

/// A remote comment's id: `r-` plus the first 40 characters of the base64url SHA-256 of its object id
/// (fits the comment id rule `^[A-Za-z0-9_-]{1,64}$` and is stable for the same object)
let remoteCommentId (objectId: string) : string =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes objectId)
    let encoded = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    "r-" + encoded.Substring(0, 40)

/// Whether Megáfono would accept the value as an actor id
let isValidActorId (id: string) = not (isNull id) && idPattern.IsMatch id

// ---------------------------------------------------------------------------
// Data access shared by slices and the worker
// ---------------------------------------------------------------------------

/// The group key of an episode: the original season id, `#`, the episode number (the dub group rule of
/// feature 009: a dub shares the thread of the season it is a dub of), or the episode id alone when it has no
/// season or no number
let episodeGroupKey (connection: NpgsqlConnection) (episodeId: string) : Task<string> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT CASE WHEN e.season_id IS NOT NULL AND e.episode_number IS NOT NULL
                            THEN coalesce(m.shared_from, e.season_id) || '#' || e.episode_number::text
                            ELSE e.id END
                FROM cr_episodes e
                LEFT JOIN cr_season_matches m ON m.season_id = e.season_id AND m.status = 'matched'
                WHERE e.id = @episode_id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("episode_id", episodeId) |> ignore

        match! cmd.ExecuteScalarAsync() with
        | :? string as key -> return key
        | _ -> return episodeId
    }

/// Queue one call to Megáfono, in the transaction of the change that needs it. `subject` is the group key,
/// the comment id or the user id (the outbox holds no payloads)
let enqueue (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (kind: string) (subject: string) : Task<unit> =
    task {
        use cmd =
            new NpgsqlCommand("INSERT INTO fediverse_outbox (kind, subject) VALUES (@kind, @subject)", connection, tx)

        cmd.Parameters.AddWithValue("kind", kind) |> ignore
        cmd.Parameters.AddWithValue("subject", subject) |> ignore
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }

/// Whether federation is on (the outbox is only written then)
let isEnabled (settings: Federation.FederationSettings) =
    match settings with
    | Federation.Enabled _ -> true
    | Federation.Disabled -> false

/// Can this user's comments federate: their username has to fit Megáfono's handle rule (accounts older than the
/// no-trailing-dash rule may not). Read inside the transaction of the change.
let private authorCanFederate (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (userId: Guid) : Task<bool> =
    task {
        use cmd = new NpgsqlCommand("SELECT username FROM users WHERE id = @id", connection, tx)
        cmd.Parameters.AddWithValue("id", userId) |> ignore

        match! cmd.ExecuteScalarAsync() with
        | :? string as username -> return isValidHandle username
        | _ -> return false
    }

/// Queue what publishing a new comment takes, in the comment's transaction: the group's thread when it has no row
/// yet (the row is made here, without an object id until the worker publishes it), then the comment. Nothing for an
/// author who can't federate.
let enqueueNewComment
    (connection: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    (authorId: Guid)
    (seriesId: string)
    (episodeId: string)
    (groupKey: string)
    (commentId: string)
    : Task<unit> =
    task {
        match! authorCanFederate connection tx authorId with
        | false -> ()
        | true ->
            use thread =
                new NpgsqlCommand(
                    """
                    INSERT INTO episode_threads (group_key, series_id, cr_episode_id)
                    VALUES (@group_key, @series_id, @episode_id)
                    ON CONFLICT (group_key) DO NOTHING
                    RETURNING group_key
                    """,
                    connection,
                    tx
                )

            thread.Parameters.AddWithValue("group_key", groupKey) |> ignore
            thread.Parameters.AddWithValue("series_id", seriesId) |> ignore
            thread.Parameters.AddWithValue("episode_id", episodeId) |> ignore

            match! thread.ExecuteScalarAsync() with
            | :? string -> do! enqueue connection tx "ensure_thread" groupKey
            | _ -> ()

            do! enqueue connection tx "publish_comment" commentId
    }

/// Whether a publish of this comment is still waiting and unclaimed: it will read the latest text when it runs,
/// so an edit needs no update of its own
let private publishNotYetClaimed (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (commentId: string) : Task<bool> =
    task {
        use cmd =
            new NpgsqlCommand(
                """
                SELECT EXISTS (SELECT 1 FROM fediverse_outbox
                               WHERE kind = 'publish_comment' AND subject = @subject
                                 AND state = 'pending' AND claimed_at IS NULL)
                """,
                connection,
                tx
            )

        cmd.Parameters.AddWithValue("subject", commentId) |> ignore
        let! found = cmd.ExecuteScalarAsync()
        return unbox<bool> found
    }

/// Queue an edit or a delete of a comment, in the change's transaction (nothing for an author who can't federate).
/// An edit queues nothing while the comment's publish is still pending and unclaimed (it sends the latest text);
/// once the publish is claimed or done, the update is queued as usual.
let enqueueCommentChange (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (authorId: Guid) (kind: string) (commentId: string) : Task<unit> =
    task {
        match! authorCanFederate connection tx authorId with
        | false -> ()
        | true ->
            let! covered =
                if kind = "update_comment" then
                    publishNotYetClaimed connection tx commentId
                else
                    Task.FromResult false

            if not covered then
                do! enqueue connection tx kind commentId
    }

/// The advisory lock that serialises handle creation, so two series can't pick the same one and a sign-up can't
/// take a username a series is taking (research R4)
[<Literal>]
let HandleLockKey = 1501L

/// The series' stored handle, created from its slug when missing. One transaction under an advisory lock;
/// unique against every series handle and every username. Handles are never changed automatically.
let ensureSeriesHandle (connection: NpgsqlConnection) (seriesId: string) : Task<string> =
    task {
        use tx = connection.BeginTransaction()

        use lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, tx)
        lock.Parameters.AddWithValue("key", HandleLockKey) |> ignore
        let! _ = lock.ExecuteNonQueryAsync()

        use existing = new NpgsqlCommand("SELECT handle FROM series_handles WHERE series_id = @series_id", connection, tx)
        existing.Parameters.AddWithValue("series_id", seriesId) |> ignore

        match! existing.ExecuteScalarAsync() with
        | :? string as handle ->
            do! tx.CommitAsync()
            return handle
        | _ ->
            use slugCmd = new NpgsqlCommand("SELECT slug FROM cr_series WHERE id = @series_id", connection, tx)
            slugCmd.Parameters.AddWithValue("series_id", seriesId) |> ignore

            let! slugValue = slugCmd.ExecuteScalarAsync()

            let slug =
                match slugValue with
                | :? string as s -> Some s
                | _ -> None

            let candidates = handleCandidates slug seriesId 200 |> Array.ofList

            use takenCmd =
                new NpgsqlCommand(
                    """
                    SELECT c FROM unnest(@candidates) AS c
                    WHERE EXISTS (SELECT 1 FROM series_handles h WHERE h.handle = c)
                       OR EXISTS (SELECT 1 FROM users u WHERE u.username = c)
                    """,
                    connection,
                    tx
                )

            takenCmd.Parameters.AddWithValue("candidates", candidates) |> ignore
            let taken = System.Collections.Generic.HashSet<string>()

            do!
                task {
                    use! reader = takenCmd.ExecuteReaderAsync()

                    while reader.Read() do
                        taken.Add(reader.GetString 0) |> ignore
                }

            match candidates |> Array.tryFind (taken.Contains >> not) with
            | None -> return failwith $"No free series handle for {seriesId}"
            | Some handle ->
                use insert =
                    new NpgsqlCommand("INSERT INTO series_handles (series_id, handle) VALUES (@series_id, @handle)", connection, tx)

                insert.Parameters.AddWithValue("series_id", seriesId) |> ignore
                insert.Parameters.AddWithValue("handle", handle) |> ignore
                let! _ = insert.ExecuteNonQueryAsync()
                do! tx.CommitAsync()
                return handle
    }
