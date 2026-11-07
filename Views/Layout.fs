module FrunchyBackend.Views.Layout

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views.Texts

/// Rewrites every <time data-fr-time> into the viewer's language and time zone.
/// data-fr-time="relative" → "2 hours ago · 30 Sep 2026, 23:46"; "relative-only" → "2 hours ago", with the absolute
/// date and time in the element's title; any other value → the absolute part only.
/// Without JavaScript the server-rendered UTC fallback stays (contracts/dashboard-ui.md).
let private timeScript =
    """
document.addEventListener('DOMContentLoaded', function () {
  // a profile's tab bar scrolls on a phone: bring the current tab into view
  document.querySelector('nav.fr-tabs [aria-current]')?.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  var lang = document.documentElement.lang || 'en';
  var rtf = new Intl.RelativeTimeFormat(lang, { numeric: 'auto' });
  var dtf = new Intl.DateTimeFormat(lang, { dateStyle: 'medium', timeStyle: 'short' });
  var units = [['year', 31536000], ['month', 2592000], ['week', 604800], ['day', 86400],
               ['hour', 3600], ['minute', 60], ['second', 1]];
  var dayStart = function (ms) { var d = new Date(ms); d.setHours(0, 0, 0, 0); return d.getTime(); };
  var relative = function (date) {
    var seconds = (date.getTime() - Date.now()) / 1000;
    // 1-7 days: calendar days in the viewer's time zone, so "yesterday" means the previous local date
    if (Math.abs(seconds) >= 86400 && Math.abs(seconds) < 604800)
      return rtf.format(Math.round((dayStart(date.getTime()) - dayStart(Date.now())) / 86400000), 'day');
    var unit = units.find(function (u) { return Math.abs(seconds) >= u[1]; }) || units[units.length - 1];
    return rtf.format(Math.round(seconds / unit[1]), unit[0]);
  };
  document.querySelectorAll('time[data-fr-time]').forEach(function (el) {
    var date = new Date(el.getAttribute('datetime'));
    if (isNaN(date.getTime())) return;
    var absolute = dtf.format(date);
    var mode = el.getAttribute('data-fr-time');
    if (mode === 'relative-only') {
      el.textContent = relative(date);
      el.title = absolute;
    } else {
      el.textContent = mode === 'relative' ? relative(date) + ' · ' + absolute : absolute;
      el.title = date.toISOString();
    }
  });
});
"""

/// Header buttons: a little less padding on phones, so the signed-in header fits at 360px in Spanish too
let private navBtn = Components.btnSm Components.Ghost + " px-2 sm:px-3.5"

