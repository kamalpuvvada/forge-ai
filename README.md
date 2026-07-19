# Forge AI

Forge AI is a trustworthy, explainable, and cost-aware software-engineering workflow. The current slice clarifies and approves requirements, creates an evidence-backed implementation plan through either Fake or OpenAI mode, then exercises safe implementation and human diff review with a deterministic Fake adapter.

Fake implementation changes only a task-specific linked worktree outside the selected active checkout. Validation execution, AI implementation generation, correction/rejection, commit, push, and pull-request creation remain explicitly unavailable.

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

The deterministic clarification adapter asks up to three development questions. After requirement and plan approval, Fake mode can create an explicitly labelled mechanical change set in an isolated worktree for human diff review without any model call.

## Read-only repository planning

`Analyze repository and create plan` normalizes and contains the supplied path, uses read-only Git metadata commands when applicable, skips reparse points, and never runs repository code, package installation, builds, tests, or Git mutations. It persists compact metadata and bounded evidence rather than unrestricted file contents.

Default limits are 5,000 discovered files, 256 KB per text file, 20 MB of considered text, 12 evidence files, and 60,000 evidence characters. Generated/minified/binary content, common build/dependency folders, and likely secret files such as `.env`, private keys, and credential files are excluded. Obvious sensitive key/value lines are replaced with `[REDACTED]`; this is a conservative safeguard, not a guarantee that a repository contains no secrets.

Evidence ranking gives strong multiword phrases more weight than generic terms, diversifies across frontend/API/core/infrastructure/test layers, boosts related contracts and tests, and lowers generic documentation and unrelated clarification code. This is requirement-driven rather than feature-keyword hardcoding. Plans support up to ten affected files, eight ordered steps, eight proposed validation commands, and twelve compact requirement-coverage mappings. Both planners cite evidence IDs, distinguish test implementation from test execution, and label validation commands as proposals. Explicit plan approval is still required before isolated Fake implementation can begin.

## Safe Fake implementation and diff review

`Generate implementation` is available only for an approved plan in Fake mode. Forge rechecks that the selected path is the exact root of a clean, non-bare Git worktree at the approved commit and rejects in-progress Git operations. A read-only preflight first captures the exact bounded source contexts, generates the exact deterministic Fake output once, and validates all operations, transformed per-file/total sizes, no-op rules, and sensitive content while the task is still `PlanApproved`. Only successful preflight can persist a lease and let preparation reserve the deterministic `forge/task-{taskId:N}` branch and create a linked sparse worktree beneath the configurable `Forge:Implementation:WorktreeRoot`; preparation byte-for-byte rechecks the source contexts before creating owned Git artifacts and after materialization. The active checkout's HEAD, branch, complete index/status, and bounded hashes of every tracked regular working-tree file are captured; sparse indexes, malformed or truncated stage metadata, nonzero stages, assume-unchanged/skip-worktree entries, symlinks, and an insufficient fingerprint budget fail closed. Every Git command uses one startup-resolved absolute executable, a sanitized non-interactive environment, a verified empty Forge-owned hooks directory, and fixed configuration that disables fsmonitor, hooks, submodule recursion, external diff/textconv, paging, credentials, and automatic maintenance.

Only approved affected paths are materialized. Shared path/file safety rejects absolute and traversal paths, `.git`, reparse points, symlinks, gitlinks, likely secrets, binary/generated/dependency files, executable Git filters, undeclared files, action mismatches, stale hashes, no-op changes, and configured size-limit violations. The Fake engine returns structured create/modify/delete operations and labelled mechanical markers; it never returns or runs shell commands and records no model call. Forge validates the complete operation set before the first write, applies it only in the isolated worktree, and derives bounded unified diffs locally with external diff, text conversion, hooks, submodule recursion, and LFS smudging disabled.

SQLite row-version compare-and-swap updates and a durable owner/attempt lease prevent two Forge processes from advancing one task concurrently. An exclusive operating-system workspace lock is held from context reads through successful result persistence. Durable phases distinguish preparation, mutation, apply, persistence, interruption, and recovery; expired work is never presented as active.

