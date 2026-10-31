/// Feature 014: the federation switch (specs/014-aspire-platform/contracts/settings.md). Federation is off
/// unless `Frunchy:Federation:Enabled` is true; when it is on, the four settings below are required and
/// startup fails naming the missing or malformed key, never its value (the token and the webhook secret
/// are secrets). Feature 015 reads the `FederationSettings` singleton.
module FrunchyBackend.Infrastructure.Federation

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting

/// What federating needs, all validated
type FederationValues =
    {
        /// The public host, no scheme (e.g. frunchy.test)
        Domain: string
        /// Megáfono's internal API base URL (port 8081), never through the gateway
        MegafonoUrl: Uri
        /// Bearer token: sent on /internal/* calls and checked on Megáfono's calls
        Token: string
        /// HMAC key for the Megafono-Signature header
        WebhookSecret: string
        /// Feature 028: lets the NodeInfo check connect to loopback and private addresses (a local dev fediverse
        /// only; `refuseInProduction` stops a Production start that sets it)
        AllowPrivateAddresses: bool
    }

type FederationSettings =
    | Disabled
    | Enabled of FederationValues

[<Literal>]
let EnabledKey = "Frunchy:Federation:Enabled"

[<Literal>]
let DomainKey = "Frunchy:Federation:Domain"

[<Literal>]
let UrlKey = "Frunchy:Megafono:Url"

[<Literal>]
let TokenKey = "Frunchy:Megafono:Token"

[<Literal>]
let WebhookSecretKey = "Frunchy:Megafono:WebhookSecret"

[<Literal>]
let AllowPrivateAddressesKey = "Frunchy:Federation:AllowPrivateAddresses"

let private valueOf (config: IConfiguration) (key: string) =
    match config[key] with
    | null -> ""
    | value -> value.Trim()

/// A host name: no scheme, no slash, no spaces
let private isHost (domain: string) =
    not (domain.Contains "://" || domain.Contains '/' || domain.Contains '\\' || domain |> Seq.exists Char.IsWhiteSpace)

let private isHttpUrl (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
    | _ -> false

/// Read the federation settings. Off needs nothing. On, every problem is reported by key name only.
let read (config: IConfiguration) : Result<FederationSettings, string> =
    if not (config.GetValue<bool>(EnabledKey, false)) then
        Ok Disabled
    else
        let domain = valueOf config DomainKey
        let url = valueOf config UrlKey
        let token = valueOf config TokenKey
        let secret = valueOf config WebhookSecretKey

        let missing = [ DomainKey, domain; UrlKey, url; TokenKey, token; WebhookSecretKey, secret ] |> List.filter (snd >> (=) "") |> List.map fst

        let malformed =
            [ if domain <> "" && not (isHost domain) then
                  $"{DomainKey} must be a host name without a scheme or a slash"
              if url <> "" && not (isHttpUrl url) then
                  $"{UrlKey} must be an absolute http or https URL" ]

        match missing, malformed with
        | [], [] -> Ok(
                Enabled
                    { Domain = domain
                      MegafonoUrl = Uri url
                      Token = token
                      WebhookSecret = secret
                      AllowPrivateAddresses = config.GetValue<bool>(AllowPrivateAddressesKey, false) }
            )
        | _ ->
            let missingText =
                match missing with
                | [] -> []
                | keys -> [ $"""missing: {String.Join(", ", keys)}""" ]

            Error $"""Federation is enabled ({EnabledKey}) but its settings are not usable ({String.Join("; ", missingText @ malformed)})"""

/// Feature 028 (FR-016): the NodeInfo guard may only be loosened outside Production. Called from `Program.buildApp`
/// (the environment isn't known to `read`); names the key, never a value.
let refuseInProduction (environment: IHostEnvironment) (settings: FederationSettings) =
    match settings with
    | Enabled values when values.AllowPrivateAddresses && environment.IsProduction() ->
        raise (
            InvalidOperationException
                $"{AllowPrivateAddressesKey} must not be true in Production: it lets the server connect to private addresses"
        )
    | _ -> ()
