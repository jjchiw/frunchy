---
name: oxpecker
description: Reference for the Oxpecker F# web stack used in this repo (Oxpecker routing/handlers, Oxpecker.ViewEngine HTML DSL, Oxpecker.Htmx, Oxpecker.Alpine) plus the repo's vertical-slice handler convention. Use when adding or changing an endpoint, route, view, htmx interaction or Alpine directive, or when unsure of an Oxpecker API name, signature or which package version supports it.
---

# Oxpecker in Frunchy

## Pinned versions (check before using any API from the docs)

The reference docs in `references/` mirror the upstream **develop** branch, which is ahead of what this repo uses:

| Package | Pinned | Notes |
|---|---|---|
| `Oxpecker` | 2.1.1 | Docs describe 3.0-beta. **Not available in 2.1.1:** `WriteMultipart*`/`multipart*`/`MultipartPart`, the `QUERY` verb. Available: `routef`, `subRoute`, verbs, `bind*`, `BindAndValidate*`, `ModelState`, `WriteHtmlView(Chunked)`, `TypedResults` via `%`. |
| `Oxpecker.ViewEngine` | 2.1.0 | `prerender`/`prerenderAround`, `Fragment`, `Render.toString` available. **Not available:** `Render.toBufferWriter`/`toHtmlDocBufferWriter`. |
| `Oxpecker.Htmx` | 4.0.x | **htmx 4 API** (`HxRequestHeader.Source`, `hxDisable` = old `hx-disabled-elt`, explicit `HxModifier.inherited`, `HxSelector`). **Not available in 4.0.0:** `hxPartial` (4.1+). The client script in `Views/Layout.fs` must stay on htmx **4.x** to match. |
| `Oxpecker.Alpine` | 0.3.0 | Matches docs. |

If an API isn't in the pinned version, either avoid it or bump the package deliberately (and say so). To verify quickly: `grep -ac <Name> ~/.nuget/packages/<package>/<version>/lib/net10.0/<Assembly>.dll`.

## Repo convention: one vertical slice per endpoint

Every endpoint lives in its own file under `Handlers/` (grouped by feature folder when there are several, e.g. `Handlers/Imports/`). A slice file is laid out top to bottom:

1. **View**: the `HtmlElement` rendering function(s), piped into `FrunchyBackend.Views.Layout.page "<Title>"` for full pages. For JSON endpoints this section holds the request/response contract records instead.
2. **Data access** (when needed): `private` functions with the slice's own SQL (raw Npgsql). Slices don't share query code.
3. **Handler**: `let handler: EndpointHandler` (or `let handler (id: Guid) : EndpointHandler` for `routef`). Dependencies come from `ctx.GetService<DbConfig>()`.
4. **Endpoint**: `let endpoint = GET [ route "/path" handler ]` with an explicit verb.

Then:
- add the file to `<Compile Include>` in `FrunchyBackend.fsproj` **after** `Views/Layout.fs` and **before** `Program.fs` (F# compile order);
- add `Handlers.<Slice>.endpoint` to the `endpoints` list in `Program.fs`.

Only cross-slice UI goes in `Views/` (currently just `Layout.fs`). See `Handlers/Dashboard.fs` (HTML) and `Handlers/Imports/CreateImport.fs` (JSON) as templates.

## Cheat sheet

```fsharp
open Oxpecker              // routing, handlers, ctx extensions
open Oxpecker.ViewEngine   // html DSL
open Oxpecker.Htmx         // .hxGet(...) etc. (htmx 4)
open Oxpecker.Alpine       // .xData(...) etc.

// Routing
GET [ route "/dashboard" handler ]                  // always give a verb; bare `route` matches ALL verbs
GET [ routef "/imports/{%O:guid}" handler ]         // handler: Guid -> EndpointHandler; bad guid → 404
routef "/u/{%s}/{%i}" (fun name age -> ...)         // %b %c %s %i %d(int64) %f %u %O:guid; ≤5 params
subRoute "/api" [ GET [ ... ]; POST [ ... ] ]
route "/old" (redirectTo "/new" false)
handlerA >=> handlerB                               // compose; routef needs >>=> / >>=>+ / >>=>++

// Reading the request
ctx.TryGetQueryValue "q"            // string option
ctx.TryGetRouteValue "id"           // string option
ctx.TryGetHeaderValue HxRequestHeader.Source
let! dto = ctx.BindJson<Dto>()      // BindForm needs [<CLIMutable>]; BindQuery is sync
match! ctx.BindAndValidateForm<Dto>() with
| ModelValidationResult.Valid v -> ...
| ModelValidationResult.Invalid (dto, errors) -> ...

// Writing the response
ctx.SetStatusCode 202
ctx.WriteHtmlView view   |  ctx.WriteJson obj  |  ctx.WriteText "s"
ctx.Write (TypedResults.NotFound())    // or in routes: %TypedResults.Ok x
htmlView view / json obj / text "s"     // EndpointHandler forms

// Views
div(class'="card", id="x") { "text"; 42; child() }   // reserved words get a trailing ': class' type' for'
div().attr("data-theme", "dark").data("k", "v").on("click", "js()")
raw "<b>unescaped</b>"                               // everything else is HTML-escaped
Fragment() { a; b }                                  // group without wrapper
for x in xs do li() { x }                            // loops/if/match work inside builders
match m with | A -> (span() { "a" } :> HtmlElement) | B -> Fragment()   // upcast when branch types differ

// htmx 4
button().hxPost("/imports").hxTarget("#status").hxSwap("outerHTML") { "Importar" }
div().hxGet($"/imports/{id}/status").hxTrigger("load delay:2s").hxSwap("outerHTML")   // polling
input().hxGet("/search").hxTrigger("keyup delay:200ms changed").hxTarget(HxSelector.closest "form")
body().hxBoost("true", HxModifier.inherited)

// Alpine
div().xData("{ open: false }") { button().xOn("click", "open = !open") { "Toggle" }; div().xShow("open") { "..." } }
```

## Gotchas

- `use` inside a handler disposes before the response is written unless the handler is a `task { }` that `return!`s the write.
- Handlers that write a body must not also fall through to another writer: `>=>` stops once the response has started.
- To return a fragment for htmx but a full page otherwise, branch on `ctx.TryGetHeaderValue HxRequestHeader.Request` (or `.Source`) as ContactApp does.
- CSRF: `AddAntiforgery()` + `UseAntiforgery()` (between `UseRouting` and `UseOxpecker`), then `ctx.GetAntiforgeryInput()` inside forms. It is enforced only when form binding runs.
- Long CE views slow compilation and IDE responsiveness. Split them into small functions.

## References (read on demand)

- `references/oxpecker-core.md`: handlers, middleware, routing, binding, validation, auth, CSRF, response writing, caching, static files, testing
- `references/viewengine.md`: elements, attributes, escaping, prerendering, rendering, ARIA, fragments
- `references/htmx.md`: full htmx 4 attribute list, extensions, selectors, request/response headers, migration notes from 2.x
- `references/alpine.md`: Alpine directive methods and modifier helpers
- `references/examples.md`: patterns from the upstream `examples/` (ContactApp, WeatherApp, CRUD, Basic) with code
