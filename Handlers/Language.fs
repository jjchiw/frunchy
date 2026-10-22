module FrunchyBackend.Handlers.Language

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views

// ---------------------------------------------------------------------------
// Handler: feature 024. Only sets a preference cookie (no account, no data): no rate limit and no
// antiforgery, so it is a plain link. An unknown language is the plain 404 and sets no cookie.
// ---------------------------------------------------------------------------

let handler (code: string) : EndpointHandler =
    fun ctx ->
        match LanguagePreference.tryParse code with
        | None ->
            let t = Texts.current ()
            ctx.SetStatusCode StatusCodes.Status404NotFound
            ctx.WriteHtmlView(Layout.messagePage ctx t t.NotFoundTitle t.NotFoundBody)
        | Some language ->
            ctx.Response.Cookies.Append(
                LanguagePreference.CookieName,
                language,
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

let endpoint = GET [ routef "/language/{%s}" handler ]
