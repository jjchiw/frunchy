/// The language preference (feature 024): the plain `frunchy.lang` cookie (`en` or `es`) that, when present and
/// valid, decides the request's language before Accept-Language does. Not stored on the account.
module FrunchyBackend.Infrastructure.LanguagePreference

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Localization

[<Literal>]
let CookieName = "frunchy.lang"

/// The languages Frunchy has, as the codes the cookie and `/language/{code}` use
let supported = [ "en"; "es" ]

/// Exactly `en` or `es` (nothing is trimmed or case-folded); anything else is not a language
let tryParse (raw: string) =
    match raw with
    | "en"
    | "es" -> Some raw
    | _ -> None

/// The other language: what the footer link offers
let other (code: string) = if code = "es" then "en" else "es"

/// Reads `frunchy.lang`; ignores a missing or invalid value, so the next provider (Accept-Language) decides
type CookieRequestCultureProvider() =
    inherit RequestCultureProvider()

    override _.DetermineProviderCultureResult(httpContext: HttpContext) : Task<ProviderCultureResult> =
        match httpContext.Request.Cookies.TryGetValue CookieName with
        | true, raw ->
            match tryParse raw with
            | Some code -> Task.FromResult(ProviderCultureResult(code))
            | None -> Task.FromResult(Unchecked.defaultof<ProviderCultureResult>)
        | _ -> Task.FromResult(Unchecked.defaultof<ProviderCultureResult>)

/// The footer link's address: set the language, then come back to this page (path and query)
let switchHref (code: string) (pathAndQuery: string) =
    $"/language/{code}?returnUrl={Uri.EscapeDataString pathAndQuery}"
