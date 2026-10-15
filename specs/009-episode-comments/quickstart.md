# Quickstart: validate feature 009

Contract: [episode page](contracts/episode-page.md). Rules: [data-model](data-model.md). Decisions:
[research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker (Testcontainers)
```

| Suite | Proves |
|---|---|
| `EpisodePageTests` | **Page (SC-001):** for 2026-sample episodes (matched, unmatched, dub, special without a number) every header field, the Crunchyroll links and the MyAnimeList block equal the stored catalog, en and es. **404s:** bad id alphabet (no query), unknown, unresolved, wrong series, lowercase. **Anime page:** every episode row links to its page. **Signed out:** the thread and a sign-in prompt with the return URL. |
| `CommentsTests` | **Create (SC-002):** two users comment; order, authors, times, the default-ticked spoiler box, the stored warning (S/E, E only, special label; en and es). **Spoilers closed on load (SC-003).** **Validation:** empty, whitespace, 2,001 characters, a bad id; nothing stored, 422, the text kept. **Double submit:** one row. **Plain text:** markup is escaped, line breaks kept. **Rate limit:** 429 on the 11th write a minute. **Rebuild:** `user-rebuild` and re-sending history leave comments unchanged. **Account deletion:** the author's comments go. **1,000 comments (SC-005):** the page stays under the bound. |
| `CommentThreadGroupTests` | **Dubs (SC-004):** with the feature 003 fixtures, comments on an original and its dub show on both pages; removing the dub link regroups without losing or changing a row; numberless and seasonless episodes keep their own thread. |
| `CommentEditTests` | **Edit/delete:** author only; edited mark; summary recomputed; audience unchanged; the tombstone hides the comment and keeps its id. **Others (SC-006):** another user, a signed-out visitor and an operator get 404 on edit and delete, and nothing changes. |
| `CommentModerationTests` | **Access:** visitors and users get 404 on hide/unhide, with and without a token, never 429. **Hide/unhide:** the placeholder for outsiders and the author, the text for operators; the events on both accounts with the right actor; an author can edit and delete a hidden comment; the hide survives the operator's account deletion. |
| `DesignSystemTests` | The episode page and the edit page are on the design system, with every hook from the contract. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

## 2. On real data

```bash
bella sdk run -- dotnet run --launch-profile http
open http://127.0.0.1:5099/u/<you>
```

1. **Anime page:** open a series and click an episode title; the episode page shows its header,
   Crunchyroll links and, if matched, the MyAnimeList block.
2. **Spoiler comment:** write one with the box left ticked. It shows the warning, closed. Open it.
3. **Plain comment:** untick the box and write another. It shows directly.
4. **Edit and delete:** edit the first comment (the "edited" mark appears), then delete the second.
5. **Dubbed series:** open the dub's episode page; the original's comments are there.
6. **Signed out:** in a private window, the thread is readable and the box is replaced by a sign-in
   prompt that comes back to the page.
7. **As an operator** (`Frunchy__RootOperator=<you>`): hide a comment. Signed out it reads "Hidden by
   an operator"; unhide it. `/account` shows the hide and unhide events.
8. **Impeccable audit:** check the §5.5 variants (no comments, long thread, hidden comment, signed
   out, Spanish, 360px).