/// The charts link every visitor sees (feature 012)
let private chartsLink (t: Texts) =
    a(href = "/charts", class' = navBtn).data("nav", "charts") { t.NavCharts }

/// Navigation, right side: "Sign in", or the signed-in user's name (linking to their public dashboard), Account and a
/// sign-out form (POST + antiforgery)
let private navEnd (ctx: HttpContext) (t: Texts) =
    match WebAuth.currentNav ctx with
    | WebAuth.SignedOut ->
        div(class' = "flex items-center gap-1 sm:gap-2") {
            chartsLink t
            a(href = "/signin", class' = Components.btnSm Components.Primary) { t.NavSignIn }
        }
        :> HtmlElement
    | WebAuth.SignedIn displayName ->
        div(class' = "flex min-w-0 items-center gap-0.5 sm:gap-2") {
            let name = "hidden max-w-[16ch] truncate text-label-md text-on-surface-variant sm:inline"

            match WebAuth.currentUsername ctx with
            | Some username ->
                a(href = $"/u/{username}", class' = name + " hover:text-on-surface hover:underline").data("nav", "user") {
                    displayName
                }
            | None -> span(class' = name).data("nav", "user") { displayName }

            chartsLink t
            a(href = "/account", class' = navBtn) { t.NavAccount }

            form(method = "post", action = "/signout") {
                ctx.GetAntiforgeryInput()
                button(type' = "submit", class' = navBtn) { t.NavSignOut }
            }
        }
        :> HtmlElement

/// Exact instant for the viewer's browser to localize (the time script above); UTC fallback text without JS.
/// Shared by the dashboard and the account page.
let timeElement (t: Texts) (mode: string) (instant: System.DateTime) =
    let utc = System.DateTime.SpecifyKind(instant, System.DateTimeKind.Utc)

    time()
        .attr("datetime", utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
        .data("fr-time", mode) {
        utc.ToString("d MMM yyyy, HH:mm", t.Culture) + " UTC"
    }

/// A `<link>` a slice wants in the page's head (feature 015: the alternate ActivityPub representation)
type HeadLink = { Rel: string; Type: string option; Href: string }

/// The `ctx.Items` key `page` reads its extra head links from, so slices don't change the page signature
[<Literal>]
let HeadLinksKey = "frunchy.headLinks"

let private headLinks (ctx: HttpContext) : HeadLink list =
    match ctx.Items.TryGetValue HeadLinksKey with
    | true, (:? (HeadLink list) as links) -> links
    | _ -> []

/// Ask the page rendered later in this request to carry one more `<link>` in its head
let addHeadLink (ctx: HttpContext) (link: HeadLink) =
    ctx.Items[HeadLinksKey] <- headLinks ctx @ [ link ]

/// Advertise the ActivityPub representation of what this page is about (feature 015, FR-003, FR-009): the
/// `<link rel="alternate" type="application/activity+json">` in the head, and the same as a `Link` response header
let advertiseActivityPub (ctx: HttpContext) (href: string) =
    addHeadLink ctx { Rel = "alternate"; Type = Some "application/activity+json"; Href = href }
    ctx.Response.Headers["Link"] <- $"<{href}>; rel=\"alternate\"; type=\"application/activity+json\""

/// The footer's language link (feature 024): one link to the OTHER language, in that language's own name (never a
/// flag). It sets the `frunchy.lang` cookie and comes back to this page. `data-language` is the target language.
let private languageSwitch (ctx: HttpContext) (t: Texts) =
    let target = LanguagePreference.other t.Lang
    let name = if target = "es" then t.LanguageNameEs else t.LanguageNameEn
    let here = ctx.Request.Path.Value + ctx.Request.QueryString.Value

    nav().attr("aria-label", t.LanguageSwitchLabel).data("language-switch", "") {
        a(href = LanguagePreference.switchHref target here, class' = Components.btnSm Components.Ghost + " -ml-3.5 sm:ml-0")
            .attr("hreflang", target)
            .attr("lang", target)
            .data("language", target) {
            Components.icon Theme.Language
            name
        }
    }

/// The footer's theme switch (feature 032): Light, Dark and System as a segmented control of three words (no icons:
/// "System" has none people read reliably). Plain links that set `frunchy.theme` and come back to this page; the
/// current mode is `aria-current="true"` and reads as the neutral selected segment, not amber.
let private themeSwitch (ctx: HttpContext) (t: Texts) =
    let here = ctx.Request.Path.Value + ctx.Request.QueryString.Value
    let current = ThemePreference.current ctx

    nav().attr("aria-label", t.ThemeSwitchLabel).data("theme-switch", "") {
        div(class' = "inline-flex items-center rounded-full p-0.5").attr("style", "border: var(--border-ghost)") {
            for mode in ThemePreference.all do
                let label =
                    match mode with
                    | ThemePreference.Light -> t.ThemeLight
                    | ThemePreference.Dark -> t.ThemeDark
                    | ThemePreference.System -> t.ThemeSystem

                let isCurrent = (mode = current)
                let size = "inline-flex h-8 items-center rounded-full px-3 text-label-md"

                let cls =
                    if isCurrent then size + " bg-surface-container-high text-on-surface"
                    else size + " text-on-surface-variant hover:text-on-surface"

                let link = a(href = ThemePreference.switchHref mode here, class' = cls).data("theme-option", mode.Code)
                let link = if isCurrent then link.attr("aria-current", "true") else link
                link { label }
        }
    }

/// Base HTML layout on the design system (Views/Theme.fs), in the request's language
let page (ctx: HttpContext) (t: Texts) (titleText: string) (content: HtmlElement) =
    let theme = ThemePreference.current ctx

    html(lang = t.Lang, class' = "bg-surface").attr("data-theme", theme.Code) {
        head() {
            meta(charset = "UTF-8")
            meta(name = "viewport", content = "width=device-width, initial-scale=1.0")
            meta(name = "color-scheme", content = (match theme with ThemePreference.Light -> "light" | ThemePreference.Dark -> "dark" | ThemePreference.System -> "dark light"))

            let surfaceOf (palette: (string * string) list) = palette |> List.find (fst >> (=) "surface") |> snd

            match theme with
            | ThemePreference.Light -> meta(name = "theme-color", content = surfaceOf Theme.lightColors)
            | ThemePreference.Dark -> meta(name = "theme-color", content = surfaceOf Theme.colors)
            | ThemePreference.System ->
                meta(name = "theme-color", content = surfaceOf Theme.lightColors).attr("media", "(prefers-color-scheme: light)")
                meta(name = "theme-color", content = surfaceOf Theme.colors).attr("media", "(prefers-color-scheme: dark)")
            title() { $"{titleText} | {t.SiteName}" }
            link(rel = "icon", href = Theme.faviconHref).attr("type", "image/svg+xml")

            for extra in headLinks ctx do
                match extra.Type with
                | Some mediaType -> link(rel = extra.Rel, href = extra.Href).attr("type", mediaType)
                | None -> link(rel = extra.Rel, href = extra.Href)

            link(rel = "preconnect", href = "https://fonts.googleapis.com")
            link(rel = "preconnect", href = "https://fonts.gstatic.com").attr("crossorigin", "")

            for sheet in Theme.fontStylesheets do
                link(rel = "stylesheet", href = sheet)

            style() { raw Theme.tokensCss }
            script(src = "https://cdn.tailwindcss.com")
            script() { raw Theme.tailwindConfig }
            style().attr("type", "text/tailwindcss") { raw Theme.componentCss }
            script(src = "https://cdn.jsdelivr.net/npm/htmx.org@4.0.0", crossorigin = "anonymous")
            script(defer = true, src = "https://cdn.jsdelivr.net/npm/alpinejs@3.x.x/dist/cdn.min.js")
            script() { raw timeScript }
        }

        body(class' = "min-h-screen bg-surface text-on-surface flex flex-col") {
            a(href = "#main", class' = "sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-[60] fr-btn fr-btn-primary") {
                t.SkipToContent
            }

            header(class' = "sticky top-0 z-50 border-b border-outline-variant/40 bg-surface/85 backdrop-blur-md") {
                nav(class' = "mx-auto flex h-16 max-w-page items-center justify-between gap-4 px-4 md:px-8") {
                    Components.brand t.SiteName
                    navEnd ctx t
                }
            }

            main(id = "main", class' = "mx-auto w-full max-w-page flex-1 px-4 py-8 md:px-8 md:py-12") { content }

            footer(class' = "border-t border-outline-variant/40") {
                div(class' = "mx-auto flex max-w-page flex-wrap items-center gap-3 px-4 py-8 text-label-md font-normal text-on-surface-variant md:px-8") {
                    // The mark and the two lines stay together; only the language link wraps onto its own line
                    div(class' = "flex items-center gap-3") {
                        span(class' = "block h-5 w-5 shrink-0 opacity-80") { raw Theme.markSvg }
                        div(class' = "min-w-0 space-y-1") {
                            p() { t.Footer }
                            p().data("footer-disclaimer", "") { t.FooterDisclaimer }
                        }
                    }
                    // The two switches stay together and wrap as one group
                    div(class' = "flex w-full flex-wrap items-center gap-x-3 gap-y-2 sm:ml-auto sm:w-auto") {
                        themeSwitch ctx t
                        languageSwitch ctx t
                    }
                }
            }
        }
    }

/// Short message page (404, 500, rate limit, expired form) in the request's language
let messagePage (ctx: HttpContext) (t: Texts) (heading: string) (body: string) =
    div(class' = "flex min-h-[50vh] items-center justify-center") {
        div(class' = "fr-card w-full max-w-md text-center space-y-4") {
            span(class' = "mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-surface-container-high text-headline-lg text-primary") {
                Components.icon Theme.ExploreOff
            }

            h1(class' = "text-headline-lg text-on-surface text-balance") { heading }
            p(class' = "text-body-lg text-on-surface-variant") { body }

            a(href = "/", class' = Components.btn Components.Primary) {
                Components.icon Theme.Home
                t.BackHome
            }
        }
    }
    |> page ctx t heading

/// The plain 404 page the review and operator pages answer to everyone who isn't allowed in
let private notFound (ctx: HttpContext) =
    let t = Texts.current ()
    ctx.SetStatusCode StatusCodes.Status404NotFound
    ctx.WriteHtmlView(messagePage ctx t t.NotFoundTitle t.NotFoundBody)

/// Run the handler for a signed-in operator (the root, or a user in the `operators` table); anyone
/// else gets the plain 404 page, never a redirect, so the review pages can't be found. Wrap it around
/// everything, before any form is read.
let requireOperator (next: System.Guid -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        match WebAuth.currentUserId ctx with
        | Some userId when WebAuth.isOperator ctx -> next userId ctx
        | _ -> notFound ctx

/// Like `requireOperator`, but only the root operator (`Frunchy:RootOperator`) gets in: an operator
/// who isn't root gets the same 404 (feature 008).
let requireRoot (next: System.Guid -> EndpointHandler) : EndpointHandler =
    fun ctx ->
        match WebAuth.currentUserId ctx with
        | Some userId when WebAuth.isRoot ctx -> next userId ctx
        | _ -> notFound ctx
