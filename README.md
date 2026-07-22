# Forge AI

Forge AI is a trustworthy, explainable, and cost-aware software-engineering workflow. The current slice clarifies and approves requirements, creates an evidence-backed implementation plan through either Fake or OpenAI mode, then generates structured implementation operations through the configured Fake or OpenAI adapter for isolated human diff review.

Accepted implementation operations change only a task-specific linked worktree outside the selected active checkout. After implementation approval, Forge can generate a bounded manual verification plan, accept append-only user-reported outcomes, and route an explicitly failed attempt through bounded failure analysis and one exact-scope correction revision. A corrected revision requires a new human diff approval, a replacement verification plan covering every prior failed result, and a second explicit human pass before `ReadyForDelivery`. Forge executes no verification command. A later, separately approved GitHub.com delivery stage can create one unsigned commit from the exact approved revision, non-force push one Forge-owned branch from `origin` toward `main`, and open one pull request that Forge never automatically merges.

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

Evidence ranking gives strong multiword phrases more weight than generic terms, diversifies across frontend/API/core/infrastructure/test layers, boosts related contracts and tests, and lowers generic documentation and unrelated clarification code. This is requirement-driven rather than feature-keyword hardcoding. Evidence selection supplies bounded read-only context; it does not automatically determine mutation scope. High-confidence approved or corrected `only` path lists instead become authoritative affected-file allowlists, while unrelated selected evidence may remain available solely as context. Explicit exclusions, exact action counts, test-change prohibitions, and repository-validation-command prohibitions are recognized only in bounded directive forms and are enforced deterministically. Plans support up to ten affected files, eight ordered steps, eight proposed validation commands, and twelve compact requirement-coverage mappings. Both planners cite evidence IDs, distinguish test implementation from test execution, and label validation commands as proposals. Explicit plan approval is still required before isolated implementation can begin.

## Safe implementation and diff review

`Generate implementation` is available only for an approved plan. Forge rechecks that the selected path is the exact root of a clean, non-bare Git worktree at the approved commit and rejects in-progress Git operations. A genuinely read-only inspection captures the exact bounded source contexts without creating the worktree root, task directory, branch, ref, lock, hooks directory, isolated Git home, workspace token, or lease. Fake mode then produces its deterministic output locally. OpenAI mode sends only the approved requirement, complete approved plan, base SHA, exact mutating paths/actions, bounded complete source text, source hashes/byte counts/context identities, directly cited evidence, and bounded evidence-backed convention notes. Repository and requirement text are labelled untrusted. Mandatory content is never truncated; over-budget or sensitive context fails before dispatch. Only a complete strictly parsed and deterministically validated proposal may reserve the deterministic `forge/task-{taskId:N}` branch and create a linked sparse worktree beneath `Forge:Implementation:WorktreeRoot`.

Only approved affected paths are materialized. Shared path/file safety rejects absolute and traversal paths, `.git`, reparse points, symlinks, gitlinks, likely secrets, binary/generated/dependency files, executable Git filters, undeclared files, action mismatches, stale hashes, no-op changes, and configured size-limit violations. The Fake engine returns structured create/modify/delete operations and labelled mechanical markers; it never returns or runs shell commands and records no model call. Forge validates the complete operation set before the first write, applies it only in the isolated worktree, and derives bounded unified diffs locally with external diff, text conversion, hooks, submodule recursion, and LFS smudging disabled.

SQLite row-version compare-and-swap updates and a durable owner/attempt lease prevent two Forge processes from advancing one task concurrently. An exclusive operating-system workspace lock is held from context reads through successful result persistence. Durable phases distinguish preparation, mutation, apply, persistence, interruption, and recovery; expired work is never presented as active.

