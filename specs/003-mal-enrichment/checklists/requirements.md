# Specification Quality Checklist: MyAnimeList Enrichment

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-10-02
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- Iteration 1:
  - **Named services**: Jikan and MyAnimeList are the data source the feature is about, so naming
    them is a product reference, not an implementation detail. Their published limits (3 per second,
    60 per minute) are external facts the requirements must respect. How pacing, retries and the
    queue are built is left to the plan.
  - **Principle I** (derived data must be rebuildable): FR-021 covers it. Matches and stored fields
    are re-derived from stored responses without new requests, and manual decisions are kept as
    operator input.
  - **Two open markers**: the matching level (series or season, in Edge Cases) and how much the
    dashboard shows (User Story 2). Defaults were chosen for everything else and recorded under
    Assumptions: the auto-match rule, excluding adult entries, refresh ages of 30 and 7 days, and
    linking back to MyAnimeList.
- Iteration 2 (2026-10-02): the user's answers were applied and recorded under Clarifications.
  - **Q1 B**: matching is per Crunchyroll season, movies included. The first season is found by
    search, later seasons and movies through MyAnimeList's sequel and related links, and dubs share
    their original's match. This changed FR-001 to FR-004 (FR-003a and FR-003b added), Edge Cases,
    the Key Entities and SC-001.
  - **Q2 B**: score and up to 3 genres on each recently-watched card, plus a top-5 "Top genres"
    section with its coverage share. This changed User Story 2, FR-018 (FR-018a added) and SC-007.
  - **Real catalog**: checked on the real `frunchydb` (read-only queries). Dubs are separate
    seasons (43 of 501), movies appear as seasons (season 0 or listed among the others), and season
    numbers are irregular (JUJUTSU KAISEN Season 3 is numbered 5), hence matching by title and links
    rather than by number.
