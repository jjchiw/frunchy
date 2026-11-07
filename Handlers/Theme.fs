module FrunchyBackend.Handlers.Theme

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views

// ---------------------------------------------------------------------------
// Handler: feature 032, like feature 024's language link. Only sets a preference cookie (no account, no data): no
// rate limit and no antiforgery, so it is a plain link. An unknown mode is the plain 404 and sets no cookie.
// ---------------------------------------------------------------------------

let handler (mode: string) : EndpointHandler =
    fun ctx ->
        match ThemePreference.tryParse mode with
        | None ->
            let t = Texts.current ()
            ctx.SetStatusCode StatusCodes.Status404NotFound
            ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)
        | Some theme ->
            ctx.Response.Cookies.Append(
                ThemePreference.CookieName,
                theme.Code,
                CookieOptions(
                    Path = "/",
                    MaxAge = Nullable(TimeSpan.FromDays 365.0),
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = ctx.Request.IsHttps
                )
            )

            let returnUrl = WebAuth.localReturnUrl (ctx.TryGetQueryValue "returnUrl" |> Option.defaultValue "") "/"
            ctx.Response.Headers["Cache-Control"] <- "no-store"
            ctx.Response.StatusCode <- StatusCodes.Status303SeeOther
            ctx.Response.Headers.Location <- returnUrl
            System.Threading.Tasks.Task.CompletedTask

// ---------------------------------------------------------------------------
// Endpoint
// ---------------------------------------------------------------------------

let endpoint = GET [ routef "/theme/{%s}" handler ]