Success persists `AwaitingImplementationReview`, source, base commit, deterministic branch, summary/warnings, per-file hashes, byte/line and addition/deletion counts, explicit diff counts/truncation metadata, and a content-aware worktree fingerprint. The fingerprint manifest binds every persisted per-file review field (using a preview hash), all result totals, completion/certainty metadata, and actual final-file hashes, lines, bytes, file count, and total bytes. It is rechecked under the workspace lock immediately before and after result persistence and whenever runtime availability is projected. Implementation summaries, warnings, operation summaries, failures, source/generated content, and diff previews use the shared sensitive-content detector, including conservative credential-labelled entropy detection. Absolute repository/worktree paths and snapshot roots are not returned by the API; history, detail, deterministic Fake summaries, and PDFs use one stable `Repository <16 hex>` display identifier. Workspaces are intentionally retained for review; missing or altered workspaces preserve their historical persisted review but are reported as recovery-required and are never automatically reset or deleted.

Every successful review is also revision 1 in a bounded authoritative ledger. A separate canonical review fingerprint binds its task/revision identity, complete approved plan, base and branch, source/model, all ordered changed-file review fields and preview hashes, totals, completion time, checkout certainty, and worktree result metadata. `Approve implementation` requires the exact current row version, revision ID, review fingerprint, and persisted proof that the active checkout was verified unchanged. It then records one idempotent human decision and transitions to `ImplementationApproved`. Approval command IDs are repository-wide: SQLite binds each ID to the complete semantic request and commits the binding, task CAS, resulting row version, and immutable timestamp in one transaction. Approval accepts persisted review evidence only: its dedicated dependency graph performs no Git, filesystem, workspace, lock, recovery, build, test, stage, commit, push, or pull-request action and does not prove that the physical worktree remains available. Legacy completed reviews receive one stable synthetic initial revision but are never inferred to be approved. User-facing implementation branch values use the stable `forge/task-[internal-id-omitted]` label; the real tokenized branch remains private persisted operational state.

**UTF-8 BOM limitation:** writable existing files must be strict UTF-8 without a BOM. Repository discovery records this metadata, and plan validation rejects a BOM-bearing modify/delete path before approval, so Forge does not enter `Implementing` and then change or strip its encoding.

## Human verification loop (Slice A)

`ImplementationApproved` can advance through `VerificationPlanning` to `AwaitingManualVerification`. Fake mode creates explicitly mechanical guidance; OpenAI mode uses `gpt-5.6-sol`, medium reasoning, an 8,000-token maximum, strict structured output, and the same hardened gateway without fallback. Plan generation reads only bounded approved evidence and performs no Git, workspace, or target-command action.

> **OpenAI verification-plan limitation:** generating the initial manual verification plan with OpenAI can still fail when otherwise useful model text is rejected by Forge's strict safety classifiers. If a retry continues to fail, use Fake mode for this step: stop Forge, set `Forge__AI__Mode=Fake`, restart it, and generate the manual verification plan again. Mode selection is process-wide, so this is an explicit operator action rather than an automatic fallback; Forge never silently changes OpenAI work to Fake. The Fake plan is deterministic, clearly labelled, non-billable, and still requires the same human execution and reporting.

Manual attempts are bound to the exact approved implementation revision, result fingerprint, and verification-plan fingerprint. Each case update appends an immutable user-reported revision. A confirmed pass requires acceptable outcomes and required evidence for every required case and transitions to `ReadyForDelivery`; a confirmed failed/blocked outcome transitions to `ManualVerificationFailed`. `ReadyForDelivery` means only that the user reported and explicitly confirmed a manual pass. It does not mean Forge independently verified the result or committed, pushed, or opened a pull request. Verification-plan and task-report PDFs preserve these trust labels and audit chronology.

Delivery remains a separate human decision after `ReadyForDelivery`. Forge prepares deterministic metadata only after read-only checks of the exact approved revision, the protected active checkout, GitHub CLI authentication, the single `origin` fetch and effective push destination, and `main`. Approval itself performs no Git or GitHub mutation. Execution stages only approved paths, creates one unsigned commit with hooks disabled, performs one non-force push to the approved Forge-owned branch, and opens one GitHub.com pull request that remains explicitly `NOT MERGED`. The browser accepts no arbitrary remote, base, branch, commit, PR metadata, command, or token; Forge persists no GitHub token. Automated target validation and merge actions remain unavailable.

