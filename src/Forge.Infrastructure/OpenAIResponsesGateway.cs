using OpenAI.Responses;
using System.ClientModel;

namespace Forge.Infrastructure;

public sealed record OpenAIResponseRequest(
    string Model,
    string ReasoningEffort,
    int MaxOutputTokens,
    string DeveloperInstructions,
    string UserInput,
    string JsonSchema);

public sealed record OpenAIResponseEnvelope(
    string ResponseId,
    string OutputText,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int? ReasoningTokens);

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
                    "forge_clarification_evaluation",
                    BinaryData.FromString(request.JsonSchema),
                    "Exactly one clarification decision.",
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
                usage?.OutputTokenDetails?.ReasoningTokenCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenAITransportException("timeout", "The OpenAI clarification request timed out.");
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

    private static string SafeMessage(string category) => category switch
    {
        "authentication" => "OpenAI rejected the configured credentials.",
        "rate_limit" => "OpenAI rate-limited the clarification request.",
        "timeout" => "The OpenAI clarification request timed out.",
        "invalid_request" => "OpenAI rejected the clarification request.",
        _ => "OpenAI could not complete the clarification request."
    };
}
