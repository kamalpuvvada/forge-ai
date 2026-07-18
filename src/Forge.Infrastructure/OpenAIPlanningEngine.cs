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
        Never claim that code was modified or that builds, tests, commands, or manual checks passed.
        Validation commands are proposals only. Identify assumptions, unresolved questions, and risks.
        Avoid unrelated refactoring. If evidence is insufficient, state that limitation instead of guessing.
        Keep the plan narrowly appropriate for the approved requirement. Never emit absolute local paths.
        Return only the supplied strict JSON schema. Step order must start at 1 and increase by 1.
        Every step path must also appear in affectedFiles, and every existing path claim must cite evidence IDs.
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
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "action": { "type": "string", "enum": ["modify", "create", "delete", "inspect"] },
                  "purpose": { "type": "string" },
                  "evidenceIds": { "type": "array", "items": { "type": "string" } },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 }
                },
                "required": ["path", "action", "purpose", "evidenceIds", "confidence"],
                "additionalProperties": false
              }
            },
            "orderedSteps": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "order": { "type": "integer" },
                  "description": { "type": "string" },
                  "affectedPaths": { "type": "array", "items": { "type": "string" } },
                  "evidenceIds": { "type": "array", "items": { "type": "string" } },
                  "expectedResult": { "type": "string" }
                },
                "required": ["order", "description", "affectedPaths", "evidenceIds", "expectedResult"],
                "additionalProperties": false
              }
            },
            "proposedValidationCommands": { "type": "array", "items": { "type": "string" } },
            "risks": { "type": "array", "items": { "type": "string" } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "unresolvedQuestions": { "type": "array", "items": { "type": "string" } },
            "summary": { "type": "string" }
          },
          "required": ["title", "objective", "repositoryUnderstanding", "affectedFiles", "orderedSteps", "proposedValidationCommands", "risks", "assumptions", "unresolvedQuestions", "summary"],
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

            var completedAt = timeProvider.GetUtcNow();
            var call = new ModelCallRecord(
                callId, ModelCallStage.Planning, "OpenAI", options.PlanningModel,
                options.PlanningReasoningEffort, startedAt, completedAt, true, response.ResponseId,
                response.InputTokens, response.CachedInputTokens, response.OutputTokens, response.ReasoningTokens,
                costCalculator.Calculate(options.PlanningModel, response.InputTokens, response.CachedInputTokens, response.OutputTokens), null);
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
            var category = exception is OpenAITransportException transport ? transport.Category : "invalid_plan_response";
            var safeMessage = exception is OpenAITransportException
                ? exception.Message
                : "OpenAI returned an invalid structured implementation plan.";
            var failed = new ModelCallRecord(
                callId, ModelCallStage.Planning, "OpenAI", options.PlanningModel,
                options.PlanningReasoningEffort, startedAt, timeProvider.GetUtcNow(), false, response?.ResponseId,
                response?.InputTokens ?? 0, response?.CachedInputTokens ?? 0, response?.OutputTokens ?? 0,
                response?.ReasoningTokens,
                response is null ? 0m : costCalculator.Calculate(options.PlanningModel, response.InputTokens, response.CachedInputTokens, response.OutputTokens),
                category);
            throw new PlanningProviderException(safeMessage, category, failed, exception);
        }
    }

    internal static ImplementationPlan ParsePlan(string json, PlanningContext context, string model)
    {
        StructuredPlan? parsed;
        try { parsed = JsonSerializer.Deserialize<StructuredPlan>(json, JsonOptions); }
        catch (JsonException exception) { throw new PlanningException("invalid_plan", "Structured planning output was malformed.", exception); }
        if (parsed is null || parsed.AffectedFiles is null || parsed.OrderedSteps is null ||
            parsed.ProposedValidationCommands is null || parsed.Risks is null || parsed.Assumptions is null ||
            parsed.UnresolvedQuestions is null)
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
        var payload = new
        {
            originalRequirement = Safe(context.OriginalRequirement, snapshot.NormalizedRoot),
            approvedRequirementSummary = Safe(context.ApprovedRequirementSummary, snapshot.NormalizedRoot),
            clarificationAnswers = context.ClarificationAnswers.Select(answer => new
            {
                question = Safe(answer.Question, snapshot.NormalizedRoot),
                answer = Safe(answer.Answer, snapshot.NormalizedRoot)
            }),
            revisionNotes = context.RevisionNotes.Select(note => Safe(note.Correction, snapshot.NormalizedRoot)),
            repositorySnapshot = new
            {
                snapshot.IsGitRepository,
                snapshot.Branch,
                snapshot.ShortHeadSha,
                snapshot.WorkingTreeStatus,
                snapshot.TotalDiscoveredFiles,
                snapshot.EligibleTextFileCount,
                snapshot.ExcludedFileCount,
                snapshot.DetectedLanguages,
                snapshot.DetectedExtensions,
                snapshot.ProjectFiles,
                snapshot.TestLocations,
                snapshot.Warnings,
                files = snapshot.Files.Select(file => new
                {
                    file.RelativePath,
                    file.ProbableRole,
                    file.IsTest,
                    file.Association,
                    file.DeclaredSymbols
                })
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
}