Verification persistence has an additive task-level `VerificationDataFormatVersion` boundary (`Current = 2`). Version zero is accepted only when Forge can positively establish that the task has no verification rows, pointers, workflow state, verification model calls, or command bindings. The parent is set atomically before the first verification child or binding is inserted and never decreases through normal persistence. Database triggers require the current parent on INSERT and make child/binding ownership immutable on `TaskId` UPDATE. Any verification artifact requires the current parent version and all current response, usage, logical-call, model-call, timing, linkage, and fingerprint fields; coordinated parent-plus-child marker removal is therefore bounded corruption rather than legacy compatibility. Genuine pre-slice tasks remain read-only and GET/PDF/history requests do not migrate them. This is corruption and bounded tamper detection within Forge's persistence trust boundary, not protection against a fully malicious database administrator able to rewrite all application history or code. Across the gateway, domain, API, frontend, and PDFs, `Complete` and `Partial` mean `UsageAvailable = true`; only `Unavailable` means false.

Relative query- or fragment-bearing tokens with a credible repository-file base are structurally treated as repository-path candidates by default, independent of the surrounding action verb. Only bounded, explicit URL, hyperlink, browser-route, documentation-link, or relative-link/example language exempts them; absolute HTTP/HTTPS URLs, JSON Pointers, and MIME types retain their dedicated classifications. The frontend separately decodes compact history and full task responses. Start is eligible only when the exact current plan has no attempt; completed earlier-plan attempts are immutable history, and a replacement plan may start its own bound attempt. Every manual-verification action requires its explicit validated backend eligibility flag plus exact current-plan/current-attempt bindings, and history entries are never an actionable fallback. Contradictory history, eligibility, or pointers reject the replacement, preserve the last valid task, enable no mutation, and issue no request. Missing or malformed telemetry is rejected rather than displayed as complete zero usage. Forge executes no target verification command and performs no delivery action.

## Submission-focused correction loop

When any manual verification case is reported as failed or blocked and the attempt is explicitly completed as failed, Forge moves the task to `ManualVerificationFailed`. From there, the user can generate a failure analysis and, when the failure is classified as an `ImplementationDefect`, approve an exact-scope correction proposal so Forge can generate a re-fix in a separate revision-2 worktree. The corrected diff must be reviewed and approved, and Forge then creates replacement plan 2 covering every failed or blocked result from plan 1. Plan 1, attempt 1, and revision 1 remain immutable audit history; the task reaches `ReadyForDelivery` only after the user manually executes plan 2 and explicitly confirms a passing second attempt.

Failure analysis may use deterministic Fake or strict-schema OpenAI mode. All five classifications are preserved, but classifications other than `ImplementationDefect` do not authorize a code correction. No target command or delivery action is performed, and exactly one correction revision is currently supported. OpenAI API billing remains separate from ChatGPT and Codex.

## Run in OpenAI mode

OpenAI API billing is separate from ChatGPT subscriptions. Configure billing and create a project API key through your OpenAI account first. The key is read only from `OPENAI_API_KEY` and is never persisted by Forge.

```powershell
$secureKey = Read-Host 'Enter OPENAI_API_KEY' -AsSecureString
$env:OPENAI_API_KEY = [Net.NetworkCredential]::new('', $secureKey).Password
$env:Forge__AI__Mode = 'OpenAI'
dotnet run --project .\src\Forge.Api --launch-profile http
```

The clarification model is `gpt-5.6-terra` with `low` reasoning and an 800-token output limit. Planning uses `gpt-5.6-sol`, `medium`, and 6,000 tokens. Implementation uses `gpt-5.6-sol`, `high`, a 32,000-token limit, and a 180-second logical deadline by default. Implementation requests use strict structured output with separate create/modify/delete arrays, `store:false`, background/streaming disabled, truncation disabled, and an empty tools collection. The provider receives no Git, filesystem, shell, function, computer, web/file-search, code-interpreter, background, or multi-agent capability. One bounded retry is allowed only for explicit 429, 502, or 503 responses, or when transport can prove failure occurred before request dispatch. Ambiguous connection failures never retry: avoiding a duplicate billable request takes priority over availability. Every physical request is recorded. API use is billed separately from ChatGPT and Codex. Clear the current shell values when finished:

