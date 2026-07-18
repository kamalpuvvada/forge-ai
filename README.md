# Forge AI

Forge AI is a trustworthy, explainable, and cost-aware software-engineering workflow that turns a requirement into a reviewed pull request. This repository currently contains the first runnable vertical slice: local task creation, deterministic one-question-at-a-time clarification, a requirement summary, explicit approval, and local SQLite persistence.

Planning, target-repository analysis, code changes, validation, review, pull-request creation, and live model integrations are **not implemented yet**.

## Prerequisites

- Windows PowerShell 7 or Windows PowerShell 5.1
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or a newer SDK that can target .NET 8
- [Node.js](https://nodejs.org/) 20 or newer with npm
- Git

Verify the tools from the repository root:

```powershell
dotnet --version
node --version
npm --version
git --version
```

## Restore dependencies

From `C:\Projects\ForgeAI` (or your clone directory):

```powershell
dotnet restore .\ForgeAI.slnx
Push-Location .\web\forge-web
npm ci
Pop-Location
```

## Run locally

Open the first PowerShell terminal at the repository root and start the API:

```powershell
dotnet run --project .\src\Forge.Api --launch-profile http
```

The API listens at `http://localhost:5180`; Swagger is at `http://localhost:5180/swagger`.

Open a second PowerShell terminal at the repository root and start the web client:

```powershell
Push-Location .\web\forge-web
npm run dev
Pop-Location
```

Open `http://localhost:5173`. Vite proxies `/api` requests to the local API.

## Test and build

Run the backend tests:

```powershell
dotnet restore .\ForgeAI.slnx
dotnet test .\ForgeAI.slnx --configuration Release
```

Build both applications:

```powershell
dotnet restore .\ForgeAI.slnx
dotnet build .\ForgeAI.slnx --configuration Release --no-restore
Push-Location .\web\forge-web
npm ci
npm run build
Pop-Location
```

## Reset the local SQLite database

Stop the backend first. Then run these exact, narrowly scoped commands from the repository root:

```powershell
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-shm -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-wal -Force -ErrorAction SilentlyContinue
```

The Development API safely recreates the database and table on its next start. This bootstrap intentionally avoids production migration complexity.

## Implemented now

- A modular-monolith solution targeting .NET 8 with Core, Infrastructure, API, tests, and a React/Vite client.
- An `EngineeringTask` aggregate with explicit states, timestamps, approval timestamps, clarification history, and guarded transitions.
- REST endpoints to create/read a task, answer the single pending question, and approve the requirement summary.
- A clearly named `FakeClarificationEngine` behind `IClarificationEngine`, with three deterministic demo questions.
- SQLite persistence through `Microsoft.Data.Sqlite`, initialized only in Development.
- Swagger/OpenAPI and validation/problem responses.
- A responsive developer-tool UI with progress, focused action states, answer history, errors/loading/success states, and truthful zero-use model telemetry.
- xUnit coverage for valid and invalid transitions, one-question behavior, answer preservation, and approval gating.

## Explicitly unfinished or mocked

- `FakeClarificationEngine` is deterministic development/demo logic. It does not call OpenAI, analyze the requirement, or inspect a repository.
- Repository paths are stored as identifiers only; existence, Git state, files, and architecture are not inspected.
- The requirement summary is mechanically assembled from the original requirement and three answers.
- Planning, plan approval behavior, implementation, builds/tests of a target repository, diff review, repairs, and pull-request preparation are only represented as future workflow states.
- OpenAI integration (including GPT-5.6), token/cost capture, Codex tooling, GitHub OAuth/API integration, and all external cloud services are absent.
- Authentication, authorization, production migrations, hosted deployment, and multi-user isolation are outside this slice.

## Repository map

```text
src/Forge.Core             Domain aggregate, state machine, ports, application service
src/Forge.Infrastructure   SQLite repository and fake clarification adapter
src/Forge.Api              REST API, DTOs, Swagger, dependency composition
tests/Forge.Core.Tests     Domain and application-service tests
web/forge-web              React + Vite + TypeScript client
docs                       Vision, architecture, decisions, submission checklist
```

See [product vision](docs/product-vision.md), [architecture](docs/architecture.md), [decision log](docs/decision-log.md), and the [Build Week checklist](docs/build-week-checklist.md).
