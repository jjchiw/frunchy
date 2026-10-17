# Quickstart: validate feature 013

Contract: [series page](contracts/series-page.md). Decisions: [research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker
```

| Suite | Proves |
|---|---|
| `SeriesPageTests` | **Fields (SC-001):** the 2026 sample for several users, enriched. Header, seasons (order, audio, dub, known counts, MyAnimeList once per entry, unmatched line), episodes (order, labels, links, comment counts with the thread's visibility) and both people counts at and below the minimum, against independent queries, in en and es. **404s.** **Seasonless and numberless episodes.** **A movie without a series row.** **Collapse:** above 30 episodes. **Speed (SC-002):** 1,100 synthetic episodes under 1 s. |
| `SeriesLinkTests` | **Links (SC-003):** chart row, episode page and anime page each link to the series page; the chart's "Your history" link only for a viewer who watched the series. |
| `UsernameTests` / sign-up / CLI tests | **Usernames (SC-004):** `abc-` refused at sign-up and by `user-create` (nothing created); `abc_` and `a-b` accepted; a stored `abc-` user's pages still work; the operator commands still find them. |
| `DesignSystemTests` | The series page is on the design system with its hooks. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

## 2. By eye

Open a series from `/charts`, then an episode, then back. Check a long series (One Piece) at 1280px
and 360px, in English and Spanish.
