# Quickstart: validate feature 011

Contract: [compatibility](contracts/compatibility.md). Decisions: [research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker
```

| Suite | Proves |
|---|---|
| `CompatibilityScoreTests` (unit) | The level bands at each boundary. Cosine on known vectors. S against the smaller history. Score falls back to S when genres are missing. **Symmetry (SC-002)** on random inputs. |
| `CompatibilityTests` | **Figures (SC-001):** two users built from the 2026 sample (one full, one with a subset plus extra series), enriched with the 003 fixtures. Every card and page hook equals an independent query, in en and es. **Access:** no card when signed out or for the owner. The page sends signed-out visitors to sign-in and back, the viewer's own page redirects, and unknown users get 404. **States:** not enough history (each side, both); no series in common. **Symmetry:** both sides see the same level. **Speed (SC-003):** 2,000 episodes each, under 1 s. **Link (SC-004):** card → page. |
| `DesignSystemTests` | The card (on a dashboard) and the compare page are on the design system, with every contract hook. |
| `SqlGuardrailTests` | The new SQL is literal and parameterised. |

## 2. By eye

Sign in as one user and open another user's dashboard: the card shows. Follow it to the compare
page. Check both at 1280px and 360px, in English and in Spanish.
