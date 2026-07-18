# Architecture

## Components and dependency direction

- **Forge.Core** owns the aggregate, approval gates, repository/evidence/plan contracts, workflow invariants, and application service. It has no project dependencies.
- **Forge.Infrastructure** implements SQLite, clarification adapters, safe repository discovery, deterministic evidence ranking, and Fake planning.
- **Forge.Api** composes modes and read-only planning services, exposes REST DTOs and safe capabilities, and maps exceptions to Problem Details.
- **forge-web** renders clarification, repository snapshots, evidence, plans, approvals, capabilities, and telemetry through REST.

```mermaid
flowchart LR
    User[Developer] --> Web[React + Vite]
    Web -->|REST DTOs| Api[ASP.NET Core API]
    Api --> Service[EngineeringTaskService]
    Service --> Domain[EngineeringTask]
    Service --> Port[IClarificationEngine]
    Service --> Repo[IEngineeringTaskRepository]
    Fake[FakeClarificationEngine] --> Port
    OpenAIEngine[OpenAIClarificationEngine] --> Port
    OpenAIEngine --> Gateway[IOpenAIResponsesGateway]
    Gateway --> SDK[Official OpenAI 2.12.0\nResponsesClient]
    RepoAdapter[SQLite adapter] --> Repo
    RepoAdapter --> DB[(forge.db)]
    Service --> Discovery[IRepositoryDiscoveryService]
    Service --> Evidence[IEvidenceSelectionService]
    Service --> Planner[IPlanningEngine]
    Discovery --> Git[read-only Git commands]
```

Dependencies point inward: API and Infrastructure depend on Core; Core knows neither. Official SDK types remain inside `SdkOpenAIResponsesGateway`. The `IOpenAIResponsesGateway` normalization boundary permits non-billable adapter tests.

## Clarification state and correction flow

The aggregate has no public general-purpose state transition. Application callers use explicit operations to apply an evaluation, answer the current question, request a summary revision, record a model call, or approve a summary.

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Clarifying: evaluation asks one question
    Draft --> AwaitingRequirementApproval: evaluation summarizes immediately
    Clarifying --> Clarifying: answer then evaluation asks one question
    Clarifying --> AwaitingRequirementApproval: evaluation summarizes
    AwaitingRequirementApproval --> Clarifying: correction note
    Clarifying --> AwaitingRequirementApproval: corrected summary
    AwaitingRequirementApproval --> ReadyForPlanning: explicit approval
    ReadyForPlanning --> Planning
    Planning --> AwaitingPlanApproval
    AwaitingPlanApproval --> Implementing
    Implementing --> Validating
    Validating --> Reviewing
    Reviewing --> Completed
```

Correction is permitted only while awaiting requirement approval. The correction record retains its timestamp and previous summary; previous clarification answers remain unchanged. The current summary is cleared before reevaluation and the revised summary requires another explicit approval.

## Read-only repository analysis and planning

After requirement approval, purpose-specific aggregate operations begin analysis, store a compact snapshot, store bounded evidence, store a validated plan, and approve that plan. Re-analysis is allowed before plan approval and replaces the prior snapshot/evidence/plan. Planning rejects missing evidence, stale snapshots, fingerprint mismatches, unsafe paths, unknown evidence IDs, and invalid create/modify/delete targets.

Discovery normalizes the root, contains every inspected path, skips reparse points, and uses `ProcessStartInfo.ArgumentList` for read-only Git commands. It never invokes repository scripts. Common dependency/build folders, generated/minified/binary files, and likely secret files are excluded. Configurable defaults cap discovery at 5,000 files, text at 256 KB per file and 20 MB total, and evidence at 12 files/60,000 characters. Limit warnings make partial inspection visible.

Evidence selection deterministically scores paths, lightweight C#/TypeScript symbols, content, roles, tests, and configuration relevance against approved requirement terms. Excerpts retain line numbers, are de-duplicated, hashed, and redact obvious sensitive key/value lines before persistence. Redaction is not a comprehensive secret scanner.

`FakePlanningEngine` creates a labelled plan using only snapshot metadata and selected evidence. Affected files cite evidence IDs; validation commands are proposals only. Plan approval moves the workflow to `Implementing`, where the UI truthfully stops because target modification is unavailable.

## OpenAI structured-output boundary

OpenAI mode uses `gpt-5.6-terra`, low reasoning effort, and a bounded 800-token output through the Responses API. `CreateResponseOptions.TextOptions` specifies `ResponseTextFormat.CreateJsonSchemaFormat(..., jsonSchemaIsStrict: true)`. The schema contains:

- `decision`: `ask` or `summarize`;
- nullable `question`, internal `questionFocus`, and `summary`;
- arrays for known facts, assumptions, and unresolved gaps.

After deserialization the adapter independently enforces:

- ask: one concise question for one atomic decision dimension, one snake-case focus, and no summary;
- summarize: one non-empty summary and null question/focus.

Questions with multiple marks, newlines, list syntax, excessive length, or a structurally combined focus are invalid provider responses. Semantic atomicity remains primarily prompt- and focus-driven. Forge never repairs output, makes a second model call, free-form parses, or silently invokes Fake mode.

The developer instruction prefix is stable. Each turn reconstructs one compact JSON context containing only the repository identifier, original requirement, previous question/answer pairs, and correction notes. Repository content is never implied. `previous_response_id` is intentionally unused.

```mermaid
sequenceDiagram
    actor Developer
    participant API
    participant Service
    participant Engine as IClarificationEngine
    participant Provider as Responses API / Fake
    participant DB as SQLite
    Developer->>API: create, answer, or correction
    API->>Service: validated command + cancellation token
    Service->>Engine: EvaluateAsync(canonical task context)
    Engine->>Provider: one bounded evaluation
    Provider-->>Engine: ask OR summarize
    Engine-->>Service: validated decision + optional call telemetry
    Service->>DB: persist task atomically
    Service-->>API: current task DTO
    API-->>Developer: one question or current summary
```

## Telemetry and estimated cost

Each real provider attempt records call ID, clarification stage, provider, model, reasoning effort, timestamps, success, response ID, input/cached/output/reasoning tokens, estimated cost, and a non-sensitive failure category. Fake mode produces no model-call record.

The estimate subtracts cached tokens from total input, prices uncached and cached input separately, then adds output pricing. Output already contains reasoning tokens, so reasoning usage is not double-counted. Rates are bound from `Forge:AI:Pricing`.

## Persistence compatibility

`EngineeringTasks` retains the first-slice columns and adds:

- `RequirementRevisionNotes TEXT NOT NULL DEFAULT '[]'`
- `ModelCalls TEXT NOT NULL DEFAULT '[]'`
- bounded snapshot, evidence, and implementation-plan JSON columns;
- evidence counters, repository analysis/fingerprint fields, and plan creation/approval timestamps.

Development startup uses `PRAGMA table_info(EngineeringTasks)` and adds only missing known columns. Existing databases do not need to be deleted.

## Failure handling and capabilities

Central exception handling maps missing tasks, invalid workflow operations, configuration faults, provider faults, and unexpected failures to safe Problem Details. Provider exception bodies and logs never include credentials or raw responses.

`GET /api/system/capabilities` reports repository inspection and Fake planning as available, while target modification, validation, review, and pull-request creation remain false. It exposes no key or secret-derived data.

## Current boundaries

Target-repository changes/tests, review, repair, pull-request creation, authentication, production migrations, and live planning providers are not part of this slice.
