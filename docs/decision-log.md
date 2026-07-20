# Decision log

## 001 — Modular monolith

**Decision:** Use Core, Infrastructure, API, and one web application in a single repository.  
**Reason:** Clear dependency boundaries support rapid parallel feature work without distributed-system overhead.  
**Trade-off:** Modules deploy together and must rely on code discipline rather than network boundaries.

## 002 — .NET 8 and React with TypeScript

**Decision:** Build the REST backend with ASP.NET Core on .NET 8 and the UI with React, Vite, and TypeScript.  
**Reason:** The combination is productive, strongly typed, easy to demonstrate locally, and well supported by development tooling.  
**Trade-off:** The team maintains contracts in both C# and TypeScript until client generation is justified.

## 003 — SQLite for the demo

**Decision:** Persist task aggregates in a local SQLite database created automatically in Development.  
**Reason:** It provides real restart-safe persistence with almost no operational setup.  
**Trade-off:** The schema bootstrap is intentionally lightweight and is not a production migration strategy.

## 004 — One question at a time

**Decision:** The domain permits exactly one pending clarification question.  
**Reason:** Focused questions reduce cognitive load and make answer preservation and sufficiency explicit.  
**Trade-off:** Clarification can take more turns than a questionnaire, so prioritization quality matters.

## 005 — Deterministic tools before LLMs

**Decision:** Use normal parsers, repository tools, builds, and tests when model reasoning is unnecessary.  
**Reason:** Deterministic work is cheaper, repeatable, and easier to explain.  
**Trade-off:** The orchestrator must choose boundaries carefully and cannot treat one model as the universal tool.

## 006 — Fake clarification adapter before live API integration

**Decision:** Implement `IClarificationEngine` with a clearly named deterministic `FakeClarificationEngine`.  
**Reason:** The full approval loop can be demonstrated and tested without secrets or external availability.  
**Trade-off:** Questions are generic and no repository or requirement reasoning occurs until the adapter is replaced.

## 007 — Lightweight SQL repository

**Decision:** Use `Microsoft.Data.Sqlite` directly rather than introducing an ORM in the first slice.  
**Reason:** One aggregate and one table do not justify broader ORM and migration complexity.  
**Trade-off:** SQL and rehydration are maintained manually; revisit if the persisted model expands materially.

## 008 — Async clarification decision boundary

**Decision:** `IClarificationEngine` is cancellation-aware and returns one validated ask-or-summarize decision.
**Reason:** Requirements may already be complete, and provider work is asynchronous. Purpose-specific domain methods replace the general transition escape hatch.
**Trade-off:** Adapters cannot return partial or ambiguous results.

## 009 — Official Responses SDK behind a protocol boundary

**Decision:** Use official `OpenAI` 2.12.0 Responses types in `SdkOpenAIResponsesGateway`, with the clarification engine depending on a narrow normalized gateway.
**Reason:** Production code compiles against official SDK types while automated tests stay deterministic and non-billable. The Responses SDK surface is currently experimental, so isolation reduces change impact.
**Trade-off:** The mapping layer must be updated if that experimental surface changes.

## 010 — Strict structured clarification output

**Decision:** Request strict JSON Schema and validate the ask/summarize combination after deserialization. One question means one atomic decision dimension, represented internally by `questionFocus` and guarded by lightweight structural checks.
**Reason:** Malformed, both, neither, or structurally bundled decisions must never become workflow facts.
**Trade-off:** Semantic atomicity still depends on model instruction; readable but invalid output is rejected instead of guessed, repaired, or retried.

## 011 — No silent provider fallback

**Decision:** Fake and OpenAI modes are explicit; OpenAI failures never invoke Fake logic.
**Reason:** A fabricated fallback would conceal cost, availability, and trust failures.
**Trade-off:** OpenAI-mode demonstrations stop visibly when configuration or the provider fails.

## 012 — Persist telemetry with additive development migration

**Decision:** Store revision notes and compact model-call records as JSON columns, adding missing columns through SQLite schema inspection.
**Reason:** Existing local databases remain usable without a full migration framework for this competition slice.
**Trade-off:** JSON columns are less queryable than normalized tables and should be revisited if reporting expands.

## 013 — Read-only deterministic repository planning

**Decision:** Normalize and contain repository paths, use only read-only Git/file APIs, select bounded redacted evidence deterministically, and create an explicitly labelled Fake implementation plan with a separate approval gate.
**Reason:** The demo can ground planning in real repository facts without billable calls or allowing target mutation.
**Trade-off:** Lightweight symbol extraction, keyword scoring, and key-name redaction are explainable but cannot provide complete semantic understanding or comprehensive secret detection.