Success persists `AwaitingImplementationReview`, source, base commit, deterministic branch, summary/warnings, per-file hashes, byte/line and addition/deletion counts, explicit diff counts/truncation metadata, and a content-aware worktree fingerprint. The fingerprint manifest binds every persisted per-file review field (using a preview hash), all result totals, completion/certainty metadata, and actual final-file hashes, lines, bytes, file count, and total bytes. It is rechecked under the workspace lock immediately before and after result persistence and whenever runtime availability is projected. Implementation summaries, warnings, operation summaries, failures, source/generated content, and diff previews use the shared sensitive-content detector, including conservative credential-labelled entropy detection. Absolute repository/worktree paths and snapshot roots are not returned by the API; history, detail, deterministic Fake summaries, and PDFs use one stable `Repository <16 hex>` display identifier. Workspaces are intentionally retained for review; missing or altered workspaces preserve their historical persisted review but are reported as recovery-required and are never automatically reset or deleted.

**UTF-8 BOM limitation:** writable existing files must be strict UTF-8 without a BOM. Repository discovery records this metadata, and plan validation rejects a BOM-bearing modify/delete path before approval, so Forge does not enter `Implementing` and then change or strip its encoding.

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
- `IImplementationEngine.GenerateAsync` is an asynchronous structured-operation boundary. This slice registers only `FakeImplementationEngine`; OpenAI implementation configuration is reported unavailable and never falls back to Fake.
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
- `GET /api/tasks` (up to 50 lightweight summaries, most recently updated first)
- `GET /api/tasks/{id}`
- `POST /api/tasks/{id}/answers`
- `POST /api/tasks/{id}/requirement-revision`
- `POST /api/tasks/{id}/requirement-approval`
- `POST /api/tasks/{id}/repository-analysis`
- `POST /api/tasks/{id}/plan`
- `POST /api/tasks/{id}/plan-approval`
- `POST /api/tasks/{id}/implementation` (Fake mode; clean approved Git state required)
- `GET /api/tasks/{id}/export/pdf`
- `GET /api/tasks/{id}/export/plan-pdf`
- `GET /api/system/capabilities`

The capabilities endpoint returns only safe mode/model/feature availability and never returns the API key or a secret-derived value.

The PDF endpoint reads the persisted task without changing it and returns `application/pdf` as an attachment named `forge-task-{taskId}.pdf`. The report includes approved task content, clarification history, model-call usage, per-call pricing provenance, and an estimated total. It omits repository paths, provider payloads, global configuration, keys, and connection details. The frontend offers the download after plan approval, prevents overlapping requests, reports safe failures, and always revokes its temporary object URL.

The separate plan-PDF endpoint is available for a complete persisted plan while awaiting approval and throughout approved implementation/review states. It returns `forge-plan-{taskId}.pdf` and derives its exact `PROPOSED PLAN — NOT APPROVED` or `APPROVED PLAN` label from persisted workflow state. Proposed validations are always marked `NOT EXECUTED`. The frontend uses `/?task={taskId}` as its canonical native deep link; history selection, refresh, Back, and Forward reopen the existing task through the detail endpoint.

PDF generation uses PdfPig 0.1.15 (Apache-2.0), registered behind `IEngineeringTaskPdfExporter`. Its Standard 14 font path is deliberately limited to ASCII plus the WinAnsi em dash verified by PDF extraction tests. Common typographic punctuation is otherwise normalized and unsupported Unicode scalars are replaced with `?`. Text is wrapped and paginated rather than clipped.

## SQLite development schema

The Development API creates the database automatically. Existing databases gain known clarification, snapshot, evidence, plan, fingerprint, implementation workspace/result/failure, and workflow-timestamp columns through `PRAGMA table_info` and narrowly scoped `ALTER TABLE ADD COLUMN` commands. Model calls are stored as bounded JSON. Legacy `Implementing` tasks without workspace metadata are read as `PlanApproved`; persisted implementation workspaces instead pass an explicit exhaustive workflow/phase/lease/failure/timestamp matrix and remain resumable or explicitly recovery-required. One SQLite `SELECT` returns implementation JSON values with character and UTF-8 byte lengths from the same statement snapshot; every length is checked before any implementation JSON string is materialized. SQLite provider failures are normalized to a Core-safe persistence exception without SQL, database paths, connection strings, or provider diagnostics. Snapshot, evidence, plan, workspace, result, and failure payloads are bounded JSON.

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
- Fake changes are mechanical workflow fixtures, not semantically correct AI implementation.
- Worktree cleanup, implementation correction/rejection, validation execution, commit/push, and pull-request creation are not implemented. Retained worktrees and branches require deliberate future lifecycle tooling.
- OpenAI implementation generation is configuration-only and unavailable; no implementation schema is sent to a provider in this slice.

See [product vision](docs/product-vision.md), [architecture](docs/architecture.md), [decision log](docs/decision-log.md), and [Build Week checklist](docs/build-week-checklist.md).
