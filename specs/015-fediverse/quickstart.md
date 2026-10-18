# Quickstart: validate feature 015

Contract: [frunchy-megafono](contracts/frunchy-megafono.md). Decisions: [research](research.md).

## 1. Automated

```bash
dotnet build && dotnet test
```

Every suite in tasks.md runs against `FakeMegafono` (no real Megáfono, no Aspire).

## 2. Under the AppHost (SC-006)

```bash
dotnet run --project AppHost/Frunchy.AppHost.fsproj
```

1. Sign up a user (Google, configured in Bella or user secrets), import a history and open a series.
2. Through the gateway with `Host: frunchy.test`: `/.well-known/webfinger?resource=acct:{username}@frunchy.test` and `acct:{series-handle}@frunchy.test` answer 200 from Megáfono.
3. Comment on an episode; within seconds Megáfono's database has the thread (series actor) and the reply (user actor).

## Validation log

**2026-10-18, final run (main session).**

- **Automated:** `dotnet test`: 664 total, 662 passed, 0 failed, 2 skipped (the extension tokens parity check and
  the opt-in MyAnimeList recording). The suite runs against `FakeMegafono`, with no Aspire and no real Megáfono.
- **Review fix:** reading the block lists for `/account` and `/review/fediverse` now gives up after 3 s, so a stuck
  Megáfono can't hold the page for the client's 10 s. A test with a 20 s hang checks it.
- **SC-006, by hand, under the AppHost against the real Megáfono** (built from the local checkout).
  - **Setup:**
    - user `fedi` created with `user-create` against the AppHost's database;
    - the 2026 sample imported with its sync key through the gateway: 202, completed, 400 watch events.
  - **Accounts:**
    - `/.well-known/webfinger?resource=acct:fedi@frunchy.test` answers **200** through the gateway, with
      `self` = `https://frunchy.test/ap/actors/{users.id}` and the dashboard as profile page.
      Megáfono serves the actor document.
    - Opening the Eyeshield 21 series page created its handle `eyeshield-21`.
      `acct:eyeshield-21@frunchy.test` answers **200**, with `self` = `…/ap/actors/series-GRE5KGN36` and the series
      page as profile. Megáfono serves its actor document.
    - Both pages show their `data-fediverse-handle` line.
  - **A comment**, written as the comment form writes it (comment, thread row, `ensure_thread` and
    `publish_comment` in one transaction; inserted in SQL, since no local sign-in provider exists):
    - Frunchy's running outbox worker sent both on the first attempt (0 retries).
    - **Thread:** the series Group published "Eyeshield 21: Episode 29 Ultimate Combo! The Devil Gunmen!", public,
      cc its followers, `url` = the episode page.
    - **Reply:**
      - `attributedTo` the user's actor and `inReplyTo` the thread;
      - `summary: "Spoilers: E1"` and `sensitive: true`;
      - `url` = `…/episode/GR9PM7P36#comment-manualcheck015a`;
      - public, cc the user's followers.
    - **Stored:** Frunchy stored both object ids, and the episode page carries the thread's alternate link.
    - **Not sent twice:** Megáfono holds exactly 2 objects.
  - **Not tried here:** a reply from a remote server (`reply.received` from real federation). That needs
    Megáfono's dev-fediverse kit (private CA, GoToSocial); the webhook path is covered by the signed-webhook tests.
