# Quickstart: validate feature 012

Contract: [charts](contracts/charts.md). Decisions: [research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker
```

| Suite | Proves |
|---|---|
| `ChartsUnitTests` (unit) | **Season of a date**: all 4 quarters, the boundaries, UTC. **The minimum's reading**: missing, 0, 1, 2, 5. **Movement**: up, down, same, new. **The trend's blocks**: their bounds. |
| `ChartsTests` | **Figures (SC-001):** several users with controlled `watched_at` (via `TimeProvider` and direct SQL). Every row, rank, count, movement and trend equals an independent query, en and es. **Minimum (SC-002):** for minimums 2–5, no row or count below it. **Links:** `mine` links only for a signed-in viewer who watched the series; the Crunchyroll link only with a slug. **States:** an empty chart, the season boundary, the season name. **Counts:** the anime and episode pages show the count only at or above the minimum, and the episode count covers the dub group. **Speed (SC-003):** 2M synthetic watch events; each chart under 1 s after a warm-up. **Navigation (SC-004):** the header link on every page. |
| `DesignSystemTests` | The three chart pages are on the design system with their hooks. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

## 2. By eye

Open `/charts` with a few users' history. With fewer than 3 people per series locally, set
`Frunchy__Charts__MinPeople=2` to see rows. Check all three tabs, 1280 and 360px, en and es.

## Validation log (2026-10-16)

**Full `dotnet test`**: 462 total, 460 passed, 0 failed, 2 skipped (both pre-existing: the extension's
`tokens.css` isn't checked out here, and the opt-in MyAnimeList recording). T012 added one test, 459 → 460
passed. `dotnet build` is clean.

**Success criteria**

| | Evidence |
|---|---|
| SC-001 | `ChartsTests`: "every row of the three charts equals the figures computed from the watch rows, for minimums 2 to 5", "movement and the 8-week trend of every row of the week chart equal the figures from the watch rows", "up, down, same and new from two weeks of known ranks…", "the charts speak English and Spanish from the language header"; `ChartsUnitTests` for the pure functions |
| SC-002 | The same figures test (minimums 2 to 5, no row below the minimum), "people counts need the minimum and count each user once", and the anime and episode count tests ("…only at or above the minimum") |
| SC-003 | `ChartsTests`: "week and season answer in under a second, all time in under 4 seconds cold and under a second cached, over 2 million watch events". Measured: week about 0.08–0.14 s, season about 0.08 s, all time cold about 1.9–2.7 s, warm about 1 ms (setup of the 2M rows about 30 s). Also "the cached all-time rows carry no viewer: each viewer sees only their own links" |
| SC-004 | `ChartsTests`: "the Charts link is in every page's header, for visitors and signed-in users, and the tabs mark the active chart", "the viewer's link shows only for a signed-in viewer who watched the series, the Crunchyroll link only with a slug" |
| SC-005 | `DesignSystemTests`: "the chart pages are on the design system and keep their test hooks" (the three charts signed out and signed in, plus the empty state: no DaisyUI or retired values, every main hook) plus the contrast tests; the audit below |

**Impeccable audit** (the three charts signed out and signed in as a viewer who watched most charted series,
an anime page and an episode page with the count line, the empty state; 1280 and 360 px, English and
Spanish; MinPeople 2, fixed clock, 12 series with a long title, a title with `<Big>`, and a series without
slug or poster). Rendered through a throwaway test that dumped the raw HTML, then headless Chrome; the test
was deleted. The poster images are broken in the screenshots because the fixture's thumbnail URLs are fake.

| Severity | Finding | Outcome |
|---|---|---|
| P1 (outside the charts) | At 360 px a signed-in header overflows: with the new "Charts" link, "Sign out" / "Cerrar sesión" is clipped off the right edge on every page (seen on the charts, anime and episode pages). Cause: `Layout.fs` `navEnd` (T007), not the charts views | Not fixed here (the audit scope is `Charts.fs` and the count lines): needs a fix in `Views/Layout.fs`, e.g. let the right-hand group wrap or shrink the ghost buttons' padding at 360 |
| P3 | At 360 the chart row is tight: rank, poster, 64 px trend and the text leave about 140 px, so titles wrap to 3–4 lines and "7 personas · 12 episodios" wraps | Not fixed: readable, no overflow, and every element is part of the contract |
| P3 | The viewer's own link (`data-chart-mine`) looks like plain title text until hover | Not fixed: the contract fixes the link and hook, and an underline on every watched row would add noise |
| P3 | Rows without a poster keep the empty poster slot (row 3), so the title stays aligned with the others | Kept on purpose |
| none | No overflow or clipped text in the charts at 360; the long title wraps cleanly; the `<Big>` title is escaped (checked in the raw HTML); the week's movement, "New" pill and trend read correctly; the empty state, tabs, season name (Otoño 2026) and Spanish copy fit; the count lines on the anime and episode pages sit under the title block at both widths | |

Screenshots (git-ignored): `.pitches/audit012/{charts-week-in,charts-week-out,charts-season-out,charts-all-in,anime,episode,charts-empty}-{en,es}-{1280,360}.png`,
plus `top-*-360.png` crops of the header; the HTML dumps sit next to them.
