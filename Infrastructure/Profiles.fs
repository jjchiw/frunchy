/// Feature 017: who can see a profile (every page under `/u/{username}`). One rule, used by every handler that
/// shows or links into a profile (specs/017-private-profiles/contracts/visibility.md):
///   1. the profile is public (the owner's choice, or no choice and `Frunchy:Profiles:DefaultVisibility=public`);
///   2. the viewer is the owner;
///   3. the viewer's `frunchy.shares` cookie holds a grant naming the owner and the owner's current share link.
/// Operators get no exception. The rule itself (`canView`) is pure; the database and the cookie sit around it.
module FrunchyBackend.Infrastructure.Profiles

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Npgsql
open FrunchyBackend.Infrastructure.Database

// ---------------------------------------------------------------------------
// Visibility and the instance setting
// ---------------------------------------------------------------------------

[<Literal>]
let DefaultVisibilityKey = "Frunchy:Profiles:DefaultVisibility"

type Visibility =
    | Public
    | Private

module Visibility =
    /// The text stored in `profile_settings.visibility` and shown in `data-visibility`
    let toDb =
        function
        | Public -> "public"
        | Private -> "private"

    /// Trimmed and case-insensitive; None for anything but `public` or `private`
    let tryParse (raw: string) =
        if isNull raw then
            None
        else
            match raw.Trim().ToLowerInvariant() with
            | "public" -> Some Public
            | "private" -> Some Private
            | _ -> None

/// The instance default for a user who never chose. Missing or blank means `Private`; any other value than
/// `public` or `private` raises, naming the key.
let parseDefault (raw: string) : Visibility =
    if String.IsNullOrWhiteSpace raw then
        Private
    else
        match Visibility.tryParse raw with
        | Some visibility -> visibility
        | None -> raise (InvalidOperationException $"{DefaultVisibilityKey} must be 'public' or 'private', not '{raw.Trim()}'.")

/// The singleton handlers read for the instance default
type ProfileSettings(defaultVisibility: Visibility) =
    member _.DefaultVisibility = defaultVisibility

let read (config: IConfiguration) : ProfileSettings =
    ProfileSettings(parseDefault config[DefaultVisibilityKey])

/// The owner's choice when there is one, otherwise the instance default
let effective (instanceDefault: Visibility) (choice: Visibility option) : Visibility =
    choice |> Option.defaultValue instanceDefault

// ---------------------------------------------------------------------------
// The rule
// ---------------------------------------------------------------------------

/// What the rule needs to know about a profile's owner
type OwnerAccess =
    { UserId: Guid
      /// The owner's own choice; None follows the instance default
      Choice: Visibility option
      Visibility: Visibility
      /// The id of the owner's current share link, when they have one
      LinkId: Guid option }

/// A browser's permission to see one profile: the owner and the link it was opened with
type Grant =
    { UserId: Guid
      LinkId: Guid
      /// Unix seconds when it was added
      At: int64 }

/// Pure: public, the owner, or a grant naming the owner's current link (an old link's grant counts for nothing)
let canView (viewer: Guid option) (grants: Grant list) (owner: OwnerAccess) : bool =
    owner.Visibility = Public
    || viewer = Some owner.UserId
    || (match owner.LinkId with
        | Some linkId -> grants |> List.exists (fun g -> g.UserId = owner.UserId && g.LinkId = linkId)
        | None -> false)

/// One primary-key query. A user without a `profile_settings` row follows the default and has no link.
let loadAccess (db: DbConfig) (settings: ProfileSettings) (userId: Guid) : Task<OwnerAccess> =
    task {
        use connection = getConnection db
        do! connection.OpenAsync()

        use cmd =
            new NpgsqlCommand(
                """
                SELECT s.visibility, s.share_link_id
                FROM users u LEFT JOIN profile_settings s ON s.user_id = u.id
                WHERE u.id = @id
                """,
                connection
            )

        cmd.Parameters.AddWithValue("id", userId) |> ignore
        use! reader = cmd.ExecuteReaderAsync()

        let choice, linkId =
            if reader.Read() then
                (if reader.IsDBNull 0 then None else Visibility.tryParse (reader.GetString 0)),
                (if reader.IsDBNull 1 then None else Some(reader.GetGuid 1))
            else
                None, None

        return
            { UserId = userId
              Choice = choice
              Visibility = effective settings.DefaultVisibility choice
              LinkId = linkId }
    }

// ---------------------------------------------------------------------------
// The grant cookie (research R4)
// ---------------------------------------------------------------------------

[<Literal>]
let GrantCookie = "frunchy.shares"

[<Literal>]
let private ProtectorPurpose = "Frunchy.ShareGrants.v1"

/// Most profiles one browser can hold a grant for; the oldest goes first
let maxGrants = 20

let grantLifetime = TimeSpan.FromDays 30.0

