/// Sign-in provider configuration (feature 002, contracts/configuration.md).
/// Credentials come only from user secrets or the environment; never from committed files.
module FrunchyBackend.Infrastructure.AuthConfig

open System
open Microsoft.Extensions.Configuration

type ProviderSettings =
    { Name: string
      Enabled: bool
      ClientId: string
      ClientSecret: string }

/// Providers this build knows how to register
let known = [ "Google"; "Discord"; "Umbral" ]

let read (config: IConfiguration) : ProviderSettings list =
    [ for name in known ->
          let section = config.GetSection $"Frunchy:Auth:Providers:{name}"

          { Name = name
            Enabled = section.GetValue<bool>("Enabled", false)
            ClientId = section["ClientId"] |> Option.ofObj |> Option.defaultValue ""
            ClientSecret = section["ClientSecret"] |> Option.ofObj |> Option.defaultValue "" } ]

/// FR-017: an enabled provider without credentials must stop the site from starting
let validate (settings: ProviderSettings list) : Result<unit, string> =
    settings
    |> List.filter _.Enabled
    |> List.tryPick (fun p ->
        let missing =
            if String.IsNullOrWhiteSpace p.ClientId then Some "ClientId"
            elif String.IsNullOrWhiteSpace p.ClientSecret then Some "ClientSecret"
            else None

        missing
        |> Option.map (fun key ->
            $"Provider '{p.Name}' is enabled but Frunchy:Auth:Providers:{p.Name}:{key} is missing (set it with dotnet user-secrets or an environment variable)"))
    |> function
        | Some message -> Error message
        | None -> Ok()

/// An OpenID Connect provider's keys and endpoints come from the discovery document at its Authority,
/// so plain http is accepted only on a loopback host (local development, Umbral on http://localhost:5173)
let allowsHttpMetadata (authority: string) =
    match Uri.TryCreate(authority, UriKind.Absolute) with
    | true, uri -> uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback
    | _ -> false

/// An enabled OpenID Connect provider needs an https Authority (http only on localhost), checked at startup
let validateAuthority (name: string) (authority: string) : Result<unit, string> =
    match Uri.TryCreate(authority, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttps || allowsHttpMetadata authority -> Ok()
    | _ ->
        Error
            $"Provider '{name}' is enabled but Frunchy:Auth:Providers:{name}:Authority is not an https URL (plain http is accepted only on localhost): '{authority}'"

/// A sign-in provider registered with the app (Google, Discord, Umbral; the tests add "test")
type FrunchyProvider =
    { /// Route key: /signin/{Name}
      Name: string
      DisplayName: string
      /// Authentication scheme to challenge
      Scheme: string
      /// prompt= value that makes the provider ask the user again (re-confirmation)
      ReconfirmPrompt: string option }

let provider (name: string) (displayName: string) (scheme: string) (reconfirmPrompt: string option) : FrunchyProvider =
    { Name = name
      DisplayName = displayName
      Scheme = scheme
      ReconfirmPrompt = reconfirmPrompt }
