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

## Validation log (2026-10-17)

**Full `dotnet test`: 496 total, 494 passed, 0 failed, 2 skipped** (both pre-existing: the extension tokens
parity check, which needs `../browser-extension`, and the opt-in MyAnimeList recording). 493 passed before
T011; the new test is the series page in the every-page design check. `dotnet build` has 0 warnings.

| Criterion | Evidence |
|---|---|
| SC-001 fields | `SeriesPageTests`: every series of the 2026 sample in en and es against independent queries; the multi-season, dub, unmatched and shared-entry series; MyAnimeList once per entry and the poster rule; comment counts with the thread's visibility; people counts at and below the minimum; a movie without a series row, seasonless and numberless episodes; the 404s |
| SC-002 speed | `a series of 1,100 episodes is ready in under a second`: about 35 ms warm (limit 1 s) |
| SC-003 links | `SeriesLinkTests`: `a chart row's title links to the series page for everyone, and Your history shows only for a viewer who watched it`, `the episode page's series title links to the series page, which opens`, `a person's anime page links to the series page` |
| SC-004 usernames | `IdentityTests` (`new names refuse a trailing dash while lookups still accept it`), `SignUpTests` (`a trailing dash is refused with the username error and creates nothing, but _ and inner dashes are fine`), `AccountActionsTests` (`user-create refuses a trailing dash with the bad-username code and creates nothing`), `OperatorActionsTests` (`an account whose name ends in a dash (made before the rule) keeps its dashboard and operator-add finds it`) |
| SC-005 design | `DesignSystemTests`: `the series page is on the design system and keeps its test hooks` (a multi-season series with a dub and MyAnimeList hooks, a 31-episode collapsed season, the 404) and the chart rows' `data-series-page-link`; impeccable audit below |

**Timed tests.** The charts test module (its 2M-row speed test) is sensitive to
load, so the charts module runs in a non-parallel collection (`Infrastructure/Collections.fs`). Run only one
`dotnet test` at a time.

**Impeccable audit** (a throwaway test rendered the raw HTML, headless Chrome shot it at 1280 and an iframe
360, en and es: a multi-season series with a dub, a long series with a 46-episode season shown closed and
open, a series nobody watches, the episode page, a person's anime page, the charts with signed-in and
signed-out rows; the detector on `Handlers/Series/Series.fs` reported nothing). No P0 or P1 finding.

| Severity | Finding | Outcome |
|---|---|---|
| P2 | The closed season's `<summary>` was 24 px tall, under the 44 px touch target | Fixed: `py-3`, no `min-h-6` (`Handlers/Series/Series.fs`) |
| P2 | The chart row title is a link but looks like plain text until hover, next to two underlined amber links | Not fixed: the title as the row's main link is the usual pattern and keeps its hover underline; revisit with the charts' design |
| P3 | A season whose episode title repeats its label reads "Episode 1 Episode 1" | Not fixed: fixture data only, real titles differ |
| P3 | The poster shows as a broken box | Not a defect: the fixtures use fake image URLs; real images load |
| n/a | The "this week" line doesn't appear in the screenshots | Expected: the sample's dates are older than 7 days |

Screenshots and the raw HTML: `.pitches/audit013/` (git-ignored), named `<page>-<en|es>-<1280|360>.png`.
