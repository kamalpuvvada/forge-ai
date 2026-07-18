# Forge AI repository guidance

## Validate changes

- Restore/build backend: `dotnet restore ForgeAI.slnx` then `dotnet build ForgeAI.slnx --no-restore`.
- Run backend tests: `dotnet test ForgeAI.slnx --no-build`.
- Install/build frontend: `npm install` then `npm run build` from `web/forge-web`.
- Do not declare work complete until relevant builds and tests have actually passed; report failures exactly.

## Architecture

- `Forge.Core` owns domain behavior, workflow invariants, and ports. It depends on no other project.
- `Forge.Infrastructure` implements Core ports for SQLite and external/fake adapters. It may depend only on Core.
- `Forge.Api` is the HTTP composition boundary. Use DTOs; never expose persistence records.
- `web/forge-web` communicates with the API through REST.
- Keep the modular monolith small. Avoid new projects, heavy frameworks, and dependencies unless they solve a demonstrated need.

## Product invariants

- Show and request exactly one clarification question at a time. Preserve answers and never ask an answered question again.
- Never invent repository facts, user intent, model output, tool results, test success, or completed integrations. Separate known facts, assumptions, gaps, and failures.
- Prefer deterministic code and normal development tools when an LLM is unnecessary. Retrieve only relevant context and reuse stable summaries to control token usage and cost.
- Do not modify a target repository before both the requirement summary and implementation plan receive explicit approval.
- Never commit secrets, API keys, tokens, credentials, or sensitive local paths. Use configuration and ignored local files.
- Resist unrelated scope expansion and unnecessary abstractions. Clearly label fakes, demos, placeholders, and unfinished functionality.