```powershell
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:Forge__AI__Mode -ErrorAction SilentlyContinue
```

Live Responses API compatibility was demonstrated by one explicitly authorized, disposable, gated implementation smoke test using the configured `gpt-5.6-sol` model. The strict structured proposal returned exactly one approved modify operation, deterministic validation accepted it, and the disposable repository's active checkout remained clean. This proves protocol compatibility for the small approved scenario only; broader model-quality evaluation remains outstanding. The smoke test ran no target build or test command and performed no staging, commit, push, pull-request, or other delivery action. OpenAI API billing for this test was separate from ChatGPT and Codex.

### Separately gated live implementation smoke test

The `Forge.LiveOpenAI.Tests` project is operator-only, excluded from `ForgeAI.slnx`, and not referenced by either standard test project. Default solution restore/build/test commands therefore cannot discover or execute its provider test, even when a key and both flags are present. The smoke test creates a unique disposable Git repository and SQLite database, calls only the OpenAI implementation engine, verifies the proposal, confirms the disposable checkout stayed clean, and deletes its temporary directory. It refuses transport unless the API key, both explicit gates, the dedicated project target, and the Category filter are all present. Run only after separately approving billable validation:

```powershell
$env:FORGE_ENABLE_LIVE_OPENAI_TEST = 'true'
$env:FORGE_LIVE_TEST_EXPLICIT_FILTER = 'true'
$env:OPENAI_API_KEY = '<environment only>'
dotnet test `
  .\tests\Forge.LiveOpenAI.Tests\Forge.LiveOpenAI.Tests.csproj `
  --filter "Category=LiveOpenAIImplementation"
Remove-Item Env:FORGE_ENABLE_LIVE_OPENAI_TEST, Env:FORGE_LIVE_TEST_EXPLICIT_FILTER -ErrorAction SilentlyContinue
```

An API key alone, flags alone, a direct project invocation without the exact filter, or a filter without both flags cannot dispatch this smoke test. The explicitly authorized live smoke completed successfully; normal solution commands remain physically unable to discover it.

## AI boundary and failure behavior

- `IClarificationEngine.EvaluateAsync` is asynchronous and cancellation-aware.
- `IPlanningEngine.CreatePlanAsync` is asynchronous and cancellation-aware and returns a plan plus optional model-call telemetry.
- `IImplementationEngine.GenerateAsync` is an asynchronous structured-operation boundary. Exactly one configured Fake or OpenAI engine is registered; OpenAI never falls back to Fake.
- Every evaluation returns exactly one decision: ask one question or provide a summary. One question means one atomic decision dimension.
- The OpenAI adapter uses official `OpenAI` 2.12.0 and the Responses API.
- Strict JSON Schema structured output is used for clarification, planning, and OpenAI implementation. Domain validation remains authoritative.
- All three OpenAI stages share one strict topology policy: completed status, one valid response ID, one assistant message, one output text, reasoning-only auxiliary items, and no refusal, tool, unknown, or duplicate output. Duplicate JSON properties are rejected recursively before deserialization.
- Planning sends the original and approved requirements once, answers, correction notes, compact snapshot metadata, and selected excerpts. Absolute roots, full files, raw responses, secrets, and hidden reasoning are excluded. `previous_response_id` is not used.
- Fake mode records no model usage.
- OpenAI mode never falls back to Fake mode. Configuration, authentication, timeout, rate-limit, malformed-output, and provider failures return safe Problem Details.
- Incomplete Responses are classified before JSON parsing. Output-limit truncation and content-filter incompleteness retain usage/cost under distinct safe categories; partial output is never persisted or returned.
- A failed plan remains recoverable with the same fresh snapshot and evidence. `Retry plan generation` makes one new planning request and does not repeat repository analysis; a stale snapshot instead requires an explicitly labelled re-analysis.
- API keys, authorization headers, complete raw responses, hidden reasoning, and user-input secrets are not stored in model-call records.

