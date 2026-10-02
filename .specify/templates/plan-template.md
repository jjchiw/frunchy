# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: [e.g., Python 3.11, Swift 5.9, Rust 1.75 or NEEDS CLARIFICATION]

**Primary Dependencies**: [e.g., FastAPI, UIKit, LLVM or NEEDS CLARIFICATION]

**Storage**: [if applicable, e.g., PostgreSQL, CoreData, files or N/A]

**Testing**: [e.g., pytest, XCTest, cargo test or NEEDS CLARIFICATION]

**Target Platform**: [e.g., Linux server, iOS 15+, WASM or NEEDS CLARIFICATION]

**Project Type**: [e.g., library/cli/web-service/mobile-app/compiler/desktop-app or NEEDS CLARIFICATION]

**Performance Goals**: [domain-specific, e.g., 1000 req/s, 10k lines/sec, 60 fps or NEEDS CLARIFICATION]

**Constraints**: [domain-specific, e.g., <200ms p95, <100MB memory, offline-capable or NEEDS CLARIFICATION]

**Scale/Scope**: [domain-specific, e.g., 10k users, 1M LOC, 50 screens or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Gates from `.specify/memory/constitution.md` (v1.1.0). Mark each ✅ / ❌ (❌ needs a row in
Complexity Tracking):

- [ ] **I. Core asset**: raw imports untouched (except full user deletion); every new derived
      table states how it is rebuilt from raw imports
- [ ] **II. Boring tech**: no new broker/queue framework/ORM/event store/service; Postgres used
      as the job queue; design sized to the ~0.6 events/s reference
- [ ] **III. Vertical slices**: each endpoint = one `Handlers/` file (view/contract → SQL →
      handler → endpoint with verb); `Program.fs` only wires; workers in `Workers/`
- [ ] **IV. Idempotent async ingestion**: endpoints persist + 202; heavy/external work in
      workers; dedup key defined; external APIs per distinct anime and rate-limited;
      per-item failure status + error
- [ ] **V. Tests**: integration tests per slice/worker against real Postgres with
      `JsonSamples/` payloads; unit tests for parsing/normalization
- [ ] **Constraints**: localized UI text (no hardcoded strings; en default + es), CDN-only frontend, schema in `Infrastructure/Database.fs`
      + `SCHEMA.md` updated, APIs exist in pinned package versions

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
# Frunchy layout (DEFAULT for this repo): show only the files this feature touches
Handlers/<Feature>/<Slice>.fs     # one file per endpoint (vertical slice)
Workers/<Name>.fs                 # BackgroundService workers
Infrastructure/Database.fs        # schema bootstrap (initializeSchema)
Views/Layout.fs                   # shared UI only
Domains/*.fs                      # shared domain types
tests/FrunchyBackend.Tests/       # integration (real Postgres) + unit tests
SCHEMA.md                         # kept in sync with schema changes

# [REMOVE IF UNUSED] Option 1: Single project
src/
├── models/
├── services/
├── cli/
└── lib/

tests/
├── contract/
├── integration/
└── unit/

# [REMOVE IF UNUSED] Option 2: Web application (when "frontend" + "backend" detected)
backend/
├── src/
│   ├── models/
│   ├── services/
│   └── api/
└── tests/

frontend/
├── src/
│   ├── components/
│   ├── pages/
│   └── services/
└── tests/

# [REMOVE IF UNUSED] Option 3: Mobile + API (when "iOS/Android" detected)
api/
└── [same as backend above]

ios/ or android/
└── [platform-specific structure: feature modules, UI flows, platform tests]
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
