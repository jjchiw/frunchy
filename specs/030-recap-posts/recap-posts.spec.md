# Feature Specification: Weekly recap posts on the fediverse

**Feature Branch**: `030-recap-posts`

**Created**: 2026-11-05

**Status**: Draft

**Input**: the follow-up task "Opt-in fediverse post of the weekly recap": feature 029 shipped the "Last 7 days" and
"Streaks" cards on the dashboard and left "Later: an opt-in fediverse post of the recap." Modelled on feature 028
(watch updates): off by default, Public profiles only, at most one unlisted Note a week built from the recap figures
over a completed UTC week, through `fediverse_outbox`, the content frozen at the first attempt, removed when it is
turned off or the profile leaves Public.

## Clarifications

### Session 2026-11-05 (decided by the main session)

- Q: One setting with the daily watch updates (028), or two? → A: **Two independent settings.** They are different
  posts: the daily update lists series and episode numbers, the weekly recap gives totals. A follower-facing account
  may want a quiet weekly digest without daily posts, or the reverse. Each opt-in has its own table, its own
  `/account` section and its own removal: turning the recap off deletes only recap posts, and turning watch updates
  off deletes only watch updates. Leaving Public removes both (each module's own `queueRemoval`). Nothing about 028
  changes.
- Q: Which week? → A: A **completed UTC week, Monday 00:00 to the next Monday 00:00 UTC** (ISO weeks, Postgres
  `date_trunc('week', …)`), the UTC days of features 005, 027, 028 and 029. Not the dashboard card's rolling "last 7
  days": a weekly post names a fixed week. The week a post covers is always **the last completed week** as of the
  import transaction's `now()`: `week_start = date_trunc('week', now() AT TIME ZONE 'UTC') - 7 days`, `[week_start,
  week_start + 7 days)` on `watched_at`. There is no backfill: a week nobody synced in the following week is never
  posted.
- Q: When is it queued? → A: Like 028, **after an import completes, in the import's own transaction**, in the same
  savepoint as the watch update (a failure is logged and undone alone; **a recap never fails an import**). So a user
  who synced on Monday gets last week's recap that day; one who syncs on Thursday gets it on Thursday (still for last
  week). One row per user and week (`ON CONFLICT DO NOTHING`), queued only when the row was inserted, so later imports
  and `user-rebuild` never post again.
- Q: Who posts? → A: Checked when queued **and again before every send** (else the row becomes `skipped`):
  1. turned recap posts on (`recap_post_settings`, **off by default**), **before the week began**
     (`enabled_at <= week_start`): turning it on posts nothing about a week already under way, so nothing watched
     before opting in is ever posted (028's rule). The first recap is for the first full week after turning it on;
     the account page says so;
  2. effective visibility **Public** (`Profiles.effective`, the instance default when no choice);
  3. a username that passes `Fediverse.isValidHandle`;
  4. federation on (otherwise nothing is queried or queued, the route is absent and the section isn't shown).
- Q: What is in it? → A: The recap card's rules (feature 029, undeduplicated, the dashboard's figures) over the
  week's window, plus two weekly figures:
  - **episodes**, **time** (minutes, written with `Texts.en.RecapTime`), **series**, **new / continued**: exactly
    `Recap.Week` computed over `[week_start, week_end)` (a series is new when the user has no watch event of it
    before `week_start`);
  - **days watched**: the number of UTC days of the week that are streak days (029's rule: a fully watched episode
    of known length), 1 to 7;
  - **most watched**: up to 3 series with the most fully watched episodes of known length in the week (then title,
    then series id), each with its count, titles linking to the public series page `https://{domain}/anime/{id}`.
  No row (and no post) when the week has no fully watched episode of known length. A partial watch alone posts
  nothing.