## Token and estimated-cost telemetry

Forge stores call identity, clarification/planning stage, provider/model, reasoning effort, timestamps, outcome, response ID when available, nullable input/cached/output/reasoning tokens, safe failure category, and a nullable estimated USD cost. One shared verification-usage contract classifies all four valid, bounded counters as `Complete`; one or more independently valid counters as `Partial`; and no trustworthy counters as `Unavailable`. Cached input cannot exceed total input, reasoning cannot exceed output because Responses output includes reasoning, and malformed fields are never coerced to zero. Cached-only usage is retained as partial but cannot be costed. A stored zero estimate is therefore distinct from a missing estimate. Newly priced calls also retain the exact input, cached-input, and output rates used for their estimate as a per-call pricing snapshot. Selected implementation pricing must be nonnegative and no more than $100,000 per million tokens; output tokens are bounded to 100,000 and timeout to 600 seconds. Invalid configuration fails before provider use, while any unexpected post-dispatch cost overflow preserves the call with unavailable estimated cost.

Costs are estimates based on configurable per-million-token rates. Cached input is not charged twice:

```text
uncached input = total input - cached input
estimate = uncached input × input rate
         + cached input × cached rate
         + output × output rate
```

The SDK output-token total already includes reasoning tokens, so reasoning tokens are a breakdown and are not added again.

Cost display is resolved consistently in the verification engine, shared resolver, task API, frontend, and PDF exports. Complete usage uses cached and uncached pricing normally. Partial usage is costed only when total input and output are known but cached input is absent; all input is then priced as uncached and the result is labelled a conservative partial estimate. Cached-only and other insufficient combinations retain telemetry but have unavailable cost. Task totals expose separate complete, partial-conservative, and combined-available subtotals plus unavailable and possibly-dispatched-unavailable counts; the combined subtotal is never presented as a complete task estimate. A stale legacy numeric estimate with unavailable usage remains readable in persistence but projects as unavailable, while a provider-reported valid zero remains numeric zero. All monetary values are estimates, not invoices, and ambiguous provider billing remains unknown.

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
- `POST /api/tasks/{id}/implementation` (configured Fake/OpenAI mode; clean approved Git state required)
- `POST /api/tasks/{id}/implementation-approval` (exact persisted-review approval; no Git or workspace access)
- `GET /api/tasks/{id}/export/pdf`
- `GET /api/tasks/{id}/export/plan-pdf`
- `GET /api/system/capabilities`

The capabilities endpoint reports clarification, planning, implementation, verification planning, failure analysis, correction, and bounded delivery configuration separately. Commit, push, and pull-request capability fields are true only when the dedicated delivery boundary finds both Git and GitHub CLI available; that system configuration never implies task eligibility. Silent fallback, automated validation, and auto-merge remain false. The endpoint returns only safe mode/model/feature availability and never returns an API key, token, or secret-derived value.

The PDF endpoint reads the persisted task without changing it and returns `application/pdf` as an attachment named `forge-task-{taskId}.pdf`. It is a workflow-stage-aware audit export: earlier reports retain requirement, clarification, and bounded revision history; planned tasks add bounded repository-analysis evidence and the complete persisted plan; implementation attempts add persisted phase/failure state; and implementation-review reports add persisted hashes, byte/line and addition/deletion counts, truncation metadata, and bounded diff previews. Completion-time active-checkout evidence is labelled separately from export-time workspace observation. Export uses a dedicated non-mutating projection that checks only persisted identity consistency and filesystem presence: it does not acquire locks, run Git, reconcile worktrees, or reverify the active checkout. Plan commands remain proposals labelled `NOT EXECUTED`; they are never presented as execution or external-validation evidence. Persisted historical requirement summaries are preserved, followed by an explicit note that their development note describes summary-generation time. Absolute paths, workspace tokens (including task-branch suffixes), repository/worktree identities, provider payloads, global configuration, keys, and connection details are redacted. Export-local character, line, page, and collection budgets produce explicit omission notices. The frontend prevents overlapping downloads, reports safe failures, and always revokes its temporary object URL.

