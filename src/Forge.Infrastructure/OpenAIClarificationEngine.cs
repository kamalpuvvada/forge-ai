using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        One question means one atomic decision dimension, not merely one sentence or one question mark.
        For ask, select the highest-impact unresolved decision and set questionFocus to one concise snake_case
        identifier for only that dimension. The question must correspond exactly to that focus.
        Never combine independent decisions such as format, destination, content, permissions, date range,
        validation, or scope using and, or, commas, parentheses, examples, or multiple clauses.
        Options are allowed only when every option answers the same single decision.
        Good: questionFocus "export_format" with "Which export format should Forge support first?"
        Good: questionFocus "report_content" with "What information must the exported task report contain?"
        Bad: questionFocus "export_format_and_destination" with "Which formats and destinations should be supported?"
        Bad: "Which formats and destinations should be supported, and what should the report include?"
        Ask only that one question and never repeat a question already answered.
        Use previous answers and requirement revision notes. Stop asking when enough material information exists.
        Do not ask optional questions that would not materially affect implementation.
        Separate known facts, assumptions, and unresolved gaps. Never invent repository content.
        Treat the repository value only as an identifier; repository inspection has not occurred.
        Never claim files, tests, technologies, or behavior not supplied in the context.
        A summary must be concise and cover requested outcome, in-scope behavior, acceptance criteria,
        constraints, validation expectations, and explicit unresolved assumptions if any.
        For summarize, question and questionFocus must be null. Reproduce JSON examples readably in summaries;
        do not add unnecessary visible backslashes before quotation marks.
        """;

    internal const string ResponseSchema = """
        {
          "type": "object",
          "properties": {
            "decision": { "type": "string", "enum": ["ask", "summarize"] },
            "question": { "type": ["string", "null"] },
            "questionFocus": { "type": ["string", "null"] },
            "summary": { "type": ["string", "null"] },
            "knownFacts": { "type": "array", "items": { "type": "string" } },
            "assumptions": { "type": "array", "items": { "type": "string" } },
            "unresolvedGaps": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["decision", "question", "questionFocus", "summary", "knownFacts", "assumptions", "unresolvedGaps"],
          "additionalProperties": false
        }
        """;

    internal const int MaximumQuestionLength = 180;
    internal const int MaximumQuestionFocusLength = 80;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

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
        if (parsed.KnownFacts is null || parsed.Assumptions is null || parsed.UnresolvedGaps is null)
            throw new InvalidDataException("Structured clarification output omitted required context arrays.");

        return parsed.Decision switch
        {
            "ask" when IsValidQuestion(parsed.Question) && IsValidQuestionFocus(parsed.QuestionFocus) && parsed.Summary is null =>
                ClarificationEvaluation.Ask(parsed.Question!, parsed.KnownFacts, parsed.Assumptions, parsed.UnresolvedGaps, call),
            "summarize" when !string.IsNullOrWhiteSpace(parsed.Summary) && parsed.Question is null && parsed.QuestionFocus is null =>
                ClarificationEvaluation.Summarize(parsed.Summary, parsed.KnownFacts, parsed.Assumptions, parsed.UnresolvedGaps, call),
            _ => throw new InvalidDataException("Structured clarification output violated the one-decision invariant.")
        };
    }

    private static bool IsValidQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaximumQuestionLength || question != question.Trim())
            return false;
        if (question.Contains('\r') || question.Contains('\n') || question[^1] != '?' || question.Count(character => character == '?') != 1)
            return false;

        return !Regex.IsMatch(question, @"^(?:[-*\u2022]\s+|\d+[.)]\s+)", RegexOptions.CultureInvariant);
    }

    private static bool IsValidQuestionFocus(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus) || focus.Length > MaximumQuestionFocusLength || focus != focus.Trim())
            return false;
        if (!Regex.IsMatch(focus, @"^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            return false;

        var tokens = focus.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return !tokens.Any(token => token is "and" or "or" or "plus" or "versus" or "vs");
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
        string? QuestionFocus,
        string? Summary,
        string[]? KnownFacts,
        string[]? Assumptions,
        string[]? UnresolvedGaps);
}
