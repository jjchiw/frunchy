# Quickstart: validate feature 010

Contract: [design system](contracts/design-system.md). Decisions: [research](research.md).

## 1. Automated validation

```bash
dotnet build   # 0 warnings
dotnet test    # needs Docker
```

| Suite | Proves |
|---|---|
| `DesignSystemTests` | **Contrast (SC-003):** every pair on the new values. **Every page (SC-001):** every route in the list renders on the new tokens with its hooks, and no Neo-Scrobble value or old font name appears. **Components:** no gradient in the component CSS. **Icons:** the icon subset is unchanged. |
| Everything else | **SC-002:** unchanged and green. A restyle that drops a `data-*` hook fails the page's own tests. |

**Parity with the extension (SC-005):**
- Skipped in worktrees, because `../browser-extension` isn't beside them.
- In the main checkout it fails until the extension applies `contracts/extension-handoff.md`, then
  passes.

## 2. By eye

```bash
bella sdk run -- dotnet run --launch-profile http
```

Open each page at 1280px and 360px, in English and in Spanish (`Accept-Language: es`):
- `/`, `/signin`, `/u/<you>`, `/u/<you>/dna`, `/u/<you>/year`;
- an anime page and an episode page with comments;
- `/account`, `/account/key`, `/account/delete`;
- as root: `/review/mal`, `/review/operators`;
- a 404.

Compare them with row 2 of the pitch canvas.

## 3. Impeccable audit (SC-004)

Audit every page above. Fix every blocking finding, and list the rest with their severity in the
validation log below.

## Validation log

**2026-10-15**, branch `010-night-broadcast`:
- `dotnet build`: 0 warnings.
- `dotnet test`: 380 total, 378 passed, 2 skipped. The skips are the extension parity check (no
  `../browser-extension` beside the worktree) and the opt-in MyAnimeList recording.

| Criterion | Evidence |
|---|---|
| SC-001 | The every-page check fails on any Neo-Scrobble value or old font, and passes on all pages. |
| SC-002 | Only `DesignSystemTests` changed (its expected values and the leftover checks). Every other test is unchanged and green. |
| SC-003 | The contrast test passes on the new palette (precomputed in research R1). |
| SC-004 | `impeccable` audits per page group (T005–T009). The medium findings were fixed: the notice icon, the home step colours and the loud card titles. Signed-in pages were audited from the HTML only, because there's no local sign-in provider. Screenshots are in `.pitches/audit010/` (git-ignored). |
| SC-005 | **Pending.** The handoff was sent to the extension session (`frunchy_extension`) on 2026-10-15; it hasn't applied it yet. Until it does, the parity check fails in the owner's main checkout. |

**Review fixes:** rose is kept for warnings and spoilers. The dub and audio pills and the DNA
studio pill move to lilac (tags), and "In progress" to amber (your own progress).

**Open:**
- **Comment author names**: rose for "others" was considered and not done.
- **Account deletion box**: `DeleteAccount.fs` has `border-error/30` without a `border`; left as is.
