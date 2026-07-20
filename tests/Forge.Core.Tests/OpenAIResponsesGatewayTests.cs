#pragma warning disable OPENAI001
using Forge.Infrastructure;
using OpenAI.Responses;

namespace Forge.Core.Tests;

public sealed class OpenAIResponsesGatewayTests
{
    [Fact]
    public void Installed_sdk_response_status_values_map_to_normalized_boundary()
    {
        Assert.Equal(OpenAIResponseStatus.Completed, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Completed));
        Assert.Equal(OpenAIResponseStatus.Incomplete, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Incomplete));
        Assert.Equal(OpenAIResponseStatus.Failed, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Failed));
        Assert.Equal(OpenAIResponseStatus.Cancelled, SdkOpenAIResponsesGateway.NormalizeStatus(ResponseStatus.Cancelled));
    }

    [Fact]
    public void Installed_sdk_incomplete_reasons_map_distinctly()
    {
        Assert.Equal(OpenAIResponseIncompleteReason.MaxOutputTokens,
            SdkOpenAIResponsesGateway.NormalizeIncompleteReason(ResponseIncompleteStatusReason.MaxOutputTokens));
        Assert.Equal(OpenAIResponseIncompleteReason.ContentFilter,
            SdkOpenAIResponsesGateway.NormalizeIncompleteReason(ResponseIncompleteStatusReason.ContentFilter));
    }

    [Fact]
    public void Raw_sdk_envelope_is_normalized_without_retaining_the_body()
    {
        var body = BinaryData.FromString("""
            {
              "id":"resp_safe","status":"completed",
              "output":[
                {"type":"reasoning"},
                {"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"ok\":true}"}]}
              ],
              "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":2},"output_tokens":4,"output_tokens_details":{"reasoning_tokens":1}}
            }
            """);

        var result = SdkOpenAIResponsesGateway.ParseResponse(body, "request-safe");

        Assert.Equal("resp_safe", result.ResponseId);
        Assert.Equal("{\"ok\":true}", result.OutputText);
        Assert.Equal("request-safe", result.ProviderRequestId);
        Assert.Equal(2, result.OutputItems?.Count);
        Assert.Equal(OpenAIResponseOutputItemKind.Reasoning, result.OutputItems?[0].Kind);
        Assert.Equal(OpenAIResponseOutputItemKind.Message, result.OutputItems?[1].Kind);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(2, result.CachedInputTokens);
        Assert.Equal(1, result.ReasoningTokens);
        Assert.True(result.UsageAvailable);
    }

    [Fact]
    public void Missing_usage_and_refusal_remain_truthful_normalized_values()
    {
        var body = BinaryData.FromString("""
            {"id":"resp_refusal","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"declined"}]}]}
            """);

        var result = SdkOpenAIResponsesGateway.ParseResponse(body, null);

        Assert.False(result.UsageAvailable);
        Assert.Equal(OpenAIResponseContentKind.Refusal, Assert.Single(Assert.Single(result.OutputItems!).Content).Kind);
        Assert.Empty(result.OutputText);
    }

    public static TheoryData<string> UnavailableUsage => new()
    {
        "null",
        "{}",
        "{\"input_tokens\":1}",
        "{\"output_tokens\":1}",
        "{\"input_tokens\":null,\"output_tokens\":1}",
        "{\"input_tokens\":1,\"output_tokens\":null}",
        "{\"input_tokens\":\"1\",\"output_tokens\":1}",
        "{\"input_tokens\":1.5,\"output_tokens\":1}",
        "{\"input_tokens\":-1,\"output_tokens\":1}",
        "{\"input_tokens\":2147483648,\"output_tokens\":1}",
        "{\"input_tokens\":1,\"output_tokens\":1,\"input_tokens_details\":{\"cached_tokens\":-1}}",
        "{\"input_tokens\":1,\"output_tokens\":1,\"output_tokens_details\":{\"reasoning_tokens\":1.5}}"
    };

    [Theory]
    [MemberData(nameof(UnavailableUsage))]
    public void Partial_or_malformed_usage_is_unavailable_without_zero_coercion(string usage)
    {
        var result = ParseUsage(usage);

        Assert.False(result.UsageAvailable);
        Assert.Null(result.InputTokens);
        Assert.Null(result.CachedInputTokens);
        Assert.Null(result.OutputTokens);
        Assert.Null(result.ReasoningTokens);
    }

    [Fact]
    public void Valid_required_usage_without_optional_details_remains_available_with_null_optional_counters()
    {
        var result = ParseUsage("{\"input_tokens\":12,\"output_tokens\":7}");

        Assert.True(result.UsageAvailable);
        Assert.Equal(12, result.InputTokens);
        Assert.Null(result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Null(result.ReasoningTokens);
    }

    [Fact]
    public void Complete_valid_usage_is_preserved()
    {
        var result = ParseUsage("{\"input_tokens\":12,\"output_tokens\":7," +
                                "\"input_tokens_details\":{\"cached_tokens\":3}," +
                                "\"output_tokens_details\":{\"reasoning_tokens\":2}}");

        Assert.True(result.UsageAvailable);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(3, result.CachedInputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(2, result.ReasoningTokens);
    }

    [Fact]
    public void Statusless_transport_errors_are_non_retryable_unless_pre_dispatch_is_proven()
    {
        Assert.False(new OpenAITransportException("provider_error", "safe").Retryable);
        Assert.False(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DispatchMayHaveOccurred).Retryable);
        Assert.False(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.ResponseReceived).Retryable);
        Assert.True(new OpenAITransportException("provider_error", "safe",
            dispatchCertainty: OpenAITransportDispatchCertainty.DefinitelyBeforeRequestDispatch).Retryable);
    }

    [Fact]
    public void Sdk_http_request_exception_is_ambiguous_and_non_retryable()
    {
        var failure = SdkOpenAIResponsesGateway.CreateHttpRequestFailure(
            new HttpRequestException("connection reset after dispatch may have occurred"));

        Assert.Equal(OpenAITransportDispatchCertainty.DispatchMayHaveOccurred, failure.DispatchCertainty);
        Assert.False(failure.Retryable);
        Assert.Equal("OpenAI could not complete the request.", failure.Message);
    }

    private static OpenAIResponseEnvelope ParseUsage(string usage) =>
        SdkOpenAIResponsesGateway.ParseResponse(BinaryData.FromString($$"""
            {
              "id":"resp_usage","status":"completed",
              "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{}"}]}],
              "usage":{{usage}}
            }
            """), null);
}
