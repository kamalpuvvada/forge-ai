using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class OpenAIPlanningEngine(
    ForgeAiOptions options,
    IOpenAIResponsesGateway? gateway,
    ModelCostCalculator costCalculator,
    TimeProvider timeProvider) : IPlanningEngine
{
    internal const string DeveloperInstructions = """
        You are a careful senior software engineer preparing a small executable implementation plan.
        Rely only on the supplied approved requirement, compact repository metadata, and selected evidence.
        Cite supplied evidence IDs for every claim about an existing file. Distinguish existing files from
        proposed new files, and never invent an existing path. Every modify, delete, or inspect item must cite
        evidence. A create item must be a repository-relative path that is absent from the supplied file list.
        Never claim that code was modified or that builds, tests, commands, or manual checks already ran.
        Describe validation only as proposed commands and expected outcomes, using imperative, future, or
        conditional language. Never use past-tense or present-perfect language implying execution. Say
        "run the tests", not "tests were run"; "tests must pass", not "tests passed"; and "verify the PDF",
        not "the PDF was verified". Identify assumptions, unresolved questions, and risks.
        Avoid unrelated refactoring. If evidence is insufficient, state that limitation instead of guessing.
        Keep the plan narrowly appropriate for the approved requirement. Never emit absolute local paths.
        Return only the supplied strict JSON schema. Step order must start at 1 and increase by 1.
        Every step path must also appear in affectedFiles, and every existing path claim must cite evidence IDs.
        The supplied "Allowed existing affected paths" list is authoritative. Every modify, delete, or inspect
        path must be chosen exactly from that list. An existing repository path absent from the list must not
        appear in affectedFiles or steps. Use create only for genuinely new paths. Do not infer an existing file
        from naming conventions or another file's imports unless that file appears in selected evidence.
        Keep the output compact: at most 10 affected files, 8 steps, 8 validation commands, 4 risks,
        4 assumptions, and 4 unresolved questions. Use concise, non-repetitive descriptions. Do not repeat
        the full approved requirement, quote evidence excerpts, or copy repository content. Objective,
        repository understanding, and summary should each normally be one concise paragraph. Keep
        affected-file purposes and step descriptions brief. Refer to evidence only by ID.
        When a plan correction is supplied, directly address it, preserve valid portions of the previous plan,
        and add missing domain or persistence work when supported by refreshed evidence. State when evidence
        remains insufficient, and do not merely reword the previous plan. Obey the supplied deterministic explicit
        plan constraints exactly: an authoritative affected-path allowlist is mutation scope, not a suggestion;
        exclusions and action counts are binding; and explicit test or validation-command prohibitions must not be
        contradicted. Selected evidence outside an allowlist remains read-only context only.
        When the approved requirement explicitly requires backend tests, identify an existing or proposed backend
        test file in affectedFiles and a concrete implementation step that changes it. Apply the same rule for
        explicitly required frontend tests. Validation commands such as dotnet test, lint, or build describe test
        execution and do not count as planning test implementation. Existing test files must cite evidence from
        that path; proposed test files must use create.
        For a generated artifact such as PDF, CSV, or document export, identify the concrete service or component
        that generates it, its API endpoint or controller integration, and any required project or package change.
        If no new dependency is needed, explain the implementation approach in the affected-file purpose or step.
        Do not defer the core generator to later analysis, and do not treat a controller as the generator unless
        repository evidence shows that controllers own such logic by design.
        Follow evidence-backed architectural boundaries, including frontend API helpers, service/application layers,
        dependency registration, project or package files, and test projects. Do not put page-level networking in a
        component when evidence identifies an API helper. Add compact requirementCoverage entries for material
        approved outcomes, required tests, and error handling; each entry must reference declared paths and steps.
        """;

    internal const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "objective": { "type": "string" },
            "repositoryUnderstanding": { "type": "string" },
            "affectedFiles": {
              "type": "array",
              "maxItems": 10,
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "action": { "type": "string", "enum": ["modify", "create", "delete", "inspect"] },
                  "purpose": { "type": "string" },
                  "evidenceIds": { "type": "array", "maxItems": 6, "items": { "type": "string" } },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
                },
                "required": ["path", "action", "purpose", "evidenceIds", "confidence"],
                "additionalProperties": false
              }
            },
            "orderedSteps": {
              "type": "array",
              "maxItems": 8,
              "items": {
                "type": "object",
                "properties": {
                  "order": { "type": "integer" },
                  "description": { "type": "string" },
                  "affectedPaths": { "type": "array", "maxItems": 6, "items": { "type": "string" } },
                  "evidenceIds": { "type": "array", "maxItems": 6, "items": { "type": "string" } },
                  "expectedResult": { "type": "string" }
                },
                "required": ["order", "description", "affectedPaths", "evidenceIds", "expectedResult"],
                "additionalProperties": false
              }
            },
            "proposedValidationCommands": { "type": "array", "maxItems": 8, "items": { "type": "string" } },
            "risks": { "type": "array", "maxItems": 4, "items": { "type": "string" } },
            "assumptions": { "type": "array", "maxItems": 4, "items": { "type": "string" } },
            "unresolvedQuestions": { "type": "array", "maxItems": 4, "items": { "type": "string" } },
            "requirementCoverage": {
              "type": "array",
              "maxItems": 12,
              "items": {
                "type": "object",
                "properties": {
                  "requirement": { "type": "string" },
                  "affectedPaths": { "type": "array", "maxItems": 10, "items": { "type": "string" } },
                  "stepOrders": { "type": "array", "maxItems": 8, "items": { "type": "integer" } }
                },
                "required": ["requirement", "affectedPaths", "stepOrders"],
                "additionalProperties": false
              }
            },
            "summary": { "type": "string" }
          },
          "required": ["title", "objective", "repositoryUnderstanding", "affectedFiles", "orderedSteps", "proposedValidationCommands", "risks", "assumptions", "unresolvedQuestions", "requirementCoverage", "summary"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<PlanningEvaluation> CreatePlanAsync(
        PlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(options.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase))
            throw new PlanningException("planning_configuration", "The OpenAI planning adapter is not the active AI mode.");
        if (gateway is null)
            throw new PlanningException("planning_configuration", "OpenAI planning requires the OPENAI_API_KEY environment variable.");
        if (!options.IsPlanningConfigurationComplete(true))
            throw new PlanningException("planning_configuration", "OpenAI planning requires a model, supported reasoning effort, positive output limit, and configured pricing.");
        if (!costCalculator.TryGetPricingSnapshot(options.PlanningModel, out var pricingSnapshot))
            throw new PlanningException("planning_configuration", "OpenAI planning requires configured pricing for the planning model.");

        var callId = Guid.NewGuid();
        var startedAt = timeProvider.GetUtcNow();
        OpenAIResponseEnvelope? response = null;
        try
        {
            response = await gateway.CreateResponseAsync(new OpenAIResponseRequest(
                options.PlanningModel,
                options.PlanningReasoningEffort,
                options.PlanningMaxOutputTokens,
                DeveloperInstructions,
                BuildCanonicalContext(context),
                ResponseSchema,
                "forge_implementation_plan",
                "An evidence-backed implementation plan."), cancellationToken);

            if (response.Status != OpenAIResponseStatus.Completed)
                throw CreateCompletionFailure(response, callId, startedAt, pricingSnapshot);

            var completedAt = timeProvider.GetUtcNow();
            var call = new ModelCallRecord(
                callId, ModelCallStage.Planning, "OpenAI", options.PlanningModel,
                options.PlanningReasoningEffort, startedAt, completedAt, true, response.ResponseId,
                response.InputTokens, response.CachedInputTokens, response.OutputTokens, response.ReasoningTokens,
                costCalculator.Calculate(pricingSnapshot, response.InputTokens, response.CachedInputTokens, response.OutputTokens).TotalCostUsd,
                null,
                pricingSnapshot);
            var plan = ParsePlan(response.OutputText, context, options.PlanningModel);
            return new PlanningEvaluation(plan, call);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlanningProviderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var category = exception switch
            {
                OpenAITransportException transport => transport.Category,
                PlanningException { Category: "missing_direct_evidence" } => "missing_direct_evidence",
                _ => "invalid_plan_response"
            };
            var safeMessage = exception switch
            {
                OpenAITransportException => exception.Message,
                PlanningException { Category: "invalid_plan_field" } planning => planning.Message,
                PlanningException { Category: "missing_direct_evidence" } planning => planning.Message,
                PlanningException when exception.Message == ImplementationPlanValidator.ValidationAlreadyPerformedMessage =>
                    ImplementationPlanValidator.ValidationAlreadyPerformedMessage,
                _ => "OpenAI returned an invalid structured implementation plan."
            };
            var failed = new ModelCallRecord(
                callId, ModelCallStage.Planning, "OpenAI", options.PlanningModel,
                options.PlanningReasoningEffort, startedAt, timeProvider.GetUtcNow(), false, response?.ResponseId,
                response?.InputTokens, response?.CachedInputTokens, response?.OutputTokens,
                response?.ReasoningTokens,
                response is null ? null : costCalculator.Calculate(pricingSnapshot, response.InputTokens, response.CachedInputTokens, response.OutputTokens).TotalCostUsd,
                category,
                response is null ? null : pricingSnapshot);
            throw new PlanningProviderException(safeMessage, category, failed, exception);
        }
    }

    private PlanningProviderException CreateCompletionFailure(
        OpenAIResponseEnvelope response,
        Guid callId,
        DateTimeOffset startedAt,
        ModelPricingSnapshot pricingSnapshot)
    {
        var (category, safeMessage) = response.Status == OpenAIResponseStatus.Incomplete
            ? response.IncompleteReason switch
            {
                OpenAIResponseIncompleteReason.MaxOutputTokens => (
                    "output_truncated",
                    "The planning response reached its output limit before the structured plan was complete."),
                OpenAIResponseIncompleteReason.ContentFilter => (
                    "content_filter",
                    "The planning response was stopped by the provider's content filter."),
                _ => ("incomplete_response", "The planning response was incomplete.")
            }
            : ("provider_response_incomplete", "OpenAI did not return a completed planning response.");
        var failed = new ModelCallRecord(
            callId, ModelCallStage.Planning, "OpenAI", options.PlanningModel,
            options.PlanningReasoningEffort, startedAt, timeProvider.GetUtcNow(), false, response.ResponseId,
            response.InputTokens, response.CachedInputTokens, response.OutputTokens, response.ReasoningTokens,
            costCalculator.Calculate(pricingSnapshot, response.InputTokens, response.CachedInputTokens, response.OutputTokens).TotalCostUsd,
            category,
            pricingSnapshot);
        return new PlanningProviderException(safeMessage, category, failed);
    }

    internal static ImplementationPlan ParsePlan(string json, PlanningContext context, string model)
    {
        StructuredPlan? parsed;
        try { parsed = JsonSerializer.Deserialize<StructuredPlan>(json, JsonOptions); }
        catch (JsonException exception) { throw new PlanningException("invalid_plan", "Structured planning output was malformed.", exception); }
        if (parsed is null || parsed.AffectedFiles is null || parsed.OrderedSteps is null ||
            parsed.ProposedValidationCommands is null || parsed.Risks is null || parsed.Assumptions is null ||
            parsed.UnresolvedQuestions is null || parsed.RequirementCoverage is null)
            throw new PlanningException("invalid_plan", "Structured planning output omitted required fields.");

        var affected = parsed.AffectedFiles.Select(file => new PlannedFileChange(
            file.Path ?? string.Empty,
            ParseAction(file.Action),
            file.Purpose ?? string.Empty,
            file.EvidenceIds ?? [],
            file.Confidence)).ToArray();
        var steps = parsed.OrderedSteps.Select(step => new ImplementationStep(
            step.Order,
            step.Description ?? string.Empty,
            step.AffectedPaths ?? [],
            step.EvidenceIds ?? [],
            step.ExpectedResult ?? string.Empty)).ToArray();
        var coverage = parsed.RequirementCoverage.Select(item => new RequirementCoverageItem(
            item.Requirement ?? string.Empty,
            item.AffectedPaths ?? [],
            item.StepOrders ?? [])).ToArray();
        var plan = new ImplementationPlan(
            parsed.Title ?? string.Empty,
            parsed.Objective ?? string.Empty,
            parsed.RepositoryUnderstanding ?? string.Empty,
            affected,
            steps,
            parsed.ProposedValidationCommands,
            parsed.Risks,
            parsed.Assumptions,
            parsed.UnresolvedQuestions,
            coverage,
            parsed.Summary ?? string.Empty,
            PlanningSource.OpenAI,
            model,
            context.CreatedAt,
            context.Snapshot.Fingerprint);
        ImplementationPlanValidator.Validate(plan, context.Snapshot, context.Evidence);
        return plan;
    }

    internal static string BuildCanonicalContext(PlanningContext context)
    {
        var snapshot = context.Snapshot;
        var constraints = PlanConstraintPolicy.Derive(context);
        var payload = new
        {
            allowedExistingAffectedPaths = context.Evidence.Select(item => Safe(item.RelativePath, snapshot.NormalizedRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
            approvedRequirementSummary = Safe(context.ApprovedRequirementSummary, snapshot.NormalizedRoot),
            clarificationAnswers = context.ClarificationAnswers.Select(answer => new
            {
                question = Safe(answer.Question, snapshot.NormalizedRoot),
                answer = Safe(answer.Answer, snapshot.NormalizedRoot)
            }),
            revisionNotes = context.RevisionNotes.Select(note => Safe(note.Correction, snapshot.NormalizedRoot)),
            latestPlanCorrection = context.LatestPlanRevision is null
                ? null
                : Safe(context.LatestPlanRevision.Correction, snapshot.NormalizedRoot),
            previousPlanAffectedPaths = (context.PreviousPlanAffectedPaths ?? [])
                .Select(path => Safe(path, snapshot.NormalizedRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase),
            explicitPlanConstraints = new
            {
                authoritativeAffectedPaths = constraints.AuthoritativePaths?.Select(item => new
                {
                    path = Safe(item.Path, snapshot.NormalizedRoot),
                    action = item.Action?.ToString()
                }),
                excludedPaths = constraints.ExcludedPaths.Select(path => Safe(path, snapshot.NormalizedRoot)),
                exactActionCounts = constraints.ExactActionCounts.ToDictionary(
                    item => item.Key.ToString(), item => item.Value, StringComparer.Ordinal),
                prohibitedActions = constraints.ProhibitedActions.Select(action => action.ToString()),
                constraints.TestChangesProhibited,
                constraints.TestExecutionProhibited,
                constraints.RepositoryValidationCommandsProhibited,
                constraints.DiffMetadataReviewOnly
            },
            repositorySnapshot = new
            {
                snapshot.TotalDiscoveredFiles,
                snapshot.EligibleTextFileCount,
                snapshot.ExcludedFileCount,
                snapshot.DetectedLanguages,
                snapshot.DetectedExtensions,
                projectFiles = snapshot.ProjectFiles.Select(path => Safe(path, snapshot.NormalizedRoot)),
                testLocations = snapshot.TestLocations.Select(path => Safe(path, snapshot.NormalizedRoot)),
                warnings = snapshot.Warnings.Select(warning => Safe(warning, snapshot.NormalizedRoot))
            },
            evidence = context.Evidence.Select(item => new
            {
                item.Id,
                item.RelativePath,
                item.StartLine,
                item.EndLine,
                item.ReasonSelected,
                excerpt = Safe(item.Excerpt, snapshot.NormalizedRoot)
            })
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static PlannedFileAction ParseAction(string? action) => action switch
    {
        "modify" => PlannedFileAction.Modify,
        "create" => PlannedFileAction.Create,
        "delete" => PlannedFileAction.Delete,
        "inspect" => PlannedFileAction.Inspect,
        _ => throw new PlanningException("invalid_plan", "Structured planning output contained an unknown file action.")
    };

    private static string Redact(string value) => DeterministicEvidenceSelectionService.RedactSensitiveValues(value);

    private static string Safe(string value, string normalizedRoot)
    {
        var withoutRoot = string.IsNullOrWhiteSpace(normalizedRoot)
            ? value
            : value.Replace(normalizedRoot, "[LOCAL_REPOSITORY]", StringComparison.OrdinalIgnoreCase)
                .Replace(normalizedRoot.Replace('\\', '/'), "[LOCAL_REPOSITORY]", StringComparison.OrdinalIgnoreCase)
                .Replace(normalizedRoot.Replace('/', '\\'), "[LOCAL_REPOSITORY]", StringComparison.OrdinalIgnoreCase);
        return Redact(withoutRoot);
    }

    private sealed record StructuredPlan(
        string? Title,
        string? Objective,
        string? RepositoryUnderstanding,
        StructuredAffectedFile[]? AffectedFiles,
        StructuredStep[]? OrderedSteps,
        string[]? ProposedValidationCommands,
        string[]? Risks,
        string[]? Assumptions,
        string[]? UnresolvedQuestions,
        StructuredRequirementCoverage[]? RequirementCoverage,
        string? Summary);

    private sealed record StructuredAffectedFile(
        string? Path,
        string? Action,
        string? Purpose,
        string[]? EvidenceIds,
        decimal Confidence);

    private sealed record StructuredStep(
        int Order,
        string? Description,
        string[]? AffectedPaths,
        string[]? EvidenceIds,
        string? ExpectedResult);

    private sealed record StructuredRequirementCoverage(
        string? Requirement,
        string[]? AffectedPaths,
        int[]? StepOrders);
}
