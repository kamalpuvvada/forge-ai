using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core;
using OpenAI.Responses;

namespace Forge.Infrastructure;

public sealed record OpenAIResponseRequest(
    string Model,
    string ReasoningEffort,
    int MaxOutputTokens,
    string DeveloperInstructions,
    string UserInput,
    string JsonSchema,
    string SchemaName = "forge_clarification_evaluation",
    string SchemaDescription = "Exactly one clarification decision.",
    string? ClientRequestId = null);

public sealed record OpenAIResponseEnvelope(
    string ResponseId,
    string OutputText,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    OpenAIResponseStatus Status = OpenAIResponseStatus.Completed,
    OpenAIResponseIncompleteReason? IncompleteReason = null,
    string? ProviderRequestId = null,
    IReadOnlyList<OpenAIResponseOutputItem>? OutputItems = null,
    bool UsageAvailable = true);

public sealed record OpenAIResponseOutputItem(
    OpenAIResponseOutputItemKind Kind,
    string? Role,
    IReadOnlyList<OpenAIResponseContent> Content);

public sealed record OpenAIResponseContent(OpenAIResponseContentKind Kind, string Text);

public enum OpenAIResponseOutputItemKind { Reasoning, Message, Tool, Unknown }
public enum OpenAIResponseContentKind { OutputText, Refusal, Unknown }

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

public enum OpenAITransportDispatchCertainty
{
    DefinitelyBeforeRequestDispatch,
    DispatchMayHaveOccurred,
    ResponseReceived
}

