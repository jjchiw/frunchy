/// A fake external sign-in provider for tests only (research R12). Never registered by the app.
/// Its challenge immediately "returns from the provider": GET /signin/test?sub=abc signs an external
/// principal with NameIdentifier = abc into the external cookie and redirects to /signin/complete.
/// `?fail=1` simulates a cancelled or failed sign-in (no external cookie).
module FrunchyBackend.Tests.Infrastructure.FakeProvider

open System.Security.Claims
open System.Text.Encodings.Web
open System.Threading.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FrunchyBackend.Infrastructure

[<Literal>]
let Scheme = "test"

type FakeProviderHandler(options: IOptionsMonitor<AuthenticationSchemeOptions>, logger: ILoggerFactory, encoder: UrlEncoder) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)

    override _.HandleAuthenticateAsync() = Task.FromResult(AuthenticateResult.NoResult())

    override this.HandleChallengeAsync(properties: AuthenticationProperties) : Task =
        // protected members can't be used inside the task closure
        let context = this.Context
        let query = this.Request.Query
        let redirect = if isNull properties || isNull properties.RedirectUri then "/signin/complete" else properties.RedirectUri

        task {
            if query.ContainsKey "fail" then
                context.Response.Redirect redirect
            else
                let sub = query["sub"].ToString()
                let identity = ClaimsIdentity([ Claim(ClaimTypes.NameIdentifier, sub) ], Scheme)
                do! context.SignInAsync(WebAuth.ExternalScheme, ClaimsPrincipal identity, properties)
                context.Response.Redirect redirect
        }

/// Register the fake provider (scheme + Frunchy provider registry entry) on a test app builder
let register (builder: WebApplicationBuilder) =
    builder.Services
        .AddAuthentication()
        .AddScheme<AuthenticationSchemeOptions, FakeProviderHandler>(Scheme, ignore)
    |> ignore

    builder.Services.AddSingleton<AuthConfig.FrunchyProvider>(AuthConfig.provider Scheme "Test" Scheme (Some "login"))
    |> ignore