/// Add a grant: a newer one for the same user replaces the old, and only the newest `maxGrants` stay
let addGrant (grants: Grant list) (grant: Grant) : Grant list =
    (grants |> List.filter (fun g -> g.UserId <> grant.UserId)) @ [ grant ]
    |> List.sortBy _.At
    |> List.rev
    |> List.truncate maxGrants
    |> List.rev

/// The cookie's value: `[{ "u": userId, "l": linkId, "at": unixSeconds }]`, data-protected
let protectGrants (protector: IDataProtector) (grants: Grant list) : string =
    let array = JsonArray()

    for g in grants do
        let item = JsonObject()
        item["u"] <- JsonValue.Create(g.UserId.ToString "N")
        item["l"] <- JsonValue.Create(g.LinkId.ToString "N")
        item["at"] <- JsonValue.Create g.At
        array.Add item

    protector.Protect(array.ToJsonString())

/// Any failure (tampered, from another instance, not JSON, wrong shape) means no grants, never an error.
/// Grants older than `grantLifetime` are dropped.
let unprotectGrants (protector: IDataProtector) (now: DateTimeOffset) (value: string) : Grant list =
    try
        use doc = JsonDocument.Parse(protector.Unprotect value)
        let oldest = (now - grantLifetime).ToUnixTimeSeconds()

        [ for item in doc.RootElement.EnumerateArray() do
              match Guid.TryParse(item.GetProperty("u").GetString()), Guid.TryParse(item.GetProperty("l").GetString()) with
              | (true, userId), (true, linkId) ->
                  let at = item.GetProperty("at").GetInt64()

                  if at >= oldest then
                      { UserId = userId
                        LinkId = linkId
                        At = at }
              | _ -> () ]
    with _ ->
        []

let private protectorOf (ctx: HttpContext) =
    ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector ProtectorPurpose

/// The grants this browser holds (none without the cookie or with an unreadable one)
let readGrants (ctx: HttpContext) : Grant list =
    match ctx.Request.Cookies[GrantCookie] with
    | null
    | "" -> []
    | value -> unprotectGrants (protectorOf ctx) (WebAuth.utcNow ctx) value

/// Add a grant to this browser's cookie: HttpOnly, SameSite=Lax, Secure over https, 30 days from now
let grantAccess (ctx: HttpContext) (userId: Guid) (linkId: Guid) : unit =
    let now = WebAuth.utcNow ctx

    let grants =
        addGrant
            (readGrants ctx)
            { UserId = userId
              LinkId = linkId
              At = now.ToUnixTimeSeconds() }

    let options = CookieOptions()
    options.HttpOnly <- true
    options.SameSite <- SameSiteMode.Lax
    options.Secure <- ctx.Request.IsHttps
    options.Path <- "/"
    options.Expires <- Nullable(now + grantLifetime)
    ctx.Response.Cookies.Append(GrantCookie, protectGrants (protectorOf ctx) grants, options)

// ---------------------------------------------------------------------------
// What a handler asks
// ---------------------------------------------------------------------------

/// A profile as one viewer sees it
type ViewerAccess =
    { Access: OwnerAccess
      /// The viewer may see the profile; when false the handler answers the unknown-user 404
      Visible: bool
      /// The viewer is signed in as the profile's owner
      IsOwner: bool }

/// Whether this request's viewer (the signed-in user and the grant cookie) may see a loaded profile
let canViewRequest (ctx: HttpContext) (access: OwnerAccess) : bool =
    canView (WebAuth.currentUserId ctx) (readGrants ctx) access

/// Load the profile's access (one query) and judge this request's viewer
let forViewer (ctx: HttpContext) (db: DbConfig) (userId: Guid) : Task<ViewerAccess> =
    task {
        let! access = loadAccess db (ctx.RequestServices.GetRequiredService<ProfileSettings>()) userId

        return
            { Access = access
              Visible = canViewRequest ctx access
              IsOwner = (WebAuth.currentUserId ctx = Some userId) }
    }

/// May this request's viewer see the profile of `userId`?
let visibleTo (ctx: HttpContext) (db: DbConfig) (userId: Guid) : Task<bool> =
    task {
        let! viewer = forViewer ctx db userId
        return viewer.Visible
    }

/// How a page that passed the check should present itself (what it says about who can see it)
type PageAccess =
    /// The profile is public
    | PublicPage
    /// The owner looking at their private profile: the page says only they can see it
    | OwnPrivatePage
    /// A link holder looking at someone's private profile
    | SharedPrivatePage

let pageAccess (viewer: ViewerAccess) : PageAccess =
    match viewer.Access.Visibility, viewer.IsOwner with
    | Public, _ -> PublicPage
    | Private, true -> OwnPrivatePage
    | Private, false -> SharedPrivatePage

/// The check a `/u/` handler runs right after its user lookup: Some how the page should present itself, or None when
/// the viewer can't see the profile (the handler then answers exactly what an unknown username gets)
let gate (ctx: HttpContext) (db: DbConfig) (userId: Guid) : Task<PageAccess option> =
    task {
        let! viewer = forViewer ctx db userId
        return if viewer.Visible then Some(pageAccess viewer) else None
    }
