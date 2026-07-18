# Forge AI

Forge AI is a trustworthy, explainable, and cost-aware software-engineering workflow. The current slice clarifies and approves requirements, inspects a local repository read-only, selects bounded evidence deterministically, creates an evidence-backed implementation plan through either Fake or OpenAI mode, and requires explicit plan approval.

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
npm test -- --run
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

Evidence ranking gives strong multiword phrases more weight than generic terms, diversifies across frontend/API/core/infrastructure/test layers, boosts related contracts and tests, and lowers generic documentation and unrelated clarification code. This is requirement-driven rather than feature-keyword hardcoding. Plans support up to ten affected files, eight ordered steps, eight proposed validation commands, and twelve compact requirement-coverage mappings. Both planners cite evidence IDs, distinguish test implementation from test execution, label validation commands as proposals, and stop at `PlanApproved` without modifying the target.

## Run in OpenAI mode

OpenAI API billing is separate from ChatGPT subscriptions. Configure billing and create a project API key through your OpenAI account first. The key is read only from `OPENAI_API_KEY` and is never persisted by Forge.

```powershell
$secureKey = Read-Host 'Enter OPENAI_API_KEY' -AsSecureString
$env:OPENAI_API_KEY = [Net.NetworkCredential]::new('', $secureKey).Password
$env:Forge__AI__Mode = 'OpenAI'
dotnet run --project .\src\Forge.Api --launch-profile http
```

The clarification model is `gpt-5.6-terra` with `low` reasoning and an 800-token output limit. The planning model is `gpt-5.6-sol` with `medium` reasoning and a 6,000-token output allowance, which includes visible output and reasoning tokens. OpenAI planning sends only approved requirement context, repository totals/stack/project/test metadata, and bounded redacted evidence; it excludes the absolute root, unrestricted file contents, and the complete repository file list. Clear the current shell values when finished:

```powershell
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:Forge__AI__Mode -ErrorAction SilentlyContinue
```

This implementation task did **not** make a live or billable OpenAI request.

## AI boundary and failure behavior

- `IClarificationEngine.EvaluateAsync` is asynchronous and cancellation-aware.
- `IPlanningEngine.CreatePlanAsync` is asynchronous and cancellation-aware and returns a plan plus optional model-call telemetry.
- Every evaluation returns exactly one decision: ask one question or provide a summary. One question means one atomic decision dimension.
- The OpenAI adapter uses official `OpenAI` 2.12.0 and the Responses API.
- Strict JSON Schema structured output is used for clarification and planning; domain validation independently enforces evidence IDs, existing/create path truth, sequential steps, safe relative paths, and proposal-only validation language.
- Planning sends the original and approved requirements once, answers, correction notes, compact snapshot metadata, and selected excerpts. Absolute roots, full files, raw responses, secrets, and hidden reasoning are excluded. `previous_response_id` is not used.
- Fake mode records no model usage.
- OpenAI mode never falls back to Fake mode. Configuration, authentication, timeout, rate-limit, malformed-output, and provider failures return safe Problem Details.
- Incomplete Responses are classified before JSON parsing. Output-limit truncation and content-filter incompleteness retain usage/cost under distinct safe categories; partial output is never persisted or returned.
- A failed plan remains recoverable with the same fresh snapshot and evidence. `Retry plan generation` makes one new planning request and does not repeat repository analysis; a stale snapshot instead requires an explicitly labelled re-analysis.
- API keys, authorization headers, complete raw responses, hidden reasoning, and user-input secrets are not stored in model-call records.

## Token and estimated-cost telemetry

Forge stores call identity, clarification/planning stage, provider/model, reasoning effort, timestamps, outcome, response ID when available, nullable input/cached/output/reasoning tokens, safe failure category, and a nullable estimated USD cost. A stored zero estimate is therefore distinct from a missing estimate. Newly priced calls also retain the exact input, cached-input, and output rates used for their estimate as a per-call pricing snapshot.

Costs are estimates based on configurable per-million-token rates. Cached input is not charged twice:

