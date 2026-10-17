module FrunchyBackend.Domains.Identity

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// URL-safe, lowercase handle used in the public dashboard address (/u/{username})
module Username =
    let private pattern = Regex("^[a-z0-9][a-z0-9_-]{2,29}$", RegexOptions.Compiled)

    /// The rule for names created from now on: like `pattern`, but the last character can't be a dash
    /// (Mastodon refuses remote usernames ending in "-")
    let private newPattern = Regex("^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9_]$", RegexOptions.Compiled)

    /// Names that would collide with Frunchy's own pages or be misleading (feature 002 data-model)
    let reserved =
        set [ "admin"; "api"; "account"; "accounts"; "login"; "logout"; "signin"; "signout"; "signup"
              "claim"; "settings"; "support"; "help"; "frunchy"; "static"; "www" ]

    /// Lowercases and validates; None when the handle doesn't match the rule or is reserved
    let tryCreate (raw: string) =
        if isNull raw then
            None
        else
            let value = raw.Trim().ToLowerInvariant()
            if pattern.IsMatch value && not (reserved.Contains value) then Some value else None

    /// The creation rule (sign-up, `user-create`): `tryCreate`'s, plus no trailing dash. Lookups keep
    /// `tryCreate`, so accounts created before the rule still resolve.
    let tryCreateNew (raw: string) =
        if isNull raw then
            None
        else
            let value = raw.Trim().ToLowerInvariant()
            if newPattern.IsMatch value && not (reserved.Contains value) then Some value else None

/// Secret the extension sends as `Authorization: Bearer <key>`. Only its SHA-256 hash is stored.
module SyncKey =
    let prefix = "frk_"

    /// "frk_" + base64url of 32 random bytes (43 chars)
    let generate () =
        let encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)
        prefix + encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// Lowercase hex SHA-256, the value persisted in users.sync_key_hash
    let hash (key: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes key) |> Convert.ToHexStringLower

/// The Crunchyroll account (plus profile, when Crunchyroll needs it) a user's history comes from
type HistoryOwner =
    { AccountId: string
      ProfileId: string option }

type User =
    { Id: Guid
      Username: string
      DisplayName: string
      CrAccountId: string option
      CrProfileId: string option
      CreatedAt: DateTimeOffset }

/// One-time sign-up invite codes: "inv_" + 12 Crockford base32 chars; only the hash is stored
module InviteCode =
    let private alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    let generate () =
        let bytes = RandomNumberGenerator.GetBytes 12
        "inv_" + String([| for b in bytes -> alphabet[int b % 32] |])

    /// Trims, accepts any case, and maps the Crockford look-alikes (I/L → 1, O → 0)
    let normalize (raw: string) =
        if isNull raw then
            ""
        else
            let trimmed = raw.Trim()
            let body = if trimmed.StartsWith("inv_", StringComparison.OrdinalIgnoreCase) then trimmed.Substring 4 else trimmed
            "inv_" + body.ToUpperInvariant().Replace('I', '1').Replace('L', '1').Replace('O', '0')

    let hash (code: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(normalize code)) |> Convert.ToHexStringLower

/// A provider identity: the provider plus that provider's stable user id (never email or name)
type ProviderIdentity =
    { Provider: string
      ProviderUserId: string }

[<RequireQualifiedAccess>]
type Actor =
    | User
    | Operator

module Actor =
    let toDb =
        function
        | Actor.User -> "user"
        | Actor.Operator -> "operator"

/// Account security events (feature 002, FR-022)
[<RequireQualifiedAccess>]
type SecurityEventType =
    | AccountCreated
    | SignedIn
    | Claimed
    | ClaimRefused
    | IdentityLinked
    | IdentityUnlinked
    | SyncKeyGenerated
    | BindingReset
    | Reconfirmed
    | DisplayNameChanged
    | MalSet
    | MalNone
    | MalRetry
    | MalAuto
    | OperatorAdded
    | OperatorRemoved
    | CommentHidden
    | CommentUnhidden

module SecurityEventType =
    let toDb =
        function
        | SecurityEventType.AccountCreated -> "account_created"
        | SecurityEventType.SignedIn -> "signed_in"
        | SecurityEventType.Claimed -> "claimed"
        | SecurityEventType.ClaimRefused -> "claim_refused"
        | SecurityEventType.IdentityLinked -> "identity_linked"
        | SecurityEventType.IdentityUnlinked -> "identity_unlinked"
        | SecurityEventType.SyncKeyGenerated -> "sync_key_generated"
        | SecurityEventType.BindingReset -> "binding_reset"
        | SecurityEventType.Reconfirmed -> "reconfirmed"
        | SecurityEventType.DisplayNameChanged -> "display_name_changed"
        | SecurityEventType.MalSet -> "mal_set"
        | SecurityEventType.MalNone -> "mal_none"
        | SecurityEventType.MalRetry -> "mal_retry"
        | SecurityEventType.MalAuto -> "mal_auto"
        | SecurityEventType.OperatorAdded -> "operator_added"
        | SecurityEventType.OperatorRemoved -> "operator_removed"
        | SecurityEventType.CommentHidden -> "comment_hidden"
        | SecurityEventType.CommentUnhidden -> "comment_unhidden"

    let all =
        [ SecurityEventType.AccountCreated; SecurityEventType.SignedIn; SecurityEventType.Claimed
          SecurityEventType.ClaimRefused; SecurityEventType.IdentityLinked; SecurityEventType.IdentityUnlinked
          SecurityEventType.SyncKeyGenerated; SecurityEventType.BindingReset; SecurityEventType.Reconfirmed
          SecurityEventType.DisplayNameChanged; SecurityEventType.MalSet; SecurityEventType.MalNone
          SecurityEventType.MalRetry; SecurityEventType.MalAuto; SecurityEventType.OperatorAdded
          SecurityEventType.OperatorRemoved; SecurityEventType.CommentHidden; SecurityEventType.CommentUnhidden ]

    let tryFromDb (value: string) = all |> List.tryFind (fun e -> toDb e = value)
