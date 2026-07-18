# Forge AI repository guidance

## Validate changes

- Restore/build backend: `dotnet restore ForgeAI.slnx` then `dotnet build ForgeAI.slnx --no-restore`.
- Run backend tests: `dotnet test ForgeAI.slnx --no-build`.
- Install/validate frontend: `npm ci`, `npm run lint`, then `npm run build` from `web/forge-web`.
- Do not declare work complete until relevant builds and tests have actually passed; report failures exactly.

## Architecture

- `Forge.Core` owns domain behavior, workflow invariants, and ports. It depends on no other project.
- `Forge.Infrastructure` implements Core ports for SQLite and external/fake adapters. It may depend only on Core.
- `Forge.Api` is the HTTP composition boundary. Use DTOs; never expose persistence records.
- `web/forge-web` communicates with the API through REST.
- Keep the modular monolith small. Avoid new projects, heavy frameworks, and dependencies unless they solve a demonstrated need.

## Product invariants

- Show and request exactly one clarification question at a time: one question means one atomic decision dimension. Preserve answers and never ask an answered question again.
- A clear requirement may produce a summary without questions. Corrections are revision notes, never agent questions, and require a new approval.
- Never invent repository facts, user intent, model output, tool results, test success, or completed integrations. Separate known facts, assumptions, gaps, and failures.
- Prefer deterministic code and normal development tools when an LLM is unnecessary. Retrieve only relevant context and reuse stable summaries to control token usage and cost.
- Do not modify a target repository before both the requirement summary and implementation plan receive explicit approval.
- Repository analysis is read-only: contain every path, skip reparse points and likely secrets, never execute repository code, and keep persisted evidence bounded and redacted.
- Deterministic Fake plans must cite real evidence, label proposed validation as unexecuted, and never claim implementation occurred.
- Never commit secrets, API keys, tokens, credentials, or sensitive local paths. Use configuration and ignored local files.
- OpenAI mode reads only `OPENAI_API_KEY`. Never log or persist keys, authorization headers, raw provider responses, or hidden reasoning.
- Never silently fall back from OpenAI to Fake mode. Provider and configuration failures remain visible and safe.
- Require strict structured output and revalidate its one-decision invariant after deserialization.
- Model costs are estimates from centralized configurable pricing; do not hard-code rates in adapters or UI code.
- Do not make a live billable model call unless the user explicitly authorizes that specific validation.
- Resist unrelated scope expansion and unnecessary abstractions. Clearly label fakes, demos, placeholders, and unfinished functionality.
