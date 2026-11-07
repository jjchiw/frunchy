/// The theme preference (feature 032): the plain `frunchy.theme` cookie (`light`, `dark` or `system`) the layout
/// renders as `<html data-theme>`. Not stored on the account; a missing or invalid value means `system`.
module FrunchyBackend.Infrastructure.ThemePreference

open System
open Microsoft.AspNetCore.Http

[<Literal>]
let CookieName = "frunchy.theme"

type Mode =
    | Light
    | Dark
    | System

    /// The code the cookie, `/theme/{code}` and `data-theme` use
    member this.Code =
        match this with
        | Light -> "light"
        | Dark -> "dark"
        | System -> "system"

/// The modes, in the order the footer offers them
let all = [ Light; Dark; System ]

/// Exactly `light`, `dark` or `system` (nothing is trimmed or case-folded); anything else is not a mode
let tryParse (raw: string) =
    match raw with
    | "light" -> Some Light
    | "dark" -> Some Dark
    | "system" -> Some System
    | _ -> None

/// The request's mode: `frunchy.theme` when valid, else `system`
let current (ctx: HttpContext) =
    match ctx.Request.Cookies.TryGetValue CookieName with
    | true, raw -> tryParse raw |> Option.defaultValue System
    | _ -> System

/// The footer link's address: set the theme, then come back to this page (path and query)
let switchHref (mode: Mode) (pathAndQuery: string) =
    $"/theme/{mode.Code}?returnUrl={Uri.EscapeDataString pathAndQuery}"
