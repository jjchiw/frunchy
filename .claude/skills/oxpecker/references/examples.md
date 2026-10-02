<!-- Digest of https://github.com/Lanayx/Oxpecker/tree/develop/examples (commit 62649df), MIT License. -->

# Oxpecker examples: patterns worth copying

The upstream examples reference Oxpecker via `ProjectReference` to `develop`, so a few APIs may be newer than the packages pinned here (see `../SKILL.md`).

| Example | What it shows | Relevant to Frunchy |
|---|---|---|
| `ContactApp` | Server-rendered CRUD with htmx 4: active search, infinite scroll, inline delete, bulk delete, polling progress bar, form validation with `ModelState`, flash messages | **Most relevant**: same stack (ViewEngine + Htmx) |
| `WeatherApp` | ViewEngine + Htmx + **Alpine**, antiforgery, lazy-loaded fragment (`hx-trigger="load"`), active nav link, page title via `ctx.Items` | Alpine usage, CSRF |
| `CRUD/Backend` | JSON API with functional DI ("Env" record + interfaces), `TypedResults` | Pattern for injecting deps without the container |
| `Basic` | Kitchen sink: `routef` formats, `bind*` composition operators, `addFilter`, `configureEndpoint`, auth, response caching, OpenAPI, streaming | Routing/composition reference |
| `Empty` | Minimal hello world | – |
| `MCP` | MCP server/client on Oxpecker | – |
| `EmptySolid`, `TodoList`, `CRUD/Frontend` | Oxpecker.Solid (Fable → SolidJS), client-side | Not used here |

## Composition root (all examples)

```fsharp
let configureApp (app: IApplicationBuilder) =
    app.UseStaticFiles()
       .UseRouting()
       // .UseAntiforgery()          // between UseRouting and UseOxpecker, if forms need CSRF
       .Use(errorHandler)            // exception middleware BEFORE Oxpecker
       .UseOxpecker(endpoints)
       .Run(notFoundHandler)         // 404 AFTER Oxpecker

let configureServices (services: IServiceCollection) =
    services.AddRouting().AddOxpecker() |> ignore
```
`Default.exceptionMiddleware` / `Default.notFoundHandler` exist as ready-made alternatives to hand-written ones.

## Routing table (ContactApp/Program.fs)

Group by verb, then `subRoute`. Typed route params via `routef`:
```fsharp
let endpoints = [
    GET [
        route "/" <| redirectTo "/contacts" true
        subRoute "/contacts" [
            route "/" getContacts
            route "/new" getNewContact
            routef "/{%i}" viewContact          // viewContact: int -> EndpointHandler
            routef "/{%i}/edit" getEditContact
        ]
    ]
    POST   [ subRoute "/contacts" [ route "/new" insertContact; routef "/{%i}/edit" updateContact ] ]
    DELETE [ subRoute "/contacts" [ route "/" deleteContacts; routef "/{%i}" deleteContact ] ]
]
```
`Endpoint` values compose freely: `GET [...]` is itself an `Endpoint`, so each Frunchy slice can export `let endpoint = GET [ route "/x" handler ]` and Program.fs just lists them.

## Full page vs. htmx fragment from the same handler (ContactApp/Handlers.fs)

Inspect the htmx request header and return either a fragment or the full layout:
```fsharp
let getContacts: EndpointHandler =
    fun ctx ->
        match ctx.TryGetQueryValue "q" with
        | Some search ->
            let result = ContactService.searchContact search |> Seq.toArray
            match ctx.TryGetHeaderValue HxRequestHeader.Source with   // htmx 4 (was HX-Trigger in htmx 2)
            | Some "input#search" -> ctx.WriteHtmlView (index.rows page result)   // fragment only
            | _ -> ctx |> writeHtml (index.html search page result archiver)      // full page
        | None -> ...
```
Views that are fragments return `Fragment() { ... }`; full pages pipe into the layout: `Fragment() { ... } |> layout.html`.

## Views that need HttpContext (ContactApp/Tools.fs, WeatherApp)

