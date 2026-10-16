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