The separate plan-PDF endpoint is available for a complete persisted plan while awaiting approval and throughout approved implementation/review states. It returns `forge-plan-{taskId}.pdf` and derives its exact `PROPOSED PLAN — NOT APPROVED` or `APPROVED PLAN` label from persisted workflow state. Proposed validations are always marked `NOT EXECUTED`. The frontend uses `/?task={taskId}` as its canonical native deep link; history selection, refresh, Back, and Forward reopen the existing task through the detail endpoint.

PDF generation uses PdfPig 0.1.15 (Apache-2.0), registered behind `IEngineeringTaskPdfExporter`. Its Standard 14 font path is deliberately limited to ASCII plus the WinAnsi em dash verified by PDF extraction tests. Common typographic punctuation is otherwise normalized and unsupported Unicode scalars are replaced with `?`. Text is wrapped and paginated rather than clipped.

## SQLite development schema

The Development API creates the database automatically. Existing databases gain known clarification, snapshot, evidence, plan, fingerprint, implementation workspace/result/failure, workflow-timestamp, and bounded implementation-revision columns through `PRAGMA table_info` and narrowly scoped `ALTER TABLE ADD COLUMN` commands. The additive `ImplementationApprovalCommands` table provides globally unique durable command bindings. Model calls are stored as bounded JSON. Legacy `Implementing` tasks without workspace metadata are read as `PlanApproved`; persisted implementation workspaces instead pass an explicit exhaustive workflow/phase/lease/failure/timestamp/revision matrix and remain resumable or explicitly recovery-required. One SQLite `SELECT` returns implementation and revision JSON values with character and UTF-8 byte lengths from the same statement snapshot; every length is checked before any JSON string is materialized. Approved tasks must also match a complete bounded command binding and verified checkout-completion evidence. SQLite provider failures are normalized to a Core-safe persistence exception without SQL, database paths, connection strings, or provider diagnostics. Snapshot, evidence, plan, workspace, result, failure, and the maximum-six revision ledger are bounded JSON.

Microsoft.Data.Sqlite remains on the .NET 8 servicing line at 8.0.22. Forge explicitly selects the coherent SQLitePCLRaw `bundle_e_sqlite3` 2.1.12 package set, which resolves to the patched SQLite 3.53.3 native runtime; a focused test opens a temporary database and verifies `select sqlite_version()` remains at or above the advisory-safe minimum.

## Manual PDF validation

Run the API and frontend in Fake mode as described above. Complete clarification, approve the requirement, analyze the repository, create and approve a plan, generate the deterministic Fake implementation, then choose **Download task PDF**. Confirm the browser downloads a `forge-task-{taskId}.pdf`, open it, and inspect the chronology, repository-analysis metadata, approved plan, implementation review, changed-file hashes/counts/diff previews, runtime status, and model-call/cost sections. Confirm proposed commands remain explicitly unexecuted. Fake mode deliberately records no billable model calls, so pricing-provenance scenarios are covered by automated tests.

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
- Authentication, multi-user isolation, production migrations, and hosted deployment are not implemented.
- Fake changes are mechanical workflow fixtures, not semantically correct AI implementation.
- Worktree cleanup, candidate rejection/regeneration, automated validation execution, merge, and generalized delivery recovery are not implemented. Exactly one approved correction revision is supported. Implementation generation and approval never stage, commit, push, or create a pull request; only the later exact, separately approved delivery stage can perform one bounded commit/push/PR sequence. Retained worktrees and branches require deliberate future lifecycle tooling.
- OpenAI implementation is available only when explicitly configured; its strict schema grants no tools and all returned operations remain untrusted until local validation succeeds.

See [product vision](docs/product-vision.md), [architecture](docs/architecture.md), [decision log](docs/decision-log.md), and [Build Week checklist](docs/build-week-checklist.md).