When the layout needs the request (flash message, active nav, antiforgery token), make views `HttpContext -> HtmlElement` and adapt once:
```fsharp
let writeHtml view ctx = htmlView (view ctx) ctx

// WeatherApp: wrap any page fn in the layout
let htmlView' f (ctx: HttpContext) = f ctx |> layout.html ctx |> ctx.WriteHtmlView
// pages set the <title> through ctx.Items
let html (ctx: HttpContext) = ctx.Items["Title"] <- "Counter"; div() { ... }
```

## htmx idioms (ContactApp/templates/index.fs)

```fsharp
// Active search
input(id="search", type'="search", name="q", value=q)
    .hxGet("/contacts").hxTrigger("search, keyup delay:200ms changed")
    .hxTarget("tbody").hxPushUrl("true").hxIndicator("#spinner")

// Inline delete of a row
a(href="#").hxDelete($"/contacts/{c.Id}").hxSwap("outerHTML swap:1s")
    .hxConfirm("Are you sure?").hxTarget(HxSelector.closest "tr") { "Delete" }

// Infinite scroll: last row loads the next page when revealed
span().hxTarget(HxSelector.closest "tr").hxTrigger("revealed").hxSwap("outerHTML")
      .hxSelect("tbody > tr").hxGet($"/contacts?page={page + 1}") { "Loading More..." }

// Polling a background job (fits Frunchy's import status!)
div().hxGet("/contacts/archive").hxTrigger("load delay:500ms") { "Creating Archive..." }

// Lazy count
span().hxGet("/contacts/count").hxTrigger("revealed") { img(class'="spinner htmx-indicator", src="...") }

// Boost all links/forms under body (htmx 4 needs explicit inheritance)
body().hxBoost("true", HxModifier.inherited) { ... }
```
Self-targeting container that re-renders itself: `div(id="archive-ui").hxTarget(HxSelector.this').hxSwap("outerHTML") { ... }`.

## Forms + validation (ContactApp)

```fsharp
[<CLIMutable>]
type ContactDTO = { id: int; [<Required>] first: string; [<Required; EmailAddress>] email: string }

let insertContact: EndpointHandler =
    fun ctx -> task {
        match! ctx.BindAndValidateForm<ContactDTO>() with
        | ModelValidationResult.Valid dto -> ...; return ctx.Response.Redirect("/contacts")
        | ModelValidationResult.Invalid invalid ->
            return! (invalid |> ModelState.Invalid |> new'.html |> writeHtml) ctx
    }

// view side (open Oxpecker.ModelValidation)
let showErrors (ms: ModelState<_>) field =
    match ms with
    | ModelState.Invalid (_, errors) -> span(class'="error") { String.Join(", ", errors.ErrorMessagesFor field) } :> HtmlElement
    | _ -> Fragment()
// read values: contact.Value(_.id >> string)   // works for Empty/Valid/Invalid
```
Note the upcast `:> HtmlElement` when `match` branches return different element types.

## Alpine + antiforgery (WeatherApp)

```fsharp
div().xData("{ currentCount: 0 }") {
    p(role="status").xText("'CurrentCount: ' + currentCount")
    button(class'="btn").xOn("click", "currentCount++") { "Click me" }
}

form(action="/weatherData", method="POST").hxTarget("#weatherData").hxSwap("outerHTML") {
    ctx.GetAntiforgeryInput()   // requires AddAntiforgery() + UseAntiforgery()
    button(type'="submit") { "Refresh" }
}
```

## Functional DI (CRUD/Backend)

Instead of `ctx.GetService<T>()`, build an `Env` record once in `configureApp` and pass it to endpoint factories:
```fsharp
type Env = { Logger: ILogger; DbClient: IDbClient }
let getEndpoints env = [ GET [ route "/" <| Handlers.getOrders env ] ]
// Handlers.getOrders env (ctx: HttpContext) = task { ... } :> Task
```
Frunchy currently uses `ctx.GetService<DbConfig>()` inside slices; switch to this only if testing without the container becomes a need.
