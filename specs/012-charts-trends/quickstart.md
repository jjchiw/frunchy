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
