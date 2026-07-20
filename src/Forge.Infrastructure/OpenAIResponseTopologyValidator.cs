using System.Text;
using System.Text.Json;
using Forge.Core;

namespace Forge.Infrastructure;

public enum OpenAIResponseTopologyFailure
{
    InvalidResponseIdentity,
    IncompleteMaxOutputTokens,
    IncompleteContentFilter,
    Incomplete,
    Empty,
    Refusal,
    Unexpected
}

public sealed class OpenAIResponseTopologyException(OpenAIResponseTopologyFailure failure)
    : Exception("OpenAI returned a response that did not satisfy the required topology.")
{
    public OpenAIResponseTopologyFailure Failure { get; } = failure;
}

public static class OpenAIProviderIdentifier
{
    public const int MaximumLength = 256;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value[0] == '.' || value.Contains("..", StringComparison.Ordinal) ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ||
            SensitiveContentDetector.ContainsSensitiveValue(value))
            return null;
        return value;
    }
}

public static class OpenAIResponseTopologyValidator
{
    public static string RequireSingleOutputText(OpenAIResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (OpenAIProviderIdentifier.Normalize(response.ResponseId) is null)
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.InvalidResponseIdentity);
        if (response.Status == OpenAIResponseStatus.Incomplete)
            throw new OpenAIResponseTopologyException(response.IncompleteReason switch
            {
                OpenAIResponseIncompleteReason.MaxOutputTokens => OpenAIResponseTopologyFailure.IncompleteMaxOutputTokens,
                OpenAIResponseIncompleteReason.ContentFilter => OpenAIResponseTopologyFailure.IncompleteContentFilter,
                _ => OpenAIResponseTopologyFailure.Incomplete
            });
        if (response.Status != OpenAIResponseStatus.Completed)
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Incomplete);

        var items = response.OutputItems ?? throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Unexpected);
        if (items.Any(item => item.Kind is OpenAIResponseOutputItemKind.Tool or OpenAIResponseOutputItemKind.Unknown))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Unexpected);
        var messages = items.Where(item => item.Kind == OpenAIResponseOutputItemKind.Message).ToArray();
        if (messages.Length == 0)
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Empty);
        if (messages.Length != 1 || !string.Equals(messages[0].Role, "assistant", StringComparison.Ordinal))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Unexpected);
        if (messages[0].Content.Any(part => part.Kind == OpenAIResponseContentKind.Refusal))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Refusal);
        if (messages[0].Content.Any(part => part.Kind == OpenAIResponseContentKind.Unknown))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Unexpected);
        var texts = messages[0].Content.Where(part => part.Kind == OpenAIResponseContentKind.OutputText).ToArray();
        if (texts.Length == 0 || string.IsNullOrWhiteSpace(texts[0].Text))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Empty);
        if (texts.Length != 1 || !string.Equals(texts[0].Text, response.OutputText, StringComparison.Ordinal))
            throw new OpenAIResponseTopologyException(OpenAIResponseTopologyFailure.Unexpected);
        return texts[0].Text;
    }
}

public static class StrictJsonDuplicatePropertyValidator
{
    public static void RejectDuplicates(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] utf8;
        try { utf8 = new UTF8Encoding(false, true).GetBytes(json); }
        catch (EncoderFallbackException exception) { throw new JsonException("JSON was not valid Unicode text.", exception); }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                     (objects.Count == 0 || !objects.Peek().Add(reader.GetString() ?? string.Empty)))
                throw new JsonException("JSON contained a duplicate property name.");
        }
    }
}
