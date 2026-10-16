# Message for the browser-extension session

Send this to the extension's Claude session (or start it with this text). It doesn't edit
FrunchyBackend.

---

Frunchy backend: the shared design system moves to "Night Broadcast" (backend feature 010, branch
`010-night-broadcast`). Please plan the matching change in the extension through your spec-kit flow.

**What changes**
- **Token values change; every name stays.** The full list is in
  `FrunchyBackend/specs/010-night-broadcast/contracts/design-system.md`.
- **One new token:** `--font-display`, used for headings and the wordmark.
- **Fonts:**
  - Figtree for text (`--font-family`);
  - Bricolage Grotesque for headings (`--font-display`);
  - IBM Plex Mono for figures, usernames, keys and URLs (`--font-mono`).

  Self-host them as you do today (for example `@fontsource-variable/figtree`,
  `@fontsource-variable/bricolage-grotesque`, `@fontsource/ibm-plex-mono`).
- **Colour roles:**
  - amber `#ffb547` is the user and the primary action;
  - rose `#ff6fa8` is other people and warnings;
  - lilac `#c9bcff` is tags;
  - coral `#ff9b85` is errors.
- **Rules:** no gradients, buttons fully rounded, cards `--radius-lg` (now `1.25rem`), and no
  eyebrow labels.
- **The extension icon and popup mark** take the new colours (backend
  `design/brand/frunchy-mark.svg`, updated in 010).

**The check**
- The backend's `DesignSystemTests` compares your `tokens.css` with the website. Every token you
  define, except `--font-family`, must match the contract's value.
- Until you land this, that test fails where both repositories sit side by side. That's expected.

**The visual reference** is row 2 of the pitch canvas,
https://claude.ai/artifact/Jzm6navcFrNnQ8TSNwcESK. It's private to the owner, so ask them for
screenshots if you can't open it.

Please send back:
- whether the contract works for the popup at 380×600 (contrast and sizes);
- anything you'd need added to it.
