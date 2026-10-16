# Quickstart: validate feature 011

Contract: [compatibility](contracts/compatibility.md). Decisions: [research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker
```

| Suite | Proves |
|---|---|
| `CompatibilityScoreTests` (unit) | The level bands at each boundary. Cosine on known vectors. S against the smaller history, scaled down below 10 series; one shared series and no genres is Low. Score falls back to S when genres are missing. **Symmetry (SC-002)** on random inputs. |
| `CompatibilityTests` | **Figures (SC-001):** two users built from the 2026 sample (one full, one with a subset plus extra series), enriched with the 003 fixtures. Every card and page hook equals an independent query, in en and es. **Access:** no card when signed out or for the owner. The page sends signed-out visitors to sign-in and back, the viewer's own page redirects, and unknown users get 404. **States:** not enough history (each side, both); no series in common. **Symmetry:** both sides see the same level. **Speed (SC-003):** 2,000 episodes each, under 1 s. **Link (SC-004):** card → page. |
| `DesignSystemTests` | The card (on a dashboard) and the compare page are on the design system, with every contract hook. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

## 2. By eye

Sign in as one user and open another user's dashboard: the card shows. Follow it to the compare
page. Check both at 1280px and 360px, in English and in Spanish.

## Validation log (2026-10-16)

**Full `dotnet test`**: 418 total, 416 passed, 0 failed, 2 skipped (both pre-existing: the extension's
`tokens.css` isn't checked out here, and the opt-in MyAnimeList recording). T009 added one test, 415 → 416
passed. `dotnet build` is clean.

**Success criteria**

| | Evidence |
|---|---|
| SC-001 | `CompatibilityTests`: "the card on a dashboard shows every figure, equal to an independent computation…", "the page shows every figure, equal to an independent computation, from both sides", "the card reads in English and Spanish", "the page reads in English and Spanish with the same figures" |
| SC-002 | `CompatibilityScoreTests` (symmetry on random inputs); `CompatibilityTests`: "load reads both histories… and is symmetric", and both sides' cards show the same level (first test above) |
| SC-003 | `CompatibilityTests`: "a compare page between two users with 2,000 watched episodes each is ready in under a second" |
| SC-004 | `CompatibilityTests`: "the card's link opens the compare page" (one click) |
| SC-005 | `DesignSystemTests`: "the compatibility card and the compare page are on the design system and keep their test hooks" (no DaisyUI or retired values, every main hook, including a shared studio) plus the contrast tests; the impeccable audit below found nothing blocking or major |

**Impeccable audit** (card on `/u/ownerb` and `/u/ownerb/compare`, signed in as `viewera`, with the 2026
sample and the MyAnimeList fixtures; 1280 and 360 px, English and Spanish; also the not-enough-history and
no-genres states). Rendered through a throwaway test that dumped the HTML, then headless Chrome; the temporary
test was deleted. The detector (`impeccable detect`) reported nothing on `Compare.fs`, `Dashboard.fs` and
`Components.fs`.

| Severity | Finding | Outcome |
|---|---|---|
| P0, P1 | none. No overflow at 360, no clipped Spanish text, the meter, tags, bars and rows reflow cleanly, amber/rose roles hold, no percentage or score outside the genre shares | |
| P3 | Card at 1280: the left column (meter, sentence, count) is shorter than the right (tags and titles), leaving a little empty space under the button | Not fixed: reads as a balanced two-column card and a change would touch no behaviour; left as is |
| P3 | With a short history the card still shows "See how you compare", which opens a page saying the same thing | Not fixed: the contract keeps `data-compare-link` in every state |
| P3 | Genre rows where one person has nothing read `0.0%` with an empty track | Not fixed: an honest figure, and the contract fixes the one-decimal values |
| P3 (not UI) | Two users with 1 series in common and no genres can reach "Very high" (the score falls back to shared series alone), as the spec's fallback rule says; the page does explain the fallback | Not fixed: a scoring rule from the spec, not a view issue |

Screenshots (git-ignored): `.pitches/audit011/{card,compare}-{en,es}-{1280,360}.png`, plus
`compare-short-en-*`, `card-short-es-*` and `compare-loner-en-*` for the short-history and no-genres states.
