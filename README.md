# Forge AI

Forge AI is a trustworthy, explainable, and cost-aware software-engineering workflow. The current slice clarifies and approves requirements, inspects a local repository read-only, selects bounded evidence deterministically, creates a labelled Fake-mode implementation plan, and requires explicit plan approval.

Target-repository modification, validation execution, review, and pull-request creation remain explicitly unavailable.

## Prerequisites

- Windows PowerShell 7 or Windows PowerShell 5.1
- .NET 8 SDK or a newer SDK capable of targeting .NET 8
- Node.js 20 or newer with npm
- Git

```powershell
dotnet --version
node --version
npm --version
git --version
```

## Restore and validate

```powershell
dotnet restore .\ForgeAI.slnx
dotnet build .\ForgeAI.slnx --configuration Release --no-restore
dotnet test .\ForgeAI.slnx --configuration Release --no-build
Push-Location .\web\forge-web
npm ci
npm run lint
npm run build
Pop-Location
```

## Run in Fake mode

Fake mode is the committed default and does not require a key or make billable requests.

Terminal 1:

```powershell
$env:Forge__AI__Mode = 'Fake'
dotnet run --project .\src\Forge.Api --launch-profile http
```

Terminal 2:

```powershell
Push-Location .\web\forge-web
npm run dev
Pop-Location
```

Open `http://localhost:5173`; Swagger is at `http://localhost:5180/swagger`.

The deterministic clarification adapter asks up to three development questions. After requirement approval, Forge can analyze the repository read-only and create a deterministic evidence-backed plan without any model call.

## Read-only repository planning

`Analyze repository and create plan` normalizes and contains the supplied path, uses read-only Git metadata commands when applicable, skips reparse points, and never runs repository code, package installation, builds, tests, or Git mutations. It persists compact metadata and bounded evidence rather than unrestricted file contents.

Default limits are 5,000 discovered files, 256 KB per text file, 20 MB of considered text, 12 evidence files, and 60,000 evidence characters. Generated/minified/binary content, common build/dependency folders, and likely secret files such as `.env`, private keys, and credential files are excluded. Obvious sensitive key/value lines are replaced with `[REDACTED]`; this is a conservative safeguard, not a guarantee that a repository contains no secrets.

Evidence ranking combines requirement terms with paths, declared symbols, content, project/module context, tests, configuration, and entry points. The Fake planner cites evidence IDs, labels validation commands as proposals, and stops after explicit plan approval without modifying the target.

## Run in OpenAI mode

OpenAI API billing is separate from ChatGPT subscriptions. Configure billing and create a project API key through your OpenAI account first. The key is read only from `OPENAI_API_KEY` and is never persisted by Forge.

```powershell
$secureKey = Read-Host 'Enter OPENAI_API_KEY' -AsSecureString
$env:OPENAI_API_KEY = [Net.NetworkCredential]::new('', $secureKey).Password
$env:Forge__AI__Mode = 'OpenAI'
dotnet run --project .\src\Forge.Api --launch-profile http
```

The clarification model is `gpt-5.6-terra`, reasoning effort is `low`, and output is limited to 800 tokens. Clear the current shell values when finished:

```powershell
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:Forge__AI__Mode -ErrorAction SilentlyContinue
```

This implementation task did **not** make a live or billable OpenAI request.

## AI boundary and failure behavior

- `IClarificationEngine.EvaluateAsync` is asynchronous and cancellation-aware.
- Every evaluation returns exactly one decision: ask one question or provide a summary. One question means one atomic decision dimension.
- The OpenAI adapter uses official `OpenAI` 2.12.0 and the Responses API.
- Strict JSON Schema structured output requires an internal `questionFocus`; the ask/summarize and atomic-question invariants are validated again after deserialization.
- Each turn sends compact canonical context: repository identifier, original requirement, answers, and revision notes. `previous_response_id` is not used.
- Fake mode records no model usage.
- OpenAI mode never falls back to Fake mode. Configuration, authentication, timeout, rate-limit, malformed-output, and provider failures return safe Problem Details.
- API keys, authorization headers, complete raw responses, hidden reasoning, and user-input secrets are not stored in model-call records.

## Token and estimated-cost telemetry

Forge stores call identity, stage, provider/model, reasoning effort, timestamps, outcome, response ID when available, input/cached/output/reasoning tokens, safe failure category, and estimated USD cost. The API returns task totals and the UI provides expandable details.

Costs are estimates based on configurable per-million-token rates. Cached input is not charged twice:

```text
uncached input = total input - cached input
estimate = uncached input × input rate
         + cached input × cached rate
         + output × output rate
```

The SDK output-token total already includes reasoning tokens, so reasoning tokens are a breakdown and are not added again.

| Model | Input / 1M | Cached input / 1M | Output / 1M |
|---|---:|---:|---:|
| gpt-5.6-sol | $5.00 | $0.50 | $30.00 |
| gpt-5.6-terra | $2.50 | $0.25 | $15.00 |
| gpt-5.6-luna | $1.00 | $0.10 | $6.00 |

## API

- `POST /api/tasks`
- `GET /api/tasks/{id}`
- `POST /api/tasks/{id}/answers`
- `POST /api/tasks/{id}/requirement-revision`
- `POST /api/tasks/{id}/requirement-approval`
- `POST /api/tasks/{id}/repository-analysis`
- `POST /api/tasks/{id}/plan`
- `POST /api/tasks/{id}/plan-approval`
- `GET /api/system/capabilities`

The capabilities endpoint returns only safe mode/model/feature availability and never returns the API key or a secret-derived value.

## SQLite development schema

The Development API creates the database automatically. Existing databases gain known clarification, snapshot, evidence, plan, fingerprint, and planning-timestamp columns through `PRAGMA table_info` and narrowly scoped `ALTER TABLE ADD COLUMN` commands. Snapshot, evidence, and plan payloads are bounded JSON.

After stopping the API, reset local data with:

```powershell
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-shm -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-wal -Force -ErrorAction SilentlyContinue
```

## Current limitations

- Fake clarification remains generic, and Fake planning uses ranking heuristics rather than semantic model reasoning.
- No automated test sends a real OpenAI request.
- Redaction covers obvious sensitive key names but cannot guarantee that every secret pattern is detected.
- Git evidence uses tracked files returned by `git ls-files`; ignored and untracked content is not selected.
- Known-fact/assumption/gap arrays are validated at the provider boundary but are not yet first-class UI context.
- Authentication, multi-user isolation, production migrations, hosted deployment, and provider retry policy are not implemented.
- Implementation and downstream engineering stages are state-machine placeholders only.

See [product vision](docs/product-vision.md), [architecture](docs/architecture.md), [decision log](docs/decision-log.md), and [Build Week checklist](docs/build-week-checklist.md).
