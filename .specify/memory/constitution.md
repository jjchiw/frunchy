<!--
Sync Impact Report
==================
Version change: (unratified template) → 1.0.0
Bump rationale: first ratification; all placeholders replaced with concrete principles.

Principles (template slot → adopted title):
  [PRINCIPLE_1_NAME] → I. Watch History Is the Core Asset
  [PRINCIPLE_2_NAME] → II. Boring Technology Until Usage Demands Otherwise
  [PRINCIPLE_3_NAME] → III. Vertical Slices
  [PRINCIPLE_4_NAME] → IV. Idempotent, Asynchronous Ingestion
  [PRINCIPLE_5_NAME] → V. Integration-Tested Slices

Added sections:
  - Additional Constraints (was [SECTION_2_NAME])
  - Development Workflow (was [SECTION_3_NAME])
  - Governance (filled)

Removed sections: none

Templates:
  ✅ .specify/templates/plan-template.md: Constitution Check gates (I–V + constraints)
     and Frunchy source layout added
  ✅ .specify/templates/tasks-template.md: tests are now REQUIRED (Principle V);
     Frunchy path conventions added
  ✅ .specify/templates/spec-template.md: reviewed, no change needed (mandatory sections
     unaffected; out-of-scope rule is enforced via the Development Workflow section)
  ✅ .specify/templates/checklist-template.md: reviewed, no change needed
  ⚠ .claude/skills/speckit-tasks/SKILL.md line ~145 still says "Tests are OPTIONAL ...
     unless explicitly requested". The skill is managed by spec-kit and left untouched;
     Principle V counts as that explicit request, and the tasks template says so.
  ✅ CLAUDE.md, README.md: already consistent (vertical slices, boring stack, raw imports)

Deferred TODOs: none
-->

# Frunchy Constitution

Frunchy is a "Last.fm for anime": automatic Crunchyroll watch history → statistics →
"anime DNA" → (later) community. This constitution defines the non-negotiable engineering
rules for building it.

## Core Principles

### I. Watch History Is the Core Asset

- Raw imports (`cr_imports`) MUST be stored exactly as received and MUST NEVER be updated
  or deleted by application code.
- Every derived table (normalized watch events, catalog entries, external-catalog matches,
  analytics projections) MUST be rebuildable by reprocessing the raw imports. A feature
  that introduces derived data MUST state how that data is rebuilt.
- Normalized watch events are an append-mostly ledger. Corrections are allowed only when
  they are idempotent and can be reproduced from raw data.

**Rationale**: the accumulated watch history is the asset competitors can't reconstruct.
Keeping it raw lets us reprocess with better logic later without losing anything.

### II. Boring Technology Until Usage Demands Otherwise

- The stack is F#, Oxpecker, Npgsql with hand-written SQL, PostgreSQL, and .NET
  `BackgroundService` workers.
- PostgreSQL is the job queue: status columns or work tables, polled by a worker.
- Message brokers (RabbitMQ, Kafka, NATS), Wolverine, Hangfire, event stores, CQRS
  frameworks, ORMs and microservices MUST NOT be introduced without a written
  justification in the plan's Complexity Tracking that shows **measured** pain.
- Scale reference for design decisions: 10k users × 5 episodes/day ≈ 0.6 events/s.
  Designs MUST NOT optimize for load orders of magnitude beyond this.

**Rationale**: the open question is which statistics users want, not throughput.
Infrastructure added early slows down discovering the product.

### III. Vertical Slices

- Each endpoint MUST live in its own file under `Handlers/`, grouped in feature folders
  when a feature has several. Inside the file, top to bottom: view (or JSON
  request/response contract) → slice-private SQL → handler → `endpoint` with an explicit
  HTTP verb.
- `Program.fs` MUST remain the composition root only: DI, middleware, error handling and
  the list of slice endpoints.
- Shared UI goes in `Views/` only once two or more slices use it. Background workers live
  in `Workers/`. Slices MUST NOT call each other's private data-access code.

**Rationale**: a feature changes in one place, and the F# compile order stays simple.

### IV. Idempotent, Asynchronous Ingestion

- Import endpoints MUST persist the request and return `202 Accepted` without doing
  normalization or external calls inline.
- Normalization and external API calls (e.g. Jikan) MUST run in background workers.
- Re-sending the same data MUST NOT create duplicate watch events. Every ingestion path
  MUST define its deduplication key.
- External catalog APIs MUST be called per distinct anime, never per watch event, and
  MUST respect the provider's published rate limits.
- Processing failures MUST be recorded per item with a status and an error message, and
  MUST NOT be silently dropped. Items that can't be processed stay visible and
  retryable.

**Rationale**: the extension sends batches (a heavy first sync, then small deltas) and
will resend data. Matching scales with distinct anime, not with events.

### V. Integration-Tested Slices

- Every endpoint slice and every worker MUST have integration tests that run against a
  real PostgreSQL instance (e.g. Testcontainers) and use real Crunchyroll payloads from
  `JsonSamples/`.
- Pure parsing and normalization functions MUST have unit tests.
- `dotnet test` MUST pass before a feature is marked complete.
- Tests are a required part of every feature's task list, not optional extras.

**Rationale**: the risky logic sits in SQL and in payload parsing. Mocks would hide
exactly those failures.

## Additional Constraints

- **UI**: server-rendered with Oxpecker.ViewEngine, htmx 4, Alpine.js and
  Tailwind/DaisyUI loaded from CDNs. There is no frontend build step. User-facing text
  MUST be in Spanish.
- **Schema**: changes go in the idempotent bootstrap script (`initializeSchema` in
  `Infrastructure/Database.fs`); there is no migration framework yet. `SCHEMA.md` MUST be
  updated in the same change.
- **Platform**: .NET 10 (`net10.0`).
- **Package APIs**: code MUST only use APIs present in the pinned package versions. The
  `oxpecker` skill (`.claude/skills/oxpecker/SKILL.md`) lists the known gaps. Upgrading a
  package is a deliberate, stated decision.

## Development Workflow

- Features go through spec-kit (specify → clarify → plan → tasks → implement) on a
  numbered feature branch (`###-feature-name`).
- Specs describe what users get and why; plans describe how. Each plan MUST include a
  Constitution Check, which is re-evaluated after design.
- Design mockups in `design/` are direction, not requirements. Elements that can't be
  derived from real data (personal ratings, XP/collection ranks, community trends,
  followers, third-party list sync) are out of scope until a spec explicitly adds their
  data source.

## Governance

- This constitution supersedes other project documents when they conflict. `CLAUDE.md`
  and `README.md` hold runtime guidance and MUST stay consistent with it.
- Amendments are made through a commit or PR that updates this file, its version and its
  Sync Impact Report, and any dependent templates under `.specify/templates/`.
- Versioning follows semver: MAJOR for removing or redefining a principle in a way that
  breaks compatibility, MINOR for a new principle or section or materially expanded
  guidance, PATCH for clarifications and wording.
- Compliance: every plan's Constitution Check MUST pass, or the violation MUST be
  justified in Complexity Tracking. Reviews verify that tests (Principle V) and
  rebuildability (Principle I) are addressed.

**Version**: 1.0.0 | **Ratified**: 2026-10-02 | **Last Amended**: 2026-10-02