public sealed class OpenAITransportException(
    string category,
    string safeMessage,
    Exception? inner = null,
    int? statusCode = null,
    OpenAITransportDispatchCertainty dispatchCertainty = OpenAITransportDispatchCertainty.DispatchMayHaveOccurred,
    TimeSpan? retryAfter = null)
    : Exception(safeMessage, inner)
{
    public string Category { get; } = category;
    public int? StatusCode { get; } = statusCode;
    public OpenAITransportDispatchCertainty DispatchCertainty { get; } = dispatchCertainty;
    public bool Retryable => StatusCode is 429 or 502 or 503 ||
                             StatusCode is null && DispatchCertainty == OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class SdkOpenAIResponsesGateway(string apiKey) : IOpenAIResponsesGateway
{
    private readonly ResponsesClient _client = new(apiKey);

    public async Task<OpenAIResponseEnvelope> CreateResponseAsync(
        OpenAIResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var content = BinaryContent.Create(BinaryData.FromString(BuildRequestJson(request)));
        var options = new RequestOptions
        {
            CancellationToken = cancellationToken,
            ErrorOptions = ClientErrorBehaviors.NoThrow,
            BufferResponse = true
        };
        options.SetHeader("X-Client-Request-Id",
            string.IsNullOrWhiteSpace(request.ClientRequestId) ? Guid.NewGuid().ToString("D") : request.ClientRequestId);

        try
        {
            var result = await _client.CreateResponseAsync(content, options);
            var response = result.GetRawResponse();
            var providerRequestId = GetSafeHeader(response.Headers, "x-request-id");
            if (response.Status is < 200 or >= 300)
                throw CreateTransportFailure(response.Status, response.Headers);
            return ParseResponse(response.Content, providerRequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenAITransportException("timeout", "The OpenAI request timed out.");
        }
        catch (ClientResultException exception)
        {
            throw CreateTransportFailure(exception.Status, null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw CreateHttpRequestFailure(exception);
        }
    }

    internal static OpenAITransportException CreateHttpRequestFailure(HttpRequestException exception) =>
        new("provider_error", "OpenAI could not complete the request.", exception,
            dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred);

    internal static string BuildRequestJson(OpenAIResponseRequest request)
    {
        JsonNode schema;
        try { schema = JsonNode.Parse(request.JsonSchema) ?? throw new JsonException(); }
        catch (JsonException exception) { throw new InvalidOperationException("The configured structured-output schema is invalid.", exception); }

        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort },
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["instructions"] = request.DeveloperInstructions,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = request.UserInput
                }
            },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.SchemaName,
                    ["description"] = request.SchemaDescription,
                    ["strict"] = true,
                    ["schema"] = schema
                }
            },
            ["tools"] = new JsonArray(),
            ["store"] = false,
            ["background"] = false,
            ["stream"] = false,
            ["truncation"] = "disabled"
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    internal static OpenAIResponseEnvelope ParseResponse(BinaryData body, string? providerRequestId)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var id = OpenAIProviderIdentifier.Normalize(String(root, "id")) ?? string.Empty;
            var status = NormalizeStatus(String(root, "status"));
            var incompleteReason = NormalizeIncompleteReason(
                root.TryGetProperty("incomplete_details", out var incomplete) && incomplete.ValueKind == JsonValueKind.Object
                    ? String(incomplete, "reason")
                    : null);
            var outputItems = ParseOutputItems(root);
            var outputText = string.Concat(outputItems
                .Where(item => item.Kind == OpenAIResponseOutputItemKind.Message)
                .SelectMany(item => item.Content)
                .Where(part => part.Kind == OpenAIResponseContentKind.OutputText)
                .Select(part => part.Text));

            var usageAvailable = TryParseUsage(root, out var inputTokens, out var cachedTokens,
                out var outputTokens, out var reasoningTokens);
            return new OpenAIResponseEnvelope(id, outputText, inputTokens, cachedTokens, outputTokens,
                reasoningTokens, status, incompleteReason, providerRequestId, outputItems, usageAvailable);
        }
        catch (JsonException exception)
        {
            throw new OpenAITransportException("invalid_response", "OpenAI returned a malformed response envelope.", exception);
        }
    }

    private static IReadOnlyList<OpenAIResponseOutputItem> ParseOutputItems(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<OpenAIResponseOutputItem>();
        foreach (var item in output.EnumerateArray())
        {
            var type = String(item, "type");
            var kind = type switch
            {
                "reasoning" => OpenAIResponseOutputItemKind.Reasoning,
                "message" => OpenAIResponseOutputItemKind.Message,
                "function_call" or "web_search_call" or "file_search_call" or "computer_call" or
                    "image_generation_call" or "code_interpreter_call" or "apply_patch_call" or "mcp_call" =>
                    OpenAIResponseOutputItemKind.Tool,
                _ => OpenAIResponseOutputItemKind.Unknown
            };
            var parts = new List<OpenAIResponseContent>();
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    var partType = String(part, "type");
                    parts.Add(partType switch
                    {
                        "output_text" => new(OpenAIResponseContentKind.OutputText, String(part, "text") ?? string.Empty),
                        "refusal" => new(OpenAIResponseContentKind.Refusal, String(part, "refusal") ?? string.Empty),
                        _ => new(OpenAIResponseContentKind.Unknown, string.Empty)
                    });
                }
            }
            items.Add(new(kind, String(item, "role"), parts));
        }
        return items;
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

    internal static OpenAIResponseStatus NormalizeStatus(string? status) => status switch
    {
        "queued" => OpenAIResponseStatus.Queued,
        "in_progress" => OpenAIResponseStatus.InProgress,
        "completed" => OpenAIResponseStatus.Completed,
        "incomplete" => OpenAIResponseStatus.Incomplete,
        "failed" => OpenAIResponseStatus.Failed,
        "cancelled" => OpenAIResponseStatus.Cancelled,
        _ => OpenAIResponseStatus.Unknown
    };

    internal static OpenAIResponseIncompleteReason? NormalizeIncompleteReason(ResponseIncompleteStatusReason? reason)
    {
        if (reason is null) return null;
        if (reason == ResponseIncompleteStatusReason.MaxOutputTokens) return OpenAIResponseIncompleteReason.MaxOutputTokens;
        if (reason == ResponseIncompleteStatusReason.ContentFilter) return OpenAIResponseIncompleteReason.ContentFilter;
        return OpenAIResponseIncompleteReason.Unknown;
    }

    internal static OpenAIResponseIncompleteReason? NormalizeIncompleteReason(string? reason) => reason switch
    {
        null => null,
        "max_output_tokens" => OpenAIResponseIncompleteReason.MaxOutputTokens,
        "content_filter" => OpenAIResponseIncompleteReason.ContentFilter,
        _ => OpenAIResponseIncompleteReason.Unknown
    };

    private static OpenAITransportException CreateTransportFailure(
        int status,
        PipelineResponseHeaders? headers,
        Exception? inner = null)
    {
        var category = status switch
        {
            0 => "provider_error",
            401 => "authentication",
            403 => "permission",
            404 => "model_unavailable",
            408 or 504 => "timeout",
            429 => "rate_limit",
            502 or 503 => "provider_error",
            >= 500 => "provider_error",
            _ => "invalid_request"
        };
        var retryable = status is 429 or 502 or 503;
        var certainty = status == 0
            ? OpenAITransportDispatchCertainty.DispatchMayHaveOccurred
            : OpenAITransportDispatchCertainty.ResponseReceived;
        return new OpenAITransportException(category, SafeMessage(category), inner, status, certainty,
            retryable ? ParseRetryAfter(headers) : null);
    }

    private static TimeSpan? ParseRetryAfter(PipelineResponseHeaders? headers)
    {
        if (headers is null || !headers.TryGetValue("retry-after", out var value)) return null;
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0 && seconds <= 60)
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    private static string? GetSafeHeader(PipelineResponseHeaders headers, string name)
    {
        if (!headers.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value) ||
            OpenAIProviderIdentifier.Normalize(value) is not { } safe)
            return null;
        return safe;
    }

    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static bool TryParseUsage(
        JsonElement root,
        out int? inputTokens,
        out int? cachedTokens,
        out int? outputTokens,
        out int? reasoningTokens)
    {
        inputTokens = cachedTokens = outputTokens = reasoningTokens = null;
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !TryRequiredToken(usage, "input_tokens", out var input) ||
            !TryRequiredToken(usage, "output_tokens", out var output) ||
            !TryOptionalNestedToken(usage, "input_tokens_details", "cached_tokens", out var cached) ||
            !TryOptionalNestedToken(usage, "output_tokens_details", "reasoning_tokens", out var reasoning) ||
            cached is { } cachedValue && cachedValue > input)
            return false;
        inputTokens = input;
        cachedTokens = cached;
        outputTokens = output;
        reasoningTokens = reasoning;
        return true;
    }

    private static bool TryRequiredToken(JsonElement value, string property, out int parsed)
    {
        parsed = 0;
        return value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number &&
               item.TryGetInt32(out parsed) && parsed >= 0;
    }

    private static bool TryOptionalNestedToken(
        JsonElement value,
        string detailsProperty,
        string tokenProperty,
        out int? parsed)
    {
        parsed = null;
        if (!value.TryGetProperty(detailsProperty, out var details)) return true;
        if (details.ValueKind is JsonValueKind.Null) return true;
        if (details.ValueKind != JsonValueKind.Object) return false;
        if (!details.TryGetProperty(tokenProperty, out var token)) return true;
        if (token.ValueKind is JsonValueKind.Null) return true;
        if (token.ValueKind != JsonValueKind.Number || !token.TryGetInt32(out var number) || number < 0) return false;
        parsed = number;
        return true;
    }

    private static string SafeMessage(string category) => category switch
    {
        "authentication" => "OpenAI rejected the configured credentials.",
        "permission" => "OpenAI rejected the configured permissions.",
        "model_unavailable" => "The configured OpenAI model is unavailable.",
        "rate_limit" => "OpenAI rate-limited the request.",
        "timeout" => "The OpenAI request timed out.",
        "invalid_request" => "OpenAI rejected the request.",
        _ => "OpenAI could not complete the request."
    };
}
