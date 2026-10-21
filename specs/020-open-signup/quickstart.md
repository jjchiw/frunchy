# Quickstart: Say sign-up is by invitation, and let the root open it

## Tests

```bash
dotnet test --filter-class "*SignUpModeTests*"
dotnet test                                     # the whole suite
```

## By hand

1. Invite mode (the default): open `/signin`. The note under the intro says sign-up needs an invite. Clicking a
   provider opens the dialog; Cancel or Esc closes it and focus returns to the provider link; "Continue with
   {Provider}" goes on to the provider. Tick "Don't show this again on this device" and continue: the next click goes
   straight to the provider.
2. As the root, `/account` > Site settings > Open > Save (a sign-in older than 10 minutes goes through
   `/signin/reconfirm` first). `/signin` then has no note and no dialog, and `/signup` has no invite field.
3. Back to Invite only: sign-up asks for a code again.

## Validation log

2026-10-20, implementation (cea2179): full suite 768 tests, 765 passed, 2 skipped, 1 failed (the known flaky
`ChartsTests` timing test, which passed on a quiet rerun); `SignUpModeTests` 24 passed; with the schema, auth,
operators, invites, account, claim, SQL-guardrail and design-system classes, 106 passed.

2026-10-20, the dialog in a browser (main session): Frunchy run locally on a fresh database with Bella's development
Umbral provider, driven by headless Chrome over the DevTools protocol with real mouse and key events. On `/signin`
in invite mode:
- the note shows, Alpine loads and the dialog starts closed;
- a click on "Sign in with Umbral" opens the dialog and stays on `/signin`, with only "Continue with Umbral" shown
  and focus inside the dialog (the checkbox);
- the backdrop is dimmed (`rgba(19, 17, 26, 0.8)`);
- Esc closes it and focus returns to the Umbral link;
- at 360px the dialog opens with the buttons stacked (Continue first) and no horizontal overflow.

Screenshots matched Night Broadcast (amber primary, surface card, fully rounded buttons). Not driven: the
continue-with-"don't show again" path in a real browser (covered by markup tests only) and the settings page.
