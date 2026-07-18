using OpenAI.Responses;
using System.ClientModel;

namespace Forge.Infrastructure;

public sealed record OpenAIResponseRequest(
    string Model,
    string ReasoningEffort,
    int MaxOutputTokens,
    string DeveloperInstructions,
    string UserInput,
    string JsonSchema,
    string SchemaName = "forge_clarification_evaluation",
    string SchemaDescription = "Exactly one clarification decision.");

public sealed record OpenAIResponseEnvelope(
    string ResponseId,
    string OutputText,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int? ReasoningTokens,
    OpenAIResponseStatus Status = OpenAIResponseStatus.Completed,
    OpenAIResponseIncompleteReason? IncompleteReason = null);

public enum OpenAIResponseStatus
{
    Unknown,
    Queued,
    InProgress,
    Completed,
    Incomplete,
    Failed,
    Cancelled
}

public enum OpenAIResponseIncompleteReason
{
    Unknown,
    MaxOutputTokens,
    ContentFilter
}

public interface IOpenAIResponsesGateway
{
    Task<OpenAIResponseEnvelope> CreateResponseAsync(
        OpenAIResponseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAITransportException(string category, string safeMessage, Exception? inner = null)
    : Exception(safeMessage, inner)
{
    public string Category { get; } = category;
}

public sealed class SdkOpenAIResponsesGateway(string apiKey) : IOpenAIResponsesGateway
{
    private readonly ResponsesClient _client = new(apiKey);

    public async Task<OpenAIResponseEnvelope> CreateResponseAsync(
        OpenAIResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = new CreateResponseOptions
        {
            Model = request.Model,
            Instructions = request.DeveloperInstructions,
            MaxOutputTokenCount = request.MaxOutputTokens,
            StoredOutputEnabled = false,
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = new ResponseReasoningEffortLevel(request.ReasoningEffort)
            },
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    request.SchemaName,
                    BinaryData.FromString(request.JsonSchema),
                    request.SchemaDescription,
                    true)
            }
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(request.UserInput));

        try
        {
            var result = (await _client.CreateResponseAsync(options, cancellationToken)).Value;
            var usage = result.Usage;
            return new OpenAIResponseEnvelope(
                result.Id,
                result.GetOutputText(),
                usage?.InputTokenCount ?? 0,
                usage?.InputTokenDetails?.CachedTokenCount ?? 0,
                usage?.OutputTokenCount ?? 0,
                usage?.OutputTokenDetails?.ReasoningTokenCount,
                NormalizeStatus(result.Status),
                NormalizeIncompleteReason(result.IncompleteStatusDetails?.Reason));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenAITransportException("timeout", "The OpenAI request timed out.");
        }
        catch (ClientResultException exception)
        {
            var category = exception.Status switch
            {
                401 or 403 => "authentication",
                408 or 504 => "timeout",
                429 => "rate_limit",
                >= 500 => "provider_error",
                _ => "invalid_request"
            };
            throw new OpenAITransportException(category, SafeMessage(category), exception);
        }
    }

    internal static OpenAIResponseStatus NormalizeStatus(ResponseStatus? status)
    {
        if (status is null) return OpenAIResponseStatus.Unknown;
        if (status == ResponseStatus.Queued) return OpenAIResponseStatus.Queued;
        if (status == ResponseStatus.InProgress) return OpenAIResponseStatus.InProgress;
        if (status == ResponseStatus.Completed) return OpenAIResponseStatus.Completed;
        if (status == ResponseStatus.Incomplete) return OpenAIResponseStatus.Incomplete;
        if (status == ResponseStatus.Failed) return OpenAIResponseStatus.Failed;
        if (status == ResponseStatus.Cancelled) return OpenAIResponseStatus.Cancelled;
        return OpenAIResponseStatus.Unknown;
    }

    internal static OpenAIResponseIncompleteReason? NormalizeIncompleteReason(ResponseIncompleteStatusReason? reason)
    {
        if (reason is null) return null;
        if (reason == ResponseIncompleteStatusReason.MaxOutputTokens) return OpenAIResponseIncompleteReason.MaxOutputTokens;
        if (reason == ResponseIncompleteStatusReason.ContentFilter) return OpenAIResponseIncompleteReason.ContentFilter;
        return OpenAIResponseIncompleteReason.Unknown;
    }

    private static string SafeMessage(string category) => category switch
    {
        "authentication" => "OpenAI rejected the configured credentials.",
        "rate_limit" => "OpenAI rate-limited the request.",
        "timeout" => "The OpenAI request timed out.",
        "invalid_request" => "OpenAI rejected the request.",
        _ => "OpenAI could not complete the request."
    };
}
