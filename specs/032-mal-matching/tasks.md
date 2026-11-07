# Tasks: Better MyAnimeList matching

**Input**: [mal-matching.spec.md](mal-matching.spec.md)

**Tests**: required; the fake MyAnimeList client (`Infrastructure/FakeMal.fs`, route keys `search:<q>`, request log)
and `MalFixtures.fs`; no real provider.

- [x] T001 Tiers 3 and 4 (spaceless equality, then spaceless after a subtitle cut) in `tierWith`, season marker read
  before removing spaces; unit tests for US1 · Infrastructure/MalMatching.fs, tests/MalMatchingTests.fs
- [x] T002 Fallback queries: a pure `fallbackQueries title` (content words ≥ 4 letters, stop words out, longest
  first, at most 3; unit tests), used by the anchor search and `searchSeason` when the first search gives no title
  hit; candidates stored per US2.3 · Infrastructure/MalMatching.fs, tests
- [x] T003 Search reuse: 3 days for an empty result list, 30 otherwise (`MalClient`'s lookup); tests with the
  `TimeProvider` · Infrastructure/MalClient.fs, tests
- [x] T004 Sweep re-queue rule for new automatic no-matches (decided > 3 days ago; earliest air date ≤ 120 days, else
  row created ≤ 120 days); tests · Workers/Enrichment.fs, tests/EnrichmentTests.fs
- [x] T005 `mal-retry-unmatched` operator command, CLI registration, usage text, contract and README command list,
  tests · Operations/MalRetryUnmatched.fs, Program.fs, FrunchyBackend.fsproj,
  specs/003-mal-enrichment/contracts/operator-cli.md (or the file that lists `mal-retry`), README.md, tests
- [x] T006 An end-to-end test of the Arne case on the real app with FakeMal (series "The Casebook of Arne"; searches
  for the title and "Casebook" empty; "Arne" returns a TV entry titled "Arne no Jikenbo" with English "The Case Book
  of Arne"; it matches; `mal-rebuild` gives the same with no request)
- [x] T007 Docs: CLAUDE.md (the MyAnimeList Matching line: tiers 3-4, fallback queries, the reuse split, the re-queue
  rule, the new command), `changelog.d/032-mal-matching.md`; full suite; commit
  `feat(032): MyAnimeList matching ignores spacing, searches again by title words, and retries new shows`
- [x] T008 Review (main session)
