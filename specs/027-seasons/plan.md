# Implementation Plan: Seasons

**Branch**: `027-seasons` | **Spec**: [seasons.spec.md](seasons.spec.md) | **Contract**: [contracts/hooks.md](contracts/hooks.md)
| **Design**: [design-brief.md](design-brief.md)

## Summary

Store each catalog episode's air date (parser, upsert, a guarded backfill from the stored pages), then change the
DNA and year statements: taste figures weighted by watched seasons, the flagship without long runners, a long-run
card and a "Your seasons" section on the DNA. First UI pass with existing components; the designer reviews
screenshots before the UI is final.

## Technical Context

F#, Oxpecker ViewEngine, raw Npgsql with literal SQL (SqlGuardrailTests), Postgres 18, xUnit v3 + Testcontainers.
No new dependency. The DNA stays one statement for its figures (add CTEs/rows to the existing union, or at most one
more statement for the seasons section); no per-row queries.

## Constitution Check

- Watch history is the core asset: raw pages are only read by the backfill; the catalog gains a derived column.
- Order-independence and idempotency: the air date follows the catalog's `source_at` rule; `user-rebuild` equality
  is tested.
- Schema change to an existing table: a guarded block in `upgrades`, proved by `SchemaUpgradeTests`.
- Tests: `data-*` hooks only.

## Design

1. `CrunchyrollPage.EpisodeFacts.AiredAt: DateTimeOffset option` from `episode_air_date` (the parser's existing date
   helper); `ImportProcessor.upsertEpisode` writes `aired_at`.
2. `Database.fs`: `aired_at` in `CREATE TABLE cr_episodes`, and the guarded upgrade with the backfill (contract).
3. A small pure module (`Infrastructure/Seasons.fs`, after Charts or with the season helper moved there): airing
   season of an instant, its key (`fall-2026`), ordering, the long-runner and long-run constants. `Charts.seasonOf`
   reuses it so both name seasons the same way.
4. `Handlers/Users/Dna.fs`: in the statement, a dedup-group air date (min), watched-season rows, studio/genre/
   decade/format counted by watched seasons, `long_runner` per series, the flagship among non-long-runners, the
   long-run row, and the seasons rows (season, series, title, poster, episodes, while-airing). F# folds the
   seasons rows into the section (top season, newest 8, earlier count). Views per the contract hooks, existing
   components, texts in en and es.
5. `Handlers/Users/Year.fs`: the same counting rule for its studio/genre/decade/format figures.
6. Tests: parser, upsert/rebuild, upgrade, DNA figures (the user story scenarios), year figures, hooks, both
   languages, empty states. `Fixtures.fs` gains a way to set `episode_air_date` and episode numbers on synthetic
   pages. Update existing DNA/year tests whose expected shares change, explaining each change in the commit.
7. Docs: `SCHEMA.md`, `CLAUDE.md` (DNA route line), CHANGELOG Unreleased (Changed: taste figures count seasons,
   flagship/long run; Added: Your seasons; Upgrade: the automatic backfill), page snapshots for the designer.

## Delegation

One Sonnet agent: commit 1 data (1-3 and their tests), commit 2 figures and UI (4-6), commit 3 docs and the
snapshot HTML. The main session reviews each against the contract, runs the suite, then sends screenshots and the
brief to the designer; design fixes go back to an agent.
