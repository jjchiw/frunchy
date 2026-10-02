module FrunchyBackend.Domains.Identity

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// URL-safe, lowercase handle used in the public dashboard address (/u/{username})
module Username =
    let private pattern = Regex("^[a-z0-9][a-z0-9_-]{2,29}$", RegexOptions.Compiled)

    /// Lowercases and validates; None when the handle doesn't match the rule
    let tryCreate (raw: string) =
        if isNull raw then
            None
        else
            let value = raw.Trim().ToLowerInvariant()
            if pattern.IsMatch value then Some value else None

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
