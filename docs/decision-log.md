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