```text
uncached input = total input - cached input
estimate = uncached input × input rate
         + cached input × cached rate
         + output × output rate
```

The SDK output-token total already includes reasoning tokens, so reasoning tokens are a breakdown and are not added again.

Cost display is resolved consistently in the task API and PDF export. A valid stored pricing snapshot is authoritative; a legacy stored estimate is preserved when its snapshot is unavailable; an unpriced legacy call may be re-estimated from current model pricing only when all usage is valid; otherwise its cost is unavailable. Totals exclude unavailable calls and identify themselves as partial. All monetary values are estimates, not invoices.

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
- `GET /api/tasks/{id}/export/pdf`
- `GET /api/system/capabilities`

The capabilities endpoint returns only safe mode/model/feature availability and never returns the API key or a secret-derived value.

The PDF endpoint reads the persisted task without changing it and returns `application/pdf` as an attachment named `forge-task-{taskId}.pdf`. The report includes approved task content, clarification history, model-call usage, per-call pricing provenance, and an estimated total. It omits repository paths, provider payloads, global configuration, keys, and connection details. The frontend offers the download after plan approval, prevents overlapping requests, reports safe failures, and always revokes its temporary object URL.

PDF generation uses PdfPig 0.1.15 (Apache-2.0), registered behind `IEngineeringTaskPdfExporter`. Its Standard 14 font path is deliberately limited to ASCII plus the WinAnsi em dash verified by PDF extraction tests. Common typographic punctuation is otherwise normalized and unsupported Unicode scalars are replaced with `?`. Text is wrapped and paginated rather than clipped.

## SQLite development schema

The Development API creates the database automatically. Existing databases gain known clarification, snapshot, evidence, plan, fingerprint, and planning-timestamp columns through `PRAGMA table_info` and narrowly scoped `ALTER TABLE ADD COLUMN` commands. Model calls are already stored as a bounded JSON payload, so nullable estimates and per-call pricing snapshots are additive JSON properties: legacy payloads load without rewriting or inventing historical rates, while newly saved calls round-trip the exact snapshot. Legacy approved plans stored as `Implementing` are read as structured `PlanApproved` plans because no implementation artifacts existed in that slice. Snapshot, evidence, model-call, and plan payloads are bounded JSON.

Microsoft.Data.Sqlite remains on the .NET 8 servicing line at 8.0.22. Forge explicitly selects the coherent SQLitePCLRaw `bundle_e_sqlite3` 2.1.12 package set, which resolves to the patched SQLite 3.53.3 native runtime; a focused test opens a temporary database and verifies `select sqlite_version()` remains at or above the advisory-safe minimum.

## Manual PDF validation

Run the API and frontend in Fake mode as described above. Complete clarification, approve the requirement, analyze the repository, create and approve a plan, then choose **Download task PDF**. Confirm the browser downloads a `forge-task-{taskId}.pdf`, open it, and inspect the requirement, clarification, status, and model-call/cost sections. Fake mode deliberately records no billable model calls, so pricing-provenance scenarios are covered by automated tests.

After stopping the API, reset local data with:

```powershell
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-shm -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath .\src\Forge.Api\data\forge.db-wal -Force -ErrorAction SilentlyContinue
```

## Current limitations

- Fake clarification remains generic, and deterministic evidence selection still uses explainable lexical/structural heuristics rather than semantic embeddings.
- No automated test sends a real OpenAI request.
- Redaction covers obvious sensitive key names but cannot guarantee that every secret pattern is detected.
- Git evidence uses tracked files returned by `git ls-files`; ignored and untracked content is not selected.
- Known-fact/assumption/gap arrays are validated at the provider boundary but are not yet first-class UI context.
- Authentication, multi-user isolation, production migrations, hosted deployment, and provider retry policy are not implemented.
- Implementation and downstream engineering stages are state-machine placeholders only.

See [product vision](docs/product-vision.md), [architecture](docs/architecture.md), [decision log](docs/decision-log.md), and [Build Week checklist](docs/build-week-checklist.md).
