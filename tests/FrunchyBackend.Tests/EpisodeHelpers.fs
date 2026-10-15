/// Helpers for the episode page and comments tests (feature 009, contracts/episode-page.md). They read the page
/// through its `data-*` hooks only, never classes.
module FrunchyBackend.Tests.EpisodeHelpers

open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open FrunchyBackend.Infrastructure.Database
open FrunchyBackend.Tests.Fixtures
open FrunchyBackend.Tests.Infrastructure.Browser
open FrunchyBackend.Tests.Infrastructure.PostgresFixture

/// One comment of a thread, as the page shows it
type PageComment =
    { Id: string
      /// `data-author`: the author's username ("" when the page doesn't say)
      Author: string
      /// `data-episode`: the episode the comment was written on
      Episode: string
      /// `data-created` (ISO)
      Created: string
      /// A `data-spoiler` element is inside the comment
      Spoiler: bool
      /// `data-edited` is inside the comment
      Edited: bool
      /// `data-hidden` is inside the comment
      Hidden: bool
      /// The comment's text: its HTML with tags removed, entities decoded and whitespace collapsed
      Text: string
      /// The comment's whole element, for finer checks
      Html: string }

let episodeUrl (seriesId: string) (episodeId: string) = $"/anime/{seriesId}/episode/{episodeId}"

/// GET the episode page in a language (an Accept-Language value such as "en" or "es"): its status and decoded HTML
let episodePage (app: TestApp) (language: string) (seriesId: string) (episodeId: string) : Task<HttpStatusCode * string> =
    task {
        let! response = getWithLanguage app.Client language (episodeUrl seriesId episodeId)
        let! html = readHtml response
        return response.StatusCode, html
    }

let private attributeOf (hook: string) (tag: string) =
    let m = Regex.Match(tag, $"\\bdata-{Regex.Escape hook}=\"([^\"]*)\"")
    if m.Success then m.Groups[1].Value else ""

/// Does the HTML carry this hook, with or without a value (`data-spoiler` but not `data-spoiler-summary`)
let hasHook (hook: string) (html: string) =
    Regex.IsMatch(html, $"\\bdata-{Regex.Escape hook}(?![-\\w])")

let private stripTags (html: string) =
    let text = Regex.Replace(html, "<[^>]*>", " ")
    Regex.Replace(WebUtility.HtmlDecode text, "\\s+", " ").Trim()

/// The element that opens at `start` (an opening tag), up to and including its matching closing tag
let private elementFrom (html: string) (start: int) =
    let openTag = Regex.Match(html.Substring start, "^<([a-zA-Z0-9]+)\\b[^>]*>")
    let name = openTag.Groups[1].Value
    let tags = Regex.Matches(html.Substring(start + openTag.Length), $"<(/?){name}\\b[^>]*>")
    let mutable depth = 1
    let mutable stop = html.Length

    for m in tags do
        if stop = html.Length then
            if m.Groups[1].Value = "/" then depth <- depth - 1 else depth <- depth + 1

            if depth = 0 then
                stop <- start + openTag.Length + m.Index + m.Length

    openTag.Value, html.Substring(start, stop - start)

/// Every `data-comment` of the page, in page order, with the attributes and the text the tests compare
let comments (html: string) : PageComment list =
    [ for m in Regex.Matches(html, "<[a-zA-Z0-9]+\\b[^>]*\\bdata-comment=\"[^\"]+\"[^>]*>") do
          let tag, element = elementFrom html m.Index

          { Id = attributeOf "comment" tag
            Author = attributeOf "author" tag
            Episode = attributeOf "episode" tag
            Created = attributeOf "created" tag
            Spoiler = hasHook "spoiler" element
            Edited = hasHook "edited" element
            Hidden = hasHook "hidden" element
            Text = stripTags element
            Html = element } ]

/// The comment form's `id` input: minted with each render of the form (research R3)
let formCommentId (html: string) : string option =
    Regex.Matches(html, "<input\\b[^>]*>")
    |> Seq.map (fun m -> m.Value)
    |> Seq.tryFind (fun tag -> Regex.IsMatch(tag, "\\bname=\"id\""))
    |> Option.map (fun tag -> WebUtility.HtmlDecode(Regex.Match(tag, "\\bvalue=\"([^\"]*)\"").Groups[1].Value))

/// Post a comment as the browser's signed-in user: read the form's `id` from the episode page, then post it with
/// the antiforgery token and `spoiler=on` when ticked. No redirect following.
let postComment (browser: Browser) (seriesId: string) (episodeId: string) (text: string) (spoiler: bool) : Task<HttpResponseMessage> =
    task {
        let page = episodeUrl seriesId episodeId
        let! form = browser.Get page
        let id = formCommentId form.Html |> Option.defaultValue ""

        let fields =
            [ "id", id; "content", text ] @ (if spoiler then [ "spoiler", "on" ] else [])

        return! browser.PostForm(page, $"{page}/comments", fields)
    }

/// The whole `comments` table as text lines, oldest first, for equality checks
let rawCommentRows (db: DbConfig) : Task<string list> =
    Db.dump
        db
        """SELECT id, cr_episode_id, coalesce(author_user_id::text, '-'), coalesce(remote_actor, '-'), content, format,
                  coalesce(summary, '-'), sensitive, coalesce(in_reply_to, '-'), audience, coalesce(object_id, '-'),
                  created_at, coalesce(edited_at::text, '-'), coalesce(deleted_at::text, '-'),
                  coalesce(hidden_at::text, '-'), coalesce(hidden_by::text, '-')
           FROM comments ORDER BY created_at, id"""
