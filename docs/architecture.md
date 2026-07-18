# Architecture

## Components and responsibilities

- **Forge.Core** contains the `EngineeringTask` aggregate, workflow state machine, clarification contracts, repository port, and application service. It has no project dependencies.
- **Forge.Infrastructure** contains the development-only `FakeClarificationEngine`, SQLite repository, and development database bootstrap.
- **Forge.Api** composes the application and exposes REST DTOs for creating, reading, answering, and approving tasks. Swagger is enabled in Development.
- **forge-web** is a React client that renders the current state and communicates only through REST.

Dependency direction is `Web → HTTP API → Core ports ← Infrastructure adapters`. Core never references Infrastructure or API.

```mermaid
flowchart LR
    User[Developer] --> Web[React + Vite]
    Web -->|REST DTOs| Api[ASP.NET Core API]
    Api --> Service[EngineeringTaskService]
    Service --> Domain[EngineeringTask aggregate]
    Service --> Engine[IClarificationEngine]
    Service --> Repo[IEngineeringTaskRepository]
    Fake[FakeClarificationEngine\nDevelopment only] --> Engine
    Sqlite[SQLite adapter] --> Repo
    Sqlite --> Db[(forge.db)]
```

## Workflow state machine

Invalid state changes throw `WorkflowException` in domain code. The current slice ends at `ReadyForPlanning`; later states exist so their order is explicit, not to imply their functionality is present.

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Clarifying
    Clarifying --> RequirementSummaryReady
    RequirementSummaryReady --> AwaitingRequirementApproval
    AwaitingRequirementApproval --> ReadyForPlanning: explicit approval
    ReadyForPlanning --> Planning
    Planning --> AwaitingPlanApproval
    AwaitingPlanApproval --> Implementing: explicit approval
    Implementing --> Validating
    Validating --> Reviewing
    Reviewing --> Completed
    Draft --> Failed
    Clarifying --> Failed
    RequirementSummaryReady --> Failed
    AwaitingRequirementApproval --> Failed
    ReadyForPlanning --> Failed
    Planning --> Failed
    AwaitingPlanApproval --> Failed
    Implementing --> Failed
    Validating --> Failed
    Reviewing --> Failed
```

## Current clarification sequence

```mermaid
sequenceDiagram
    actor Developer
    participant Web
    participant API
    participant Service
    participant Fake as FakeClarificationEngine
    participant DB as SQLite
    Developer->>Web: Repository + requirement
    Web->>API: POST /api/tasks
    API->>Service: Create task
    Service->>Fake: Evaluate task
    Fake-->>Service: One question
    Service->>DB: Persist aggregate
    API-->>Web: Task DTO
    loop One question per turn
        Developer->>Web: Answer current question
        Web->>API: POST /api/tasks/{id}/answers
        API->>Service: Save answer and evaluate
        Service->>Fake: Evaluate preserved context
        Fake-->>Service: Next question or summary
        Service->>DB: Persist aggregate
        API-->>Web: Updated task DTO
    end
    Developer->>Web: Approve summary
    Web->>API: POST /api/tasks/{id}/requirement-approval
    Service->>DB: Persist ReadyForPlanning
    API-->>Web: Approved task DTO
```

## Integration boundaries

`IClarificationEngine` is the replacement point for a future OpenAI adapter. That adapter will return one prioritized question or a grounded summary, plus recorded model-call metadata; it must not bypass domain gates.

Repository discovery, relevant-file retrieval, deterministic search, builds, tests, and diff inspection will be introduced behind focused tool interfaces. Planning must cite retrieved files and evidence. Target-repository mutation remains prohibited until both requirement and plan approval are stored.
