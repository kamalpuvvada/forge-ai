# Product vision

## Problem

Software-engineering agents often move from a vague prompt to code too quickly. They can assume missing requirements, send excessive repository context, make opaque changes, and report results without enough evidence. That makes them costly and difficult to trust.

## Target user

Forge AI is for developers and small engineering teams who want agentic implementation speed without surrendering review, scope control, or visibility into cost and evidence.

## Product promise

Forge AI converts an engineering requirement into a reviewed pull request through explicit, explainable gates. It asks only the most important unanswered question, preserves approved context, shows what it knows, validates changes with normal tools, and reports model usage and estimated cost.

## Differentiator

Trust is part of the workflow rather than a disclaimer. Requirement approval, evidence-backed planning, implementation, validation, diff review, and pull-request preparation are distinct states with enforced transitions. Deterministic tools are preferred to model calls whenever possible.

## Competition demo story

1. A developer selects a local repository and enters an ambiguous work item.
2. Forge asks one focused question at a time while previous answers remain visible.
3. Forge presents a concise summary and cannot continue without explicit approval.
4. The approved task becomes ready for evidence-backed planning.
5. The complete competition story will then show repository retrieval, an approved plan, implementation, tests, review, repairs, a pull-request summary, and per-run usage/cost.

The current vertical slice demonstrates approved requirements/planning, deterministic Fake or strict-schema OpenAI implementation proposals, isolated task-specific worktree mutation only after complete local validation, bounded human diff review, exact human approval of persisted review evidence, and a bound manual verification loop. Forge generates the checklist but runs nothing; outcomes are explicitly user-reported. Legacy verification format zero is accepted only for tasks with no verification artifact, while any artifact requires the current parent and child format, immutable child ownership, and exact current pointers. Provider usage remains explicitly complete, partial, or unavailable, conservative partial cost is separated from complete estimates, and ambiguous billing remains unknown. The frontend fully decodes actionable task JSON before use; each manual action requires explicit backend eligibility, history is never an actionable fallback, and a malformed replacement preserves the last valid selected task while enabling no mutation. `ReadyForDelivery` means the exact approved revision received acceptable recorded outcomes and an explicit human pass confirmation, not that Forge independently validated, committed, pushed, or opened a pull request. Failure analysis and correction remain the next slice.

## Three-day non-goals

- A general-purpose autonomous coding platform or multi-agent orchestration system.
- GitHub OAuth, hosted persistence, organization management, billing, or production deployment.
- Support for every language, source host, IDE, or issue tracker.
- Fully autonomous merging or bypassing human approval gates.
- Claiming the fake clarification adapter performs repository analysis or AI reasoning.