- Q: The text? → A: English, like 028 (a post on the fediverse, not a page in a viewer's language), from `Texts.en`
  (Spanish values exist and are unused by the post). Example: "My week on Frunchy (Nov 2 – Nov 8, 2026): 14 episodes,
  5 h 20 min, 4 series (2 new, 2 continued). Watched on 5 of 7 days. Most watched: Frieren (6), The Apothecary
  Diaries (4) and Dandadan (2)." Two paragraphs (`<p>`): the figures, then the most watched. Every title is escaped,
  every link absolute. No content warning (`summary` none, `sensitive: false`): titles and numbers spoil nothing.
- Q: How is it sent? → A: The outbox, new kinds `post_recap` and `delete_recaps`, subject = the user id (so it runs
  in order with the user's other items). The worker reads the user's pending `recap_posts` rows (oldest first),
  re-checks the conditions, builds the HTML at the **first attempt** from `watch_events` in the row's window and
  **stores `content` and `url` on the row before calling Megáfono**; every retry sends the stored values (Megáfono
  answers 409 when a key returns with a different body). `POST /internal/actors/{userId}/objects` with
  `audience: "unlisted"`, no `inReplyTo`, no summary, `sensitive: false`, Idempotency-Key
  `recap:{userId}:{yyyy-MM-dd}` (the week's Monday), `url` = `https://{domain}/u/{username}#recap-{yyyy-MM-dd}`.
  `object_id`/`object_url` are stored whatever the row's state; the state becomes `published` only from `pending`.
  An empty week at build time (the history changed) → `skipped`. A permanent refusal of a row → that row `skipped`,
  as 028 does.
- Q: Turning it off, or leaving Public? → A: As 028: both stop new posts **and delete the earlier ones**, in the
  change's transaction (`RecapPosts.queueRemoval`): pending rows become `skipped`, and when any row is `pending` or
  `published` (read before), `delete_recaps` is queued; the worker deletes each stored object not yet `deleted`
  (2xx, 404, 410 → `deleted`; transient → retry; other 4xx → refused). Turning it back on posts only new weeks.
  A change of `Frunchy:Profiles:DefaultVisibility` deletes nothing (the re-check stops new posts).
- Q: Account deletion? → A: Unchanged: `delete_actor` removes the actor and its objects; rows cascade.

## Requirements *(mandatory)*

- **FR-001**: Schema. `recap_post_settings (user_id uuid PRIMARY KEY REFERENCES users ON DELETE CASCADE, enabled_at
  timestamptz NOT NULL DEFAULT now())`. `recap_posts (user_id uuid REFERENCES users ON DELETE CASCADE, week_start
  date, covered_from timestamptz, covered_until timestamptz, state text CHECK (pending | published | skipped |
  deleted) DEFAULT 'pending', object_id text NULL, object_url text NULL, content text NULL, url text NULL, created_at
  timestamptz DEFAULT now(), PRIMARY KEY (user_id, week_start))`. `fediverse_outbox.kind` accepts `post_recap` and
  `delete_recaps`: in the `CREATE TABLE` and as a guarded idempotent `upgrades` block after 028's (the kind check
  lacking `post_recap` is dropped by its real name and the ten-kind check added), proved by `SchemaUpgradeTests`
  (an eight-kind outbox is upgraded with its rows kept; a six-kind one goes through both blocks; a second run is a
  no-op). `SCHEMA.md` documents both tables and the kinds.
- **FR-002**: `POST /account/recap-posts` (federation on only, in `Program.federationEndpoints`; absent otherwise):
  `requireSignedIn`, `readValidatedForm`, `enabled` exactly `on` or `off` (else
  `?msg=recap-posts-invalid#recap-posts`, nothing changes), `account-write` limiter. One transaction: on = insert if
  missing (keeps the first `enabled_at`); off = delete and `RecapPosts.queueRemoval`. Security event
  `recap_posts_enabled` / `recap_posts_disabled` only on a real change. Redirect
  `?msg=recap-posts-on|recap-posts-off#recap-posts`.
- **FR-003**: `/account` shows `section[data-account="recap-posts"]` (the `accountSection` helper, id `recap-posts`,
  right after the watch-updates section) only when federation is on and the username can federate: what it does
  (a weekly post of the recap, Monday to Sunday UTC, the first one for the first full week after turning it on), the
  state `data-recap-posts-state="on|off"`, one form to turn it on or off, the deletion note, and
  `data-recap-posts-not-public` when the effective visibility isn't Public. The three messages render as the page's
  other notices.
- **FR-004**: `Infrastructure/RecapPosts.fs` (after `Recap.fs`, `Profiles.fs`, `Fediverse.fs`, `WatchUpdates.fs`):
  `isOn`, `setEnabled`, `queueRemoval` (as 028), and `queueAfterImport connection tx (policy: WatchUpdates.Policy)
  userId`: the username check in F#, then one statement that checks opt-in (`enabled_at <= week_start`), Public, and
  inserts the week's row `ON CONFLICT DO NOTHING RETURNING` only when a fully watched episode of known length is in
  the window; when inserted, `Fediverse.enqueue … "post_recap"`. Called by `ImportProcessor` inside the existing
  savepoint, after `WatchUpdates.queueAfterImport`.
- **FR-005**: `Infrastructure/Recap.fs` gains the window form: `loadBetween connection userId (from) (until: option)`
  (the existing statement with `w.watched_at < @until` when given; `loadWeek` keeps its exact behaviour by calling it
  with `None`), plus the week's days watched and most watched series (literal SQL, parameters only). The dashboard
  is unchanged and its tests stay green.
- **FR-006**: The worker (`Workers/FediverseOutbox.fs`) handles `post_recap` and `delete_recaps` as in the
  clarifications, with a pure public `recapPostHtml (domain: string) (weekStart: DateOnly) (post: RecapPost) : string`
  unit-tested without Postgres (figures, singular/plural, "1 new"/"0 continued", time formatting, 0 to 3 most
  watched joined with commas and "and", titles with `<script>` and `&` escaped, links absolute under
  `https://{domain}`, the week's dates).
- **FR-007**: `/account/visibility`: when the effective level leaves Public (federated), also
  `RecapPosts.queueRemoval`.
- **FR-008**: All copy in `Views/Texts.fs` (en and es), including the two event labels. No new design token or icon
  (reuse `Theme.CalendarMonth`), no change to `/api/*`, the import contract, the dashboard or the extension.
- **FR-009**: SQL literal with parameters (the guardrail test). Docs: `docs/fediverse.md` ("Weekly recap posts"
  section and the outbox kinds), `SCHEMA.md`, CLAUDE.md (a line after "Watch updates"),
  `changelog.d/030-recap-posts.md`.

## Success Criteria *(mandatory)*

- **SC-001**: A public user who turned recap posts on before last week and watched last week gets, at their first
  sync this week, exactly one unlisted Note with last week's figures and links; a second sync posts nothing.
- **SC-002**: Opting in during last week, not opting in, Members or Private, a week with nothing fully watched, or
  federation off produces no post. Watch updates (028) and recap posts are independent.
- **SC-003**: Turning recap posts off or leaving Public deletes the published recaps on Megáfono and stops new ones,
  even when a post was already queued; turning watch updates off deletes no recap.
