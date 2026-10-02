module FrunchyBackend.Views.Layout

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Views.Texts

/// Rewrites every <time data-fr-time> into the viewer's language and time zone.
/// data-fr-time="relative" → "2 hours ago · 30 Sep 2026, 23:46"; any other value → the absolute part only.
/// Without JavaScript the server-rendered UTC fallback stays (contracts/dashboard-ui.md).
let private timeScript =
    """
document.addEventListener('DOMContentLoaded', function () {
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
    el.textContent = el.getAttribute('data-fr-time') === 'relative' ? relative(date) + ' · ' + absolute : absolute;
    el.title = date.toISOString();
  });
});
"""

/// Navbar right side: "Sign in", or the signed-in user's name, Account and a sign-out form (POST + antiforgery)
let private navEnd (ctx: HttpContext) (t: Texts) =
    match WebAuth.currentNav ctx with
    | WebAuth.SignedOut -> div(class' = "flex-none") { a(href = "/signin", class' = "btn btn-ghost btn-sm") { t.NavSignIn } }
    | WebAuth.SignedIn displayName ->
        div(class' = "flex-none flex items-center gap-2") {
            span(class' = "text-sm text-gray-400").data("nav", "user") { displayName }
            a(href = "/account", class' = "btn btn-ghost btn-sm") { t.NavAccount }

            form(method = "post", action = "/signout") {
                ctx.GetAntiforgeryInput()
                button(type' = "submit", class' = "btn btn-ghost btn-sm") { t.NavSignOut }
            }
        }

/// Exact instant for the viewer's browser to localize (the time script above); UTC fallback text without JS.
/// Shared by the dashboard and the account page.
let timeElement (t: Texts) (mode: string) (instant: System.DateTime) =
    let utc = System.DateTime.SpecifyKind(instant, System.DateTimeKind.Utc)

    time()
        .attr("datetime", utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
        .data("fr-time", mode) {
        utc.ToString("d MMM yyyy, HH:mm", t.Culture) + " UTC"
    }

/// Base HTML layout with DaisyUI + HTMX, in the request's language
let page (ctx: HttpContext) (t: Texts) (titleText: string) (content: HtmlElement) =
    html(lang = t.Lang) {
        head() {
            title() { $"{titleText} | {t.SiteName}" }
            meta(charset = "UTF-8")
            meta(name = "viewport", content = "width=device-width, initial-scale=1.0")
            script(src = "https://cdn.tailwindcss.com")
            link(
                href = "https://cdn.jsdelivr.net/npm/daisyui@4.11.1/dist/full.min.css",
                rel = "stylesheet",
                type' = "text/css"
            )
            script(src = "https://cdn.jsdelivr.net/npm/htmx.org@4.0.0", crossorigin = "anonymous")
            script(defer = true, src = "https://cdn.jsdelivr.net/npm/alpinejs@3.x.x/dist/cdn.min.js")
            style() { "body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif; }" }
            script() { raw timeScript }
        }
        body(class' = "bg-base-100").attr("data-theme", "dark") {
            div(class' = "navbar bg-base-200 sticky top-0 z-50 shadow-lg") {
                div(class' = "flex-1") { a(href = "/", class' = "btn btn-ghost text-xl") { $"🎬 {t.SiteName}" } }
                navEnd ctx t
            }
            div(class' = "container mx-auto px-4 py-8") { content }
            footer(class' = "footer footer-center bg-base-200 text-base-content p-4 mt-16") {
                div() { p() { t.Footer } }
            }
        }
    }

/// Simple message page (404, 500) in the request's language
let messagePage (ctx: HttpContext) (t: Texts) (heading: string) (body: string) =
    div(class' = "hero bg-base-200 rounded-lg") {
        div(class' = "hero-content text-center") {
            div(class' = "max-w-md") {
                h1(class' = "text-4xl font-bold") { heading }
                p(class' = "py-6") { body }
                a(href = "/", class' = "btn btn-primary") { t.BackHome }
            }
        }
    }
    |> page ctx t heading
