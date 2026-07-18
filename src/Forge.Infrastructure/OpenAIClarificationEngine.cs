using System.Text.Json;
using Forge.Core;

namespace Forge.Infrastructure;

public sealed class OpenAIClarificationEngine(
    ForgeAiOptions options,
    IOpenAIResponsesGateway? gateway,
    ModelCostCalculator costCalculator,
    TimeProvider timeProvider) : IClarificationEngine
{
    internal const string DeveloperInstructions = """
        You are a careful senior software engineer clarifying a work item before planning.
        Return exactly one decision using the supplied strict JSON schema: ask one question or summarize.
        Ask only the highest-value unresolved question and never repeat a question already answered.
        Use previous answers and requirement revision notes. Stop asking when enough material information exists.
        Do not ask optional questions that would not materially affect implementation.
        Separate known facts, assumptions, and unresolved gaps. Never invent repository content.
        Treat the repository value only as an identifier; repository inspection has not occurred.
        Never claim files, tests, technologies, or behavior not supplied in the context.
        A summary must be concise and cover requested outcome, in-scope behavior, acceptance criteria,
        constraints, validation expectations, and explicit unresolved assumptions if any.
        """;

    internal const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "decision": { "type": "string", "enum": ["ask", "summarize"] },
            "question": { "type": ["string", "null"] },
            "summary": { "type": ["string", "null"] },
            "knownFacts": { "type": "array", "items": { "type": "string" } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "unresolvedGaps": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["decision", "question", "summary", "knownFacts", "assumptions", "unresolvedGaps"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClarificationEvaluation> EvaluateAsync(
        EngineeringTask task,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(options.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase))
            throw new ClarificationConfigurationException("The OpenAI clarification adapter is not the active AI mode.");
        if (gateway is null)
            throw new ClarificationConfigurationException("OpenAI mode requires the OPENAI_API_KEY environment variable.");
        if (!options.IsOpenAiConfigurationComplete(true))
            throw new ClarificationConfigurationException("OpenAI mode requires a model, supported reasoning effort, positive output limit, and configured pricing.");

        var callId = Guid.NewGuid();
        var startedAt = timeProvider.GetUtcNow();
        OpenAIResponseEnvelope? response = null;
        try
        {
            response = await gateway.CreateResponseAsync(
                new OpenAIResponseRequest(
                    options.ClarificationModel,
                    options.ClarificationReasoningEffort,
                    options.ClarificationMaxOutputTokens,
                    DeveloperInstructions,
                    BuildCanonicalContext(task),
                    ResponseSchema),
                cancellationToken);

            var completedAt = timeProvider.GetUtcNow();
            var estimatedCost = costCalculator.Calculate(
                options.ClarificationModel,
                response.InputTokens,
                response.CachedInputTokens,
                response.OutputTokens);
            var call = new ModelCallRecord(
                callId,
                ModelCallStage.Clarification,
                "OpenAI",
                options.ClarificationModel,
                options.ClarificationReasoningEffort,
                startedAt,
                completedAt,
                true,
                response.ResponseId,
                response.InputTokens,
                response.CachedInputTokens,
                response.OutputTokens,
                response.ReasoningTokens,
                estimatedCost,
                null);

            return ParseEvaluation(response.OutputText, call);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ClarificationProviderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var category = exception is OpenAITransportException transport ? transport.Category : "invalid_response";
            var safeMessage = exception is OpenAITransportException
                ? exception.Message
                : "OpenAI returned an invalid structured clarification response.";
            var failed = new ModelCallRecord(
                callId,
                ModelCallStage.Clarification,
                "OpenAI",
                options.ClarificationModel,
                options.ClarificationReasoningEffort,
                startedAt,
                timeProvider.GetUtcNow(),
                false,
                response?.ResponseId,
                response?.InputTokens ?? 0,
                response?.CachedInputTokens ?? 0,
                response?.OutputTokens ?? 0,
                response?.ReasoningTokens,
                response is null ? 0m : costCalculator.Calculate(
                    options.ClarificationModel,
                    response.InputTokens,
                    response.CachedInputTokens,
                    response.OutputTokens),
                category);
            throw new ClarificationProviderException(safeMessage, category, failed, exception);
        }
    }

    internal static ClarificationEvaluation ParseEvaluation(string json, ModelCallRecord call)
    {
        StructuredEvaluation? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StructuredEvaluation>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Structured clarification output was malformed.", exception);
        }

        if (parsed is null) throw new InvalidDataException("Structured clarification output was empty.");
        var knownFacts = parsed.KnownFacts ?? [];
        var assumptions = parsed.Assumptions ?? [];
        var unresolvedGaps = parsed.UnresolvedGaps ?? [];
        return parsed.Decision switch
        {
            "ask" when !string.IsNullOrWhiteSpace(parsed.Question) && parsed.Summary is null =>
                ClarificationEvaluation.Ask(parsed.Question, knownFacts, assumptions, unresolvedGaps, call),
            "summarize" when !string.IsNullOrWhiteSpace(parsed.Summary) && parsed.Question is null =>
                ClarificationEvaluation.Summarize(parsed.Summary, knownFacts, assumptions, unresolvedGaps, call),
            _ => throw new InvalidDataException("Structured clarification output violated the one-decision invariant.")
        };
    }

    private static string BuildCanonicalContext(EngineeringTask task) => JsonSerializer.Serialize(new
    {
        repositoryIdentifier = task.Repository,
        originalRequirement = task.OriginalRequirement,
        clarificationAnswers = task.ClarificationAnswers.Select(answer => new { answer.Question, answer.Answer }),
        requirementRevisionNotes = task.RequirementRevisionNotes.Select(note => note.Correction)
    }, JsonOptions);

    private sealed record StructuredEvaluation(
        string? Decision,
        string? Question,
        string? Summary,
        string[]? KnownFacts,
        string[]? Assumptions,
        string[]? UnresolvedGaps);
}
