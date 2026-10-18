/// Feature 015 (US6): the pieces the account page's block section and the operator page share: the notice, the
/// add form and the list with a remove button per block. Hooks: `data-block-add`, `data-block-remove`, `data-block`
/// (the actor id or the domain), `data-block-error`, `data-block-notice`.
module FrunchyBackend.Views.Blocks

open Microsoft.AspNetCore.Http
open Oxpecker
open Oxpecker.ViewEngine
open FrunchyBackend.Infrastructure
open FrunchyBackend.Infrastructure.BlockActions
open FrunchyBackend.Views.Components
open FrunchyBackend.Views.Texts

let private successKeys = set [ "block-added"; "block-removed" ]

let private noticeKeys =
    set [ "block-added"; "block-already"; "block-removed"; "block-not-found"; "block-not-blocked"; "block-blocked"
          "block-unreachable"; "block-invalid"; "block-error" ]

/// Whether `msg` is one of ours (the account page shows these inside the section, not at its top)
let isBlockNotice (msg: string) = noticeKeys.Contains msg

/// The result of the last change: what happened, or why nothing did (a refusal carries `data-block-error`)
let notice (t: Texts) (msg: string) : HtmlElement =
    if not (noticeKeys.Contains msg) then
        Fragment() :> HtmlElement
    elif successKeys.Contains msg then
        (alert Success (t.BlockNotice msg)).data("block-notice", msg) :> HtmlElement
    elif msg = "block-already" then
        (alert Notice (t.BlockNotice msg)).data("block-notice", msg) :> HtmlElement
    else
        (alert Failure (t.BlockNotice msg)).data("block-error", msg) :> HtmlElement

/// One field that takes an account (a handle or its address) or a whole server, and the Block button
let addForm (ctx: HttpContext) (t: Texts) (action: string) : HtmlElement =
    form(method = "post", action = action, class' = "space-y-4").data("block-add", "") {
        ctx.GetAntiforgeryInput()

        div(class' = "w-full sm:max-w-lg") {
            fieldWith
                "text"
                []
                (fun i -> i.attr("required", "").attr("autocomplete", "off").attr("autocapitalize", "none").attr("spellcheck", "false") |> ignore)
                t.BlocksAddLabel
                "target"
                ""
                (Some t.BlocksAddHint)
                None
                true
        }

        button(type' = "submit", class' = btn Secondary) {
            icon Theme.Block
            t.BlocksAddButton
        }
    }

let private blockRow (ctx: HttpContext) (t: Texts) (removeAction: string) (item: MegafonoClient.BlockItem) : HtmlElement =
    let kind, value =
        match item.Actor, item.Domain with
        | Some actor, _ -> "actor", actor
        | None, Some domain -> "domain", domain
        | None, None -> "", ""

    li(class' = "flex flex-wrap items-center justify-between gap-x-4 gap-y-3 py-3 first:pt-0 last:pb-0").data("block", value).data("kind", kind) {
        div(class' = "flex min-w-0 flex-col items-start gap-1.5") {
            pill (if kind = "actor" then TertiaryTone else Muted) (if kind = "actor" then t.BlockKindAccount else t.BlockKindServer)
            span(class' = "fr-mono text-body-md text-on-surface [overflow-wrap:anywhere]") { value }
        }

        form(method = "post", action = removeAction).data("block-remove", "") {
            ctx.GetAntiforgeryInput()
            input(type' = "hidden", name = "kind", value = kind)
            input(type' = "hidden", name = "target", value = value)

            button(type' = "submit", class' = btnSm Secondary).attr("aria-label", t.BlocksRemoveLabel value) {
                icon Theme.LinkOff
                t.BlocksRemoveButton
            }
        }
    }
    :> HtmlElement

/// The list of blocks, or the empty line, or the plain line saying Megáfono can't be asked right now
let list
    (ctx: HttpContext)
    (t: Texts)
    (hook: string)
    (removeAction: string)
    (emptyText: string)
    (failedText: string)
    (listing: Listing)
    : HtmlElement =
    div().data(hook, "") {
        match listing with
        | Unavailable ->
            p(class' = "flex items-start gap-2 text-body-md text-on-surface-variant").data("block-load-failed", "") {
                span(class' = "text-on-surface-variant") { icon Theme.Warning }
                failedText
            }
        | Loaded [] -> p(class' = "text-body-md text-on-surface-variant").data("blocks-empty", "") { emptyText }
        | Loaded items ->
            ul(class' = "divide-y divide-outline-variant/40") {
                for item in items do
                    blockRow ctx t removeAction item
            }
    }