## 014 — Evidence-backed OpenAI planning behind the existing gateway

**Decision:** Make planning asynchronous and mode-specific, use one strict-schema Responses call for OpenAI planning, enrich provenance internally, and validate every returned path/evidence/step invariant independently.
**Reason:** Model reasoning can connect bounded cross-layer evidence while deterministic validation prevents invented paths and unsupported claims from becoming workflow facts.
**Trade-off:** Insufficient or malformed output fails visibly with no repair, retry, or Fake fallback.

## 2026-07-20: Prefer duplicate-billing safety and one strict Responses topology

**Decision:** Retry statusless transport failures only when failure before dispatch is proven; treat ambiguous failures as non-retryable. Require valid response identity and one shared strict topology across clarification, planning, and implementation. Partial usage is unavailable, duplicate JSON properties are rejected, and cost arithmetic cannot erase post-dispatch telemetry.

**Reason:** A connection failure can occur after the provider accepted a billable request. Conservative retry, strict response identity, and fail-soft telemetry preserve an honest audit record without risking silent duplicate charges.

**Trade-off:** Some transient ambiguous failures require a user-triggered new attempt instead of automatic recovery.

## 015 — Approval is not implementation

**Decision:** Explicit plan approval ends at `PlanApproved`; legacy `Implementing` rows from the plan-only slice migrate to that state.
**Reason:** Workflow state must describe completed work truthfully, and approval alone does not authorize or imply repository mutation.
**Trade-off:** The demo stops at a clear gate until a later implementation milestone is built.

## 016 — Treat response completeness as a planning invariant

**Decision:** Preserve the installed SDK's response status and incomplete reason, parse structured output only when completed, classify output-limit and content-filter incompleteness separately, and raise the planning allowance to 6,000 tokens while capping plan collections.
**Reason:** A response cut off at its configured allowance is operational truncation, not an ordinary malformed semantic plan. Explicit classification preserves accurate usage/cost and provides a recoverable user-triggered retry without exposing partial output.
**Trade-off:** The higher allowance can increase worst-case cost; compact prompts, reduced repository metadata, strict collection limits, and no automatic retry bound that risk.

## 017 — Implementation approval binds persisted review evidence

**Decision:** Store a bounded authoritative implementation-revision ledger and require task row version, active revision ID, approved-plan fingerprint, base commit, canonical result fingerprint, and verified unchanged-checkout completion evidence before transitioning to `ImplementationApproved`. Bind every approval command ID globally in SQLite to its complete semantic request, task result row, and immutable timestamp in the same transaction. Route approval through a persistence-only dependency graph, and project tokenized implementation branches through one safe display formatter.
**Reason:** A human decision must identify the exact review that was accepted and remain auditable without mutating or even observing the physical workspace during approval.
**Trade-off:** The active implementation fields are temporarily duplicated as a compatibility projection and must validate exactly against revision 1. Approval does not prove that the retained worktree is still available; a future validation or delivery command must reverify it.

## 018 — OpenAI proposes bounded operations before workspace reservation

**Decision:** Inspect approved sources read-only, send one bounded untrusted-data context to a tool-free strict-schema OpenAI implementation engine, validate the complete proposal deterministically, and only then reserve the isolated branch/worktree. Retry at most once for narrowly proven transient outcomes and record every physical request.
**Reason:** Provider reasoning can author real replacements without granting filesystem or command authority, while deterministic scope/content checks and delayed reservation preserve the active checkout and keep failed attempts artifact-free.
**Trade-off:** Complete replacements and conservative 32/64 KiB source/output budgets exclude large files; a provider failure is billable and must be explicitly retried rather than repaired or silently replaced by Fake output.

## 019 — Validate implementation compatibility through one disposable gated smoke

**Decision:** Record the successful explicitly authorized `gpt-5.6-sol` Responses API smoke as compatibility evidence for the strict OpenAI implementation boundary. The disposable test accepted exactly one approved modify operation, passed deterministic validation, and left its active checkout clean.
**Reason:** A real gated request demonstrates that the configured model, Responses transport, strict schema, and local validation interoperate without granting repository or command authority. OpenAI API billing is separate from ChatGPT and Codex.
**Trade-off:** This evidence covers only one small approved scenario and is not broad model-quality testing. It ran no target validation command and performed no staging, commit, push, pull-request, or delivery action.
