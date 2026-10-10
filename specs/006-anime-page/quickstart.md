# Quickstart: validate feature 006

Contract: [anime-page](contracts/anime-page.md). Rules: [data-model](data-model.md). Decisions:
[research](research.md).

## 1. Automated validation

```bash
dotnet test
```

| Suite | Proves |
|---|---|
| `AnimeTests` (12) | **Figures (SC-001):** for a series with several seasons, a dub pair and an unmatched series, every total, season and episode equals an independent count. **Totals (SC-002):** the dashboard's rule, restricted to the series; series pages sum to the dashboard's episodes. **States:** an all-partial series; an all-unresolved series (404); bad ids and unknown users (404). **Links:** the Crunchyroll link with and without a slug. **Shared entries:** a MyAnimeList entry shared by several seasons is shown once. **Other:** en and es; identical after `user-rebuild` and a re-send; no MyAnimeList call. |
| `AnimeLinkTests` (3) | Every series title on the dashboard (cards and newest episode), the DNA page (insignia, genre top anime) and the year page (top series, genre top anime) links to its anime page, which answers 200 (SC-004). |
| `DesignSystemTests` | The anime page has no DaisyUI and keeps its hooks (SC-005). |

## 2. On real data

```bash
bella sdk run -- dotnet run --launch-profile http
open http://127.0.0.1:5099/u/jjchiw/anime/GRMG8ZQZR
```

## Validation log

- **2026-10-10**:
  - **Suite**: `dotnet test` gives **287 passed, 1 skipped** (the opt-in recording test), with 0
    build warnings.
  - **Delegation**: the code was written by a Sonnet agent and reviewed by the main session. The
    review sent back one fix: a MyAnimeList entry shared by several seasons repeated its whole block
    on every season, and it now shows once (`data-mal-compact` on the rest).
  - **`/u/jjchiw/anime/GRMG8ZQZR` (One Piece) on the real database**:
    - 266 episodes, 106.4 h, first watch Feb 14 2021, latest Sep 29 2026, 7 seasons.
    - The full MyAnimeList block appears once, with 6 compact ones.
    - Timing (SC-003): 216 ms on the first request, then 30–35 ms.
    - A lowercase id gets 404.
    - Checked in the cmux browser at 1440px. The agent checked 360px in Spanish through an iframe.
